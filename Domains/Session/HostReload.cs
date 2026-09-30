// Host half of a party wipe: replicate what vanilla does after a game over (ResetGame.ResetNow, then the
// title screen's Continue = LoadMenuUI.confirmLoading) without going through the title screen:
//   Save    SProgress.Load(slot) -> scene recorded in the save ("SceneName"); ResetNow; SaveManager.slotID = slot;
//           SaveManager.loading = true; AsyncLoader.LoadLevel(scene). The new scene's LoadingManager then runs
//           SaveManager.Load() (it does nothing unless loading == true), restores bag/stats/flags and puts the
//           player on the save point, and clears loading.
//   NewGame No usable save (new game that was never saved, or an empty slot): the vanilla new-game path,
//           ResetNow + SaveManager.NewGame() + AsyncLoader.LoadLevel(<start scene the game loaded after NewGame>).
//   Retry   Not even a recorded start scene (e.g. session began on a cheat-teleported level): reload the current
//           scene, nothing is reset, nobody loses items; the wipe degrades to a full-HP retry.
// Clients are dragged by SceneFollow: the host's AsyncLoader.LoadLevel goes through SceneLoadGate, which
// broadcasts SceneFollow. Ring / floor-drop / claim resets run only once SaveManager.Load actually executed
// (DeathPatches postfix, guarded by "loading was true on entry"), via OnLoadFinished.
using System;
using SyncRADation.ItemSystem;
using SyncRADation.Patches;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    public static class HostReload
    {
        public enum Mode { None = 0, Save = 1, NewGame = 2, Retry = 3 }

        public struct Plan
        {
            public bool Started;
            public Mode Mode;
            public string Scene;
            public int Slot;
        }

        /// <summary>SProgress string key SaveManager.Save writes the active scene name to (string literal 8527).</summary>
        private const string SceneKey = "SceneName";
        private const float PendingTimeout = 45f;

        private static bool _pending;
        private static bool _wipeReload;
        private static bool _setLoading;
        private static float _startedAt;
        private static Mode _mode;
        private static string _targetScene = "";

        // Run origin + start scene: per-process, deliberately not reset by StopNetwork (the run outlives the session).
        // Run origin, tracked even in solo (two bool writes, no IO): a fresh run must never reload a stale slot.
        private static bool _freshRun;
        private static bool _newGameArmed;
        private static string _newGameScene = "";

        /// <summary>A reload was started and SaveManager.Load has not run yet.</summary>
        public static bool Pending => _pending;

        public static string Describe => _pending ? _mode + " reload pending" : "idle";

        // ------------------------------------------------------------------ run-origin tracking (all roles)

        /// <summary>SaveManager.NewGame ran: this run has no save yet and its start scene is whatever loads next.</summary>
        public static void NoteNewGame()
        {
            _freshRun = true;
            _newGameArmed = true;
        }

        /// <summary>A native Save or Load happened: slotID now names this run's save.</summary>
        public static void NoteSlotBound()
        {
            _freshRun = false;
        }

        /// <summary>AsyncLoader.LoadLevel(string) postfix: the first real level load after NewGame is the new-game start.</summary>
        public static void NoteLevelLoad(string target)
        {
            if (!_newGameArmed) return;
            if (string.IsNullOrEmpty(target) || SceneFollowService.IsMenuScene(target)
                || SceneFollowService.IsTransient(target))
                return;
            _newGameArmed = false;
            _newGameScene = target;
        }

        /// <summary>
        /// Session boundary (StopNetwork): the party-wipe bookkeeping is over, but a reload already under way is the
        /// host's own game load and must still complete (the watchdog in Tick clears a stuck one).
        /// </summary>
        public static void Reset()
        {
            _wipeReload = false;
        }

        // ------------------------------------------------------------------ host: start the reload

        public static Plan TryBegin()
        {
            var plan = new Plan { Scene = "", Mode = Mode.None };
            if (_pending) return plan;

            int slot = SafeSlot();
            string scene = "";
            var mode = Mode.None;

            if (!_freshRun && slot > 0)
            {
                string saved = ReadSavedScene(slot);
                if (IsLoadable(saved))
                {
                    scene = saved;
                    mode = Mode.Save;
                }
            }
            if (mode == Mode.None && IsLoadable(_newGameScene))
            {
                scene = _newGameScene;
                mode = Mode.NewGame;
            }
            if (mode == Mode.None)
            {
                string here = ActiveScene();
                if (IsLoadable(here))
                {
                    scene = here;
                    mode = Mode.Retry;
                }
            }
            if (mode == Mode.None)
            {
                PlaytestLog.Warn("Damage", "wipe: no scene to reload (no save, no start scene, menu scene active)");
                return plan;
            }

            try
            {
                if (mode != Mode.Retry)
                {
                    // Vanilla order: game over -> ResetNow -> (menu) -> NewGame | loading flag -> LoadLevel.
                    NetGate.BeginApply();
                    try { ResetGame.ResetNow(); }
                    catch (Exception ex) { Guard.Swallow("HostReload.ResetNow", ex); }
                    finally { NetGate.EndApply(); }
                }
                if (mode == Mode.Save)
                {
                    SaveManager.slotID = slot;
                    SaveManager.loading = true;
                    _setLoading = true;
                }
                else if (mode == Mode.NewGame)
                {
                    NetGate.BeginApply();
                    try { SaveManager.NewGame(); }
                    finally { NetGate.EndApply(); }
                }
                // Not inside BeginApply: SceneLoadGate must see the host load and broadcast SceneFollow.
                AsyncLoader.LoadLevel(scene);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[Damage] host reload (" + mode + " '" + scene + "') failed: " + ex.Message);
                Abort("LoadLevel threw");
                return plan;
            }

            _pending = true;
            _wipeReload = true;
            _mode = mode;
            _targetScene = scene;
            _startedAt = Time.unscaledTime;
            plan.Started = true;
            plan.Mode = mode;
            plan.Scene = scene;
            plan.Slot = slot;
            PlaytestLog.Event("Damage", "wipe reload " + mode + " scene='" + scene + "'"
                + (mode == Mode.Save ? " slot=" + slot : ""));
            return plan;
        }

        // ------------------------------------------------------------------ load hooks (DeathPatches)

        /// <summary>SaveManager.Load prefix: remember whether this is a real load (native Load is a no-op otherwise).</summary>
        public static bool LoadWillRun()
        {
            try { return SaveManager.loading; }
            catch (Exception ex) { Guard.Swallow("HostReload.loading", ex); return false; }
        }

        /// <summary>SaveManager.Load postfix with loading == true on entry. True when this load finished a wipe reload.</summary>
        public static bool OnLoadFinished()
        {
            bool wipe = _wipeReload;
            _pending = false;
            _wipeReload = false;
            _setLoading = false;
            _freshRun = false;
            return wipe;
        }

        /// <summary>
        /// ModRuntime.OnSceneChanged: the NewGame / Retry reloads never run SaveManager.Load (loading stays false), so
        /// the target scene arriving is their completion signal. Save reloads complete in OnLoadFinished instead.
        /// </summary>
        public static void OnSceneArrived(string scene)
        {
            if (!_pending || _mode == Mode.Save) return;
            if (!string.Equals(scene, _targetScene, StringComparison.Ordinal)) return;
            var mode = _mode;
            bool wipe = _wipeReload;
            _pending = false;
            _wipeReload = false;
            try
            {
                if (mode == Mode.NewGame && NetGate.Host)
                {
                    ResetHostWorldState(ModRuntime.Network); // fresh run: Current is invalid, so the ring becomes empty
                    PlaytestLog.Event("Damage", "wipe reload NewGame arrived '" + scene + "'");
                }
                else
                {
                    PlaytestLog.Event("Damage", "wipe reload Retry arrived '" + scene + "' (nothing reset)");
                }
                if (wipe && NetGate.Host)
                    SessionReset.RunAll(SessionReset.ReasonWipe);
            }
            catch (Exception ex) { Guard.Swallow("HostReload.OnSceneArrived", ex); }
        }

        /// <summary>
        /// Host, after the save (or a new game) really loaded: the party key ring becomes the save's snapshot (empty
        /// without one) and floor drops / pickup claims are dropped, so the world does not keep state the save never had.
        /// </summary>
        public static void ResetHostWorldState(LanNetworkManager net)
        {
            PartyKeyRing.Import(PartySaveService.RingForCurrent());
            PartyKeyRing.Broadcast();
            DroppedItemManager.ClearAll();
            ItemPickupPatches.ResetDropClaims();
            net?.PickupSync.Reset();
        }

        /// <summary>Per-frame watchdog: a reload whose Load never ran must not leave loading == true behind.</summary>
        public static void Tick()
        {
            if (!_pending) return;
            if (Time.unscaledTime - _startedAt < PendingTimeout) return;
            PlaytestLog.Warn("Damage", "wipe reload " + _mode + " never reached SaveManager.Load - giving up");
            Abort("timeout");
        }

        private static void Abort(string why)
        {
            if (_setLoading)
            {
                try { SaveManager.loading = false; }
                catch (Exception ex) { Guard.Swallow("HostReload.abort", ex); }
            }
            _setLoading = false;
            _pending = false;
            _wipeReload = false;
            PlaytestLog.Event("Damage", "wipe reload aborted (" + why + ")");
        }

        // ------------------------------------------------------------------ helpers

        private static int SafeSlot()
        {
            try { return SaveManager.slotID; }
            catch (Exception ex) { Guard.Swallow("HostReload.slotID", ex); return 0; }
        }

        /// <summary>The scene name the slot's last save recorded ("" when the slot is empty).</summary>
        private static string ReadSavedScene(int slot)
        {
            try
            {
                SProgress.Load(slot); // the same call SaveManager.Load starts with; the reload overwrites progress anyway
                return SProgress.GetString(SceneKey, "") ?? "";
            }
            catch (Exception ex)
            {
                Guard.Swallow("HostReload.ReadSavedScene", ex);
                return "";
            }
        }

        private static string ActiveScene()
        {
            try { return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? ""; }
            catch (Exception ex) { Guard.Swallow("HostReload.ActiveScene", ex); return ""; }
        }

        private static bool IsLoadable(string scene)
        {
            if (string.IsNullOrEmpty(scene)) return false;
            if (SceneFollowService.IsTransient(scene) || SceneFollowService.IsMenuScene(scene)) return false;
            try { return Application.CanStreamedLevelBeLoaded(scene); }
            catch (Exception ex)
            {
                Guard.Swallow("HostReload.CanStream", ex);
                return true; // cannot verify: let LoadLevel decide (it throws into TryBegin's catch)
            }
        }
    }
}
