// Scene follow: the host owns chapter / scene loads. A host load broadcasts SceneFollow and every client loads the same
// scene (Apply); a client's own load becomes a request the host runs (TryApplyRequest) and everyone then follows.
// The wreck / hole split never follows (AirlockCinematic).
using SyncRADation.ItemSystem;
using SyncRADation.Patches;
using SyncRADation.Sync;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SyncRADation.Networking
{
    public static class SceneFollowService
    {
        // Non-level scenes (Pregame): MainMenu / DeadMenu / EndCredits. They are in build settings, so IsKnownScene accepts
        // them and every load path (AsyncLoader, SceneHelper.LoadScene/LoadSceneDirect, NewApplication.LoadLevel,
        // SceneManager.LoadScene) is gated: DeadMenu (LAB_Emptiness cutscene -> LoadSceneDirect) and its buttons (LoadScene
        // MEM_Memory), and the post-credits CreditsEnd (ResetGame.ResetNow + LoadLevel MainMenu) follow the host like a
        // chapter load.
        public const string MainMenuScene = "MainMenu";
        public const string DeadMenuScene = "DeadMenu";
        public const string EndCreditsScene = "EndCredits";

        // A load the host started (or a follower is running) to this scene, and when.
        static string _pending;
        static float _pendingAt;
        // Client: the scene it asked the host for, and when.
        static string _requested;
        static float _requestedAt;
        const float InflightWindow = 12f;

        // Host: the newest peer request that arrived while the host was busy.
        static string _queued;
        static float _queuedAt;
        // The request waits for as long as the host stays busy (dual-box chapter loads and a death / respawn can each
        // run past 30 s); it is only abandoned after this hard cap, and then the requester is pointed back at the
        // host's scene instead of being left hanging.
        const float QueueMaxWait = 90f;

        // Client: re-ask when a blocked / queued request never produced a load.
        const float RetryAfter = 25f;
        const int MaxRetries = 2;
        static int _retries;

        // Client: a host follow that arrived while this player was watching a cutscene. Cutscenes are per player, so a
        // scene-ending one (PEN_CodeRoomEnd -> LOV) is not cut short: the follow runs when it ends (or when its own load
        // fires), capped in case the cutscene never ends.
        static string _heldFollow;
        static float _heldAt;
        const float HoldForCutsceneMax = 180f;

        // Scope: loads are swallowed (even inside IsApplying). Used while a follow tears down a dialogue whose end
        // callbacks could otherwise start a load of their own on the follower.
        static int _suppressLoads;

        // persistent: build-settings scene names by build index, fixed for the process
        static string[] _buildNames;
        // persistent: same names as a set
        static System.Collections.Generic.HashSet<string> _buildSet;

        public static void Reset()
        {
            _pending = null;
            _pendingAt = 0f;
            _requested = null;
            _requestedAt = 0f;
            _queued = null;
            _queuedAt = 0f;
            _heldFollow = null;
            _heldAt = 0f;
            _retries = 0;
            _suppressLoads = 0;
        }

        public static bool LoadsSuppressed => _suppressLoads > 0;
        public static void BeginSuppressLoads() => _suppressLoads++;
        public static void EndSuppressLoads() { if (_suppressLoads > 0) _suppressLoads--; }

        // ------------------------------------------------------------------ scene names

        static string ActiveScene()
        {
            try { return SceneManager.GetActiveScene().name ?? ""; }
            catch (System.Exception ex) { PlaytestLog.Warn("Scene", "active scene: " + ex.Message); return ""; }
        }

        public static bool IsTransient(string sceneName)
        {
            return string.IsNullOrEmpty(sceneName) || string.Equals(sceneName, "LoadingScreen", System.StringComparison.Ordinal);
        }

        public static bool LocalIsTransient() => IsTransient(ActiveScene());

        /// <summary>
        /// "MainMenu" (SceneHelper.resetGame, StringLiteral_13696 -> index 13695) or "MainMenu2" (CreditsEnd,
        /// StringLiteral_13714 -> index 13713): Ghidra string literals are off by one against stringliteral.json.
        /// </summary>
        public static bool IsMainMenu(string sceneName)
        {
            return !string.IsNullOrEmpty(sceneName) && sceneName.StartsWith(MainMenuScene, System.StringComparison.Ordinal);
        }

        public static bool IsMenuScene(string sceneName)
        {
            return IsMainMenu(sceneName)
                || string.Equals(sceneName, DeadMenuScene, System.StringComparison.Ordinal)
                || string.Equals(sceneName, EndCreditsScene, System.StringComparison.Ordinal)
                || string.Equals(sceneName, "Credits", System.StringComparison.Ordinal);
        }

        static string[] BuildNames()
        {
            if (_buildNames != null) return _buildNames;
            try
            {
                var names = new string[SceneManager.sceneCountInBuildSettings];
                for (int i = 0; i < names.Length; i++)
                    names[i] = SceneFileName(SceneUtility.GetScenePathByBuildIndex(i));
                _buildSet = new System.Collections.Generic.HashSet<string>(names, System.StringComparer.Ordinal);
                _buildNames = names;
            }
            catch (System.Exception e) { Guard.Swallow(e); return new string[0]; }
            return _buildNames;
        }

        static string SceneFileName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            int start = Mathf.Max(path.LastIndexOf('/'), path.LastIndexOf('\\')) + 1;
            int dot = path.LastIndexOf('.');
            return dot <= start ? path.Substring(start) : path.Substring(start, dot - start);
        }

        // Every load path (AsyncLoader, SceneHelper, LoadLevelZone, LoadLevelInteraction, PenroseAirlock,
        // AirlockDoorLoadZone) can only reach a scene in build settings, so that is the whole check.
        static bool IsKnownScene(string sceneName)
        {
            return !IsTransient(sceneName) && BuildNames().Length > 0 && _buildSet.Contains(sceneName);
        }

        public static string ResolveLevelName(int index)
        {
            var names = BuildNames();
            string named = index >= 0 && index < names.Length ? names[index] : "";
            if (!IsTransient(named)) return named;
            try
            {
                string stored = AsyncLoader.targetLevelString;
                if (!IsTransient(stored)) return stored;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return named;
        }

        // ------------------------------------------------------------------ load bookkeeping

        public static bool AlreadyGoingTo(string sceneName)
        {
            // Only our own NoteGoingTo window counts: AsyncLoader.targetLevelString keeps the last native target after a
            // network reset, and trusting it coalesced peer requests and skipped SendSceneFollow for good.
            return !IsTransient(sceneName)
                && string.Equals(_pending, sceneName, System.StringComparison.Ordinal)
                && Time.unscaledTime - _pendingAt < InflightWindow;
        }

        static bool AlreadyRequested(string sceneName)
        {
            return string.Equals(_requested, sceneName, System.StringComparison.Ordinal)
                && Time.unscaledTime - _requestedAt < InflightWindow;
        }

        static void NoteRequested(string sceneName)
        {
            _requested = sceneName;
            _requestedAt = Time.unscaledTime;
        }

        public static void NoteGoingTo(string sceneName)
        {
            if (IsTransient(sceneName)) return;
            _pending = sceneName;
            _pendingAt = Time.unscaledTime;
        }

        public static void NoteArrived(string sceneName)
        {
            if (IsTransient(sceneName)) return;
            if (string.Equals(_pending, sceneName, System.StringComparison.Ordinal))
                _pending = null;
            if (string.Equals(_requested, sceneName, System.StringComparison.Ordinal))
            {
                _requested = null;
                _retries = 0;
            }
        }

        static void SendFollow(string sceneName)
        {
            var net = LanNetworkManager.Instance;
            if (net != null && net.IsConnected) net.SceneHandlers.SendSceneFollow(sceneName, false);
        }

        // ------------------------------------------------------------------ client: request

        public static void RequestFollow(string sceneName)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            if (IsTransient(sceneName) || AlreadyRequested(sceneName) || AlreadyGoingTo(sceneName)) return;
            if (!string.Equals(_requested, sceneName, System.StringComparison.Ordinal)) _retries = 0;
            NoteRequested(sceneName);
            net.SceneHandlers.SendSceneFollow(sceneName, true);
        }

        /// <summary>This player is watching a cutscene (native gameState cutscene): a level change waits for it.</summary>
        public static bool InLocalCutscene()
        {
            try { return PlayerState.player != null && PlayerState.gameState == PlayerState.gameStates.cutscene; }
            catch (System.Exception e) { Guard.Swallow(e); return false; }
        }

        /// <summary>
        /// Client: this player's own cutscene ended in a load of the scene a held follow points at (the host is there
        /// already): run the follow now instead of asking the host. False when nothing is held for that scene.
        /// </summary>
        public static bool TryReleaseHeldFollow(string sceneName)
        {
            if (string.IsNullOrEmpty(_heldFollow) || !string.Equals(_heldFollow, sceneName, System.StringComparison.Ordinal))
                return false;
            _heldFollow = null;
            PlaytestLog.Event("Scene", "cutscene over, follow '" + sceneName + "'");
            Apply(sceneName);
            return true;
        }

        /// <summary>Client tick: run a held follow once the cutscene is over; re-ask when a blocked / queued request never produced a load.</summary>
        public static void TickClient()
        {
            if (!string.IsNullOrEmpty(_heldFollow)
                && (!InLocalCutscene() || Time.unscaledTime - _heldAt > HoldForCutsceneMax))
            {
                string held = _heldFollow;
                _heldFollow = null;
                PlaytestLog.Event("Scene", "cutscene over, follow '" + held + "'");
                Apply(held);
            }
            if (string.IsNullOrEmpty(_requested)) return;
            var net = LanNetworkManager.Instance;
            if (!NetGate.Client) return;
            if (Time.unscaledTime - _requestedAt < RetryAfter) return;
            string scene = _requested;
            string here = ActiveScene();
            if (string.Equals(here, scene, System.StringComparison.Ordinal) || IsTransient(here))
            {
                _requested = null;
                _retries = 0;
                return;
            }
            if (_retries >= MaxRetries)
            {
                PlaytestLog.Warn("Scene", "gave up re-requesting '" + scene + "'");
                _requested = null;
                _retries = 0;
                return;
            }
            _retries++;
            NoteRequested(scene);
            PlaytestLog.Event("Scene", "re-request '" + scene + "' (attempt " + (_retries + 1) + ")");
            net.SceneHandlers.SendSceneFollow(scene, true);
        }

        // ------------------------------------------------------------------ host: a peer's request

        public static bool TryApplyRequest(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return false;
            if (!IsKnownScene(sceneName))
            {
                PlaytestLog.Warn("Scene", "unknown '" + sceneName + "'");
                return false;
            }
            string here = ActiveScene();
            if (AirlockCinematic.IsWreckHoleSplit(here, sceneName))
            {
                PlaytestLog.Event("Scene", "reject peer '" + sceneName + "' (airlock split, host='" + here + "')");
                return true;
            }
            bool alreadyHere = string.Equals(here, sceneName, System.StringComparison.Ordinal);
            if (AirlockCinematic.DeferFollowWhileAirlockPresent())
            {
                if (alreadyHere) return true;
                PlaytestLog.Event("Scene", "reject peer '" + sceneName + "' (host airlock cinematic)");
                return false;
            }
            if (IsMainMenu(sceneName) && string.Equals(here, EndCreditsScene, System.StringComparison.Ordinal))
            {
                // CreditsEnd runs ResetGame + LoadLevel(MainMenu) on every peer when *its* credits finish. A client that
                // is faster must not cut the host's credits short: the host's own load follows.
                PlaytestLog.Event("Scene", "ignore peer '" + sceneName + "' (host still in credits)");
                return true;
            }
            if (alreadyHere)
            {
                SendFollow(sceneName);
                return true;
            }
            if (AlreadyGoingTo(sceneName))
            {
                // Still emit follow so a late peer request during the host load is answered.
                PlaytestLog.Event("Scene", "coalesce load '" + sceneName + "'");
                SendFollow(sceneName);
                return true;
            }
            string busy = HostBusyReason(sceneName, here);
            if (busy != null)
            {
                // A different-scene request while the host is mid-load / loading / dying must not start a second
                // load on top of the first (two peers taking two different doors). Keep only the newest request
                // and run it once the host has arrived and is alive again (Tick).
                QueueRequest(sceneName, busy);
                return true;
            }
            // A peer's request drags the host out of whatever it was doing: same sticky-state teardown as a follower's
            // Apply (open inventory / menu / dialogue / cutscene state would survive the load).
            try { DroppedItemRegistry.RestorePlayForLoad(); }
            catch (System.Exception ex) { PlaytestLog.Warn("Scene", "RestorePlayForLoad: " + ex.Message); }
            try
            {
                // The host's own load gate (SceneLoadGate) notes the target and broadcasts the follow.
                AsyncLoader.LoadLevel(sceneName);
                return true;
            }
            catch (System.Exception ex) { PlaytestLog.Warn("Scene", "peer LoadLevel failed: " + ex.Message); }
            Apply(sceneName);
            SendFollow(sceneName);
            return true;
        }

        static bool HostDead(string here)
        {
            // Menu scenes keep the last hp / charState statics, and non-gameplay scenes (profile select, calibration)
            // have no live player object: stale statics there are not "the host is dying".
            if (IsMenuScene(here)) return false;
            try
            {
                if (PlayerState.player == null) return false;
                return PlayerState.charState == PlayerState.charStates.dead || PlayerState.hp <= 0;
            }
            catch (System.Exception ex) { PlaytestLog.Warn("Scene", "host vitals: " + ex.Message); }
            return false;
        }

        /// <summary>Why the host cannot start a load for a peer request right now, or null when it can.</summary>
        static string HostBusyReason(string sceneName, string here)
        {
            if (IsTransient(here)) return "host loading";
            if (!string.IsNullOrEmpty(_pending) && !string.Equals(_pending, sceneName, System.StringComparison.Ordinal)
                && Time.unscaledTime - _pendingAt < InflightWindow)
                return "host load in flight to '" + _pending + "'";
            if (HostDead(here)) return "host dead";
            // Cutscenes are per player: a client's request (its scene-ending cutscene finished first) waits for the
            // host's own copy; that usually ends in the same load.
            if (InLocalCutscene()) return "host in a cutscene";
            return null;
        }

        static void QueueRequest(string sceneName, string reason)
        {
            PlaytestLog.Event("Scene", "queue peer '" + sceneName + "' (" + reason + ")");
            // The cap is absolute: a client re-requesting every ~25 s replaces the scene but must not refresh the clock,
            // or the queue would never expire while the requester keeps retrying.
            if (string.IsNullOrEmpty(_queued)) _queuedAt = Time.unscaledTime;
            _queued = sceneName;
        }

        /// <summary>Host tick: run the queued peer request once the host has arrived and is alive.</summary>
        public static void Tick()
        {
            if (string.IsNullOrEmpty(_queued)) return;
            var net = LanNetworkManager.Instance;
            if (!NetGate.Host || !NetGate.Party)
            {
                _queued = null; // offline, or the requester is gone
                return;
            }
            if (Time.unscaledTime - _queuedAt > QueueMaxWait)
            {
                PlaytestLog.Event("Scene", "drop stale queued peer '" + _queued + "'");
                _queued = null;
                // Tell everyone (the requester included) which scene the host is actually in.
                string here = ActiveScene();
                if (!IsTransient(here)) net.SceneHandlers.SendSceneFollow(here, false);
                return;
            }
            if (HostBusyReason(_queued, ActiveScene()) != null) return;
            string q = _queued;
            _queued = null;
            PlaytestLog.Event("Scene", "run queued peer '" + q + "'");
            TryApplyRequest(q);
        }

        // ------------------------------------------------------------------ follower

        public static void Apply(string sceneName)
        {
            if (IsTransient(sceneName)) return;
            string cur = ActiveScene();
            if (string.Equals(cur, sceneName, System.StringComparison.Ordinal))
            {
                _pending = null;
                return;
            }
            if (AlreadyGoingTo(sceneName)) return;

            NoteGoingTo(sceneName);
            PlaytestLog.Event("Scene", "follow load '" + sceneName + "' (was '" + cur + "')");
            // An open inventory / menu / dialogue, or a cutscene whose coroutine dies with the unloaded scene, would leave
            // gameState / PlayerState.cutscene / dialogue sticky on the follower: restore play first, like a disconnect.
            try { DroppedItemRegistry.RestorePlayForLoad(); }
            catch (System.Exception ex) { PlaytestLog.Warn("Scene", "RestorePlayForLoad: " + ex.Message); }
            NetGate.BeginApply();
            try
            {
                if (IsMainMenu(sceneName)) ResetBeforeMenu();
                LoadFirstAvailable(sceneName);
            }
            catch (System.Exception ex) { PlaytestLog.Warn("Scene", "load failed: " + ex.Message); }
            finally { NetGate.EndApply(); }
        }

        // SceneHelper.resetGame / CreditsEnd run ResetNow before the menu load; a peer the host drags there never ran it.
        // A failed reset still loads the menu.
        static void ResetBeforeMenu()
        {
            try { ResetGame.ResetNow(); }
            catch (System.Exception ex) { PlaytestLog.Warn("Scene", "ResetGame.ResetNow: " + ex.Message); }
        }

        // Fallback chain: AsyncLoader (loading screen), then the scene's SceneHelper, NewApplication, and plain SceneManager.
        static void LoadFirstAvailable(string sceneName)
        {
            try { AsyncLoader.LoadLevel(sceneName); return; } catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                var helpers = WorldLookup.All<SceneHelper>();
                if (helpers != null && helpers.Length > 0 && helpers[0] != null)
                {
                    helpers[0].LoadScene(sceneName);
                    return;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try { NewApplication.LoadLevel(sceneName); return; } catch (System.Exception e) { Guard.Swallow(e); }
            SceneManager.LoadScene(sceneName);
        }

        public static void HandleMessage(SceneFollowMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (net == null) return;
            if (msg.IsRequest)
            {
                if (NetGate.HostRole) TryApplyRequest(msg.SceneName);
                return;
            }
            if (NetGate.HostRole) return;
            if (AirlockCinematic.ShouldIgnoreHostFollow(msg.SceneName))
            {
                PlaytestLog.Event("Scene", "ignore follow '" + msg.SceneName + "' (airlock split)");
                return;
            }
            // Not for the scene this player asked for itself (its own cutscene already ended in that load and is
            // waiting on the host; gameState stays cutscene until it leaves).
            if (InLocalCutscene() && !IsMainMenu(msg.SceneName)
                && !string.Equals(ActiveScene(), msg.SceneName, System.StringComparison.Ordinal)
                && !string.Equals(_requested, msg.SceneName, System.StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(_heldFollow)) _heldAt = Time.unscaledTime;
                _heldFollow = msg.SceneName;
                PlaytestLog.Event("Scene", "hold follow '" + msg.SceneName + "' until this cutscene ends");
                return;
            }
            Apply(msg.SceneName);
        }
    }
}
