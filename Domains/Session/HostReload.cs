// Host half of a party wipe: replicate what vanilla does after a game over (ResetGame.ResetNow, then the
// title screen's Continue = LoadMenuUI.confirmLoading) without going through the title screen:
//   Save    SProgress.Load(slot) -> scene recorded in the save ("SceneName"); ResetNow; SaveManager.slotID = slot;
//           SaveManager.loading = true; AsyncLoader.LoadLevel(scene). The new scene's LoadingManager then runs
//           SaveManager.Load() (it does nothing unless loading == true), restores bag/stats/flags and puts the
//           player on the save point, and clears loading.
//   NewGame No usable save (new game that was never saved, or an empty slot): the vanilla new-game path,
//           ResetNow + SaveManager.NewGame() + AsyncLoader.LoadLevel(<start scene the game loaded after NewGame>).
//   Retry   Not even a recorded start scene (e.g. session began on a cheat-teleported level): reload the current
//           scene. The host resets what clients reset (floor drops, pickup claims) and gets its bag-at-down back, so
//           nobody loses items and the world matches on every peer; the wipe degrades to a full-HP retry.
// NewGame is only chosen for a run that really is fresh (never saved / loaded); a stale start scene must never
// destroy a saved run.
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
        /// <summary>Non-transient time (LoadingScreen does not count) a reload may take before it is retried / abandoned.</summary>
        private const float PendingTimeout = 45f;
        /// <summary>Absolute cap, loading screen included: a reload that never leaves the LoadingScreen is abandoned.</summary>
        private const float HardTimeout = 240f;

        private static bool _pending;
        private static bool _wipeReload;
        private static bool _setLoading;
        private static float _startedAt;
        private static float _issuedAt;
        private static bool _retried;
        private static BagEntry[] _retryBag;
        private static Mode _mode;
        private static string _targetScene = "";

        // Run origin + start scene: per-process, deliberately not reset by StopNetwork (the run outlives the session).
        // Run origin, tracked even in solo (two bool writes, no IO): a fresh run must never reload a stale slot.
        private static bool _freshRun;
        private static bool _newGameArmed;
        private static string _newGameScene = "";
        // Scene the run's slot last saved / loaded (read from SProgress in memory at that moment: a disk read
        // at wipe time would clobber the live progress when the wipe ends up as a Retry).
        private static string _savedScene = "";
        private static int _savedSlot;

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
            NoteSavedScene();
        }

        private static void NoteSavedScene()
        {
            try
            {
                _savedSlot = SaveManager.slotID;
                _savedScene = SProgress.GetString(SceneKey, "") ?? "";
            }
            catch (Exception ex) { Guard.Swallow("HostReload.NoteSavedScene", ex); }
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
        /// Session boundary (start / stop): forget every pending-reload fact. A load already issued still completes
        /// natively (SaveManager.loading is left as set, so its Load runs); what goes is the party bookkeeping around it:
        /// the pending gate (held dumps of a new session), the watchdog re-issue / abort, the Retry bag restore.
        /// </summary>
        public static void Reset()
        {
            _pending = false;
            _wipeReload = false;
            _setLoading = false;
            _retried = false;
            _retryBag = null;
            _mode = Mode.None;
            _targetScene = "";
            _startedAt = 0f;
            _issuedAt = 0f;
        }

        // ------------------------------------------------------------------ host: start the reload

        public static Plan TryBegin()
        {
            var plan = new Plan { Scene = "", Mode = Mode.None };
            if (_pending) return plan;
            // The gate would swallow our LoadLevel (GateLevel -> false) after ResetNow already emptied the host.
            if (SceneFollowService.LoadsSuppressed)
            {
                PlaytestLog.Warn("Damage", "wipe: scene loads are suppressed - retry later");
                return plan;
            }

            int slot = SafeSlot();
            string scene = "";
            var mode = Mode.None;

            if (!_freshRun && slot > 0)
            {
                string saved = SavedSceneFor(slot);
                if (IsLoadable(saved))
                {
                    scene = saved;
                    mode = Mode.Save;
                }
            }
            // NewGame wipes the run: only for a run that has no save at all.
            if (mode == Mode.None && _freshRun && IsLoadable(_newGameScene))
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

            string bagSource;
            _retryBag = mode == Mode.Retry ? PartySaveService.ResolveWipeBag(default(PartySaveToken), out bagSource) : null;
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
            _issuedAt = _startedAt;
            _retried = false;
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
            NoteSavedScene();
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
                else if (NetGate.Host)
                {
                    ResetRetryWorldState(ModRuntime.Network);
                    PlaytestLog.Event("Damage", "wipe reload Retry arrived '" + scene + "' (drops/claims reset, bag restored)");
                }
                if (wipe && NetGate.Host)
                    SessionReset.RunAll(SessionReset.ReasonWipe);
            }
            catch (Exception ex) { Guard.Swallow("HostReload.OnSceneArrived", ex); }
        }

        /// <summary>
        /// Retry wipe, host: the same local reset clients run (floor drops, claims) and the bag it held when it went
        /// down (drops may have taken it off the floor-dropped bag; ClearAll would destroy it). Ring and story untouched.
        /// </summary>
        private static void ResetRetryWorldState(LanNetworkManager net)
        {
            DroppedItemManager.ClearAll();
            ItemPickupPatches.ResetDropClaims();
            net?.PickupSync.Reset();
            var bag = _retryBag;
            _retryBag = null;
            if (bag != null) PartySaveService.RestoreBag(bag);
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

        /// <summary>
        /// Per-frame watchdog. Only non-transient time counts (a cold-disk load sits in LoadingScreen for a long
        /// time). A reload whose Load / arrival never happened is retried once; only then abandoned, so the host is
        /// never left reset (ResetNow already ran) without a restore if it can be helped.
        /// </summary>
        public static void Tick()
        {
            if (!_pending) return;
            float now = Time.unscaledTime;
            if (SceneFollowService.LocalIsTransient())
            {
                _startedAt = now;
                if (now - _issuedAt < HardTimeout) return;
                PlaytestLog.Warn("Damage", "wipe reload " + _mode + " stuck in the loading screen - giving up");
                Abort("hard timeout");
                return;
            }
            if (now - _startedAt < PendingTimeout) return;
            if (_mode != Mode.Retry && !_retried && ReissueLoad()) return;
            PlaytestLog.Warn("Damage", "wipe reload " + _mode + " never completed - giving up");
            Abort("timeout");
        }

        /// <summary>Second (and last) attempt at the load the watchdog saw not complete.</summary>
        private static bool ReissueLoad()
        {
            _retried = true;
            PlaytestLog.Warn("Damage", "wipe reload " + _mode + " '" + _targetScene + "' did not complete - retrying once");
            try
            {
                if (_mode == Mode.Save)
                {
                    SaveManager.loading = true;
                    _setLoading = true;
                }
                AsyncLoader.LoadLevel(_targetScene);
                _startedAt = Time.unscaledTime;
                return true;
            }
            catch (Exception ex)
            {
                Guard.Swallow("HostReload.Reissue", ex);
                return false;
            }
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
            _retryBag = null;
            PlaytestLog.Event("Damage", "wipe reload aborted (" + why + ")");
            // Dumps held back while pending must not stay queued forever (the world is whatever it is now).
            try { ModRuntime.Network?.SessionHandlers.DeferDump(-1); }
            catch (Exception ex) { Guard.Swallow("HostReload.abortDump", ex); }
        }

        // ------------------------------------------------------------------ helpers

        private static int SafeSlot()
        {
            try { return SaveManager.slotID; }
            catch (Exception ex) { Guard.Swallow("HostReload.slotID", ex); return 0; }
        }

        /// <summary>
        /// The scene name the slot's last save recorded ("" when unknown). Memory first (captured at the last Save /
        /// Load of this slot). Only when this process never saved or loaded the slot is the file read, which
        /// overwrites the live SProgress - acceptable there: nothing of this run is in memory to lose.
        /// </summary>
        private static string SavedSceneFor(int slot)
        {
            if (_savedSlot == slot && !string.IsNullOrEmpty(_savedScene)) return _savedScene;
            try
            {
                SProgress.Load(slot);
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
