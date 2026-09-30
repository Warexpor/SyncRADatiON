// Host commands chapter loads; clients apply the same AsyncLoader.LoadLevel.
using SyncRADation.ItemSystem;
using SyncRADation.Patches;
using SyncRADation.Sync;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SyncRADation.Networking
{
    public static class SceneFollowService
    {
        public static void RequestFollow(string sceneName)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            if (string.IsNullOrEmpty(sceneName) || IsTransient(sceneName)) return;
            if (AlreadyRequested(sceneName) || AlreadyGoingTo(sceneName)) return;
            NoteRequested(sceneName);
            net.SendSceneFollow(sceneName, true);
        }

        public static bool TryApplyRequest(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return false;
            if (!IsKnownScene(sceneName))
            {
                PlaytestLog.Warn("Scene", "unknown '" + sceneName + "'");
                return false;
            }
            try
            {
                string here = SceneManager.GetActiveScene().name ?? "";
                if (AirlockCinematic.IsWreckHoleSplit(here, sceneName))
                {
                    PlaytestLog.Event("Scene", "reject peer '" + sceneName
                        + "' (airlock split, host='" + here + "')");
                    return true;
                }
                if (AirlockCinematic.DeferFollowWhileAirlockPresent())
                {
                    if (string.Equals(here, sceneName, System.StringComparison.Ordinal))
                        return true;
                    PlaytestLog.Event("Scene", "reject peer '" + sceneName + "' (host airlock cinematic)");
                    return false;
                }
            }
            catch { }
            if (HostStillInCredits(sceneName))
            {
                // CreditsEnd runs ResetGame + LoadLevel(MainMenu) on every peer when *its* credits finish. A
                // client that is faster must not cut the host's credits short: the host's own load follows.
                PlaytestLog.Event("Scene", "ignore peer '" + sceneName + "' (host still in credits)");
                return true;
            }
            try
            {
                string cur = SceneManager.GetActiveScene().name ?? "";
                if (string.Equals(cur, sceneName, System.StringComparison.Ordinal))
                {
                    var net = LanNetworkManager.Instance;
                    if (net != null && net.IsConnected)
                        net.SendSceneFollow(sceneName, false);
                    return true;
                }
            }
            catch { }
            if (AlreadyGoingTo(sceneName))
            {
                // Still emit follow so a late peer request during host load is not silent.
                PlaytestLog.Event("Scene", "coalesce load '" + sceneName + "'");
                try
                {
                    var net = LanNetworkManager.Instance;
                    if (net != null && net.IsConnected)
                        net.SendSceneFollow(sceneName, false);
                }
                catch { }
                return true;
            }
            string busy = HostBusyReason(sceneName);
            if (busy != null)
            {
                // A different-scene request while the host is mid-load / loading / dying must not start a second
                // load on top of the first (two peers taking two different doors). Keep only the newest request
                // and run it once the host has arrived and is alive again (Tick).
                QueueRequest(sceneName, busy);
                return true;
            }
            try
            {
                AsyncLoader.LoadLevel(sceneName);
                return true;
            }
            catch (System.Exception ex)
            {
                PlaytestLog.Warn("Scene", "peer LoadLevel failed: " + ex.Message);
            }
            Apply(sceneName);
            try
            {
                var net = LanNetworkManager.Instance;
                if (net != null && net.IsConnected)
                    net.SendSceneFollow(sceneName, false);
            }
            catch { }
            return true;
        }

        // --- Non-level scenes (Pregame): MainMenu / DeadMenu / EndCredits -------------------------
        // They are in build settings, so IsKnownScene accepts them and every load path (AsyncLoader,
        // SceneHelper.LoadScene/LoadSceneDirect, NewApplication.LoadLevel, SceneManager.LoadScene) is gated:
        // DeadMenu (LAB_Emptiness cutscene -> LoadSceneDirect) and its buttons (LoadScene MEM_Memory), and the
        // post-credits CreditsEnd (ResetGame.ResetNow + LoadLevel MainMenu) follow the host like a chapter load.
        public const string MainMenuScene = "MainMenu";
        public const string DeadMenuScene = "DeadMenu";
        public const string EndCreditsScene = "EndCredits";

        public static bool IsMenuScene(string sceneName)
        {
            return string.Equals(sceneName, MainMenuScene, System.StringComparison.Ordinal)
                || string.Equals(sceneName, DeadMenuScene, System.StringComparison.Ordinal)
                || string.Equals(sceneName, EndCreditsScene, System.StringComparison.Ordinal)
                || string.Equals(sceneName, "Credits", System.StringComparison.Ordinal);
        }

        static string HostSceneNow()
        {
            try { return SceneManager.GetActiveScene().name ?? ""; }
            catch (System.Exception ex) { PlaytestLog.Warn("Scene", "active scene: " + ex.Message); return ""; }
        }

        static bool HostStillInCredits(string requested)
        {
            return string.Equals(requested, MainMenuScene, System.StringComparison.Ordinal)
                && string.Equals(HostSceneNow(), EndCreditsScene, System.StringComparison.Ordinal);
        }

        static bool HostDead()
        {
            // Menu scenes keep the last hp / charState statics: a dead hp there is not "the host is dying".
            if (IsMenuScene(HostSceneNow())) return false;
            try { if (PlayerState.charState == PlayerState.charStates.dead) return true; }
            catch (System.Exception ex) { PlaytestLog.Warn("Scene", "charState: " + ex.Message); }
            try { if (PlayerState.hp <= 0) return true; }
            catch (System.Exception ex) { PlaytestLog.Warn("Scene", "hp: " + ex.Message); }
            return false;
        }

        static bool LoadInFlight(string exceptScene)
        {
            return !string.IsNullOrEmpty(_pending)
                && !string.Equals(_pending, exceptScene, System.StringComparison.Ordinal)
                && Time.unscaledTime - _pendingAt < InflightWindow;
        }

        /// <summary>Why the host cannot start a load for a peer request right now, or null when it can.</summary>
        static string HostBusyReason(string sceneName)
        {
            if (LocalIsTransient()) return "host loading";
            if (LoadInFlight(sceneName)) return "host load in flight to '" + _pending + "'";
            if (HostDead()) return "host dead";
            return null;
        }

        static string _queued;
        static float _queuedAt;
        const float QueueTtl = 20f;

        static void QueueRequest(string sceneName, string reason)
        {
            PlaytestLog.Event("Scene", "queue peer '" + sceneName + "' (" + reason + ")");
            _queued = sceneName;
            _queuedAt = Time.unscaledTime;
        }

        /// <summary>Host tick: run the queued peer request once the host is arrived / alive.</summary>
        public static void Tick()
        {
            if (string.IsNullOrEmpty(_queued)) return;
            var net = LanNetworkManager.Instance;
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected)
            {
                _queued = null;
                return;
            }
            if (Time.unscaledTime - _queuedAt > QueueTtl)
            {
                PlaytestLog.Event("Scene", "drop stale queued peer '" + _queued + "'");
                _queued = null;
                return;
            }
            if (HostBusyReason(_queued) != null) return;
            string q = _queued;
            _queued = null;
            PlaytestLog.Event("Scene", "run queued peer '" + q + "'");
            TryApplyRequest(q);
        }

        public static bool LocalIsTransient()
        {
            try { return IsTransient(SceneManager.GetActiveScene().name); }
            catch { return false; }
        }

        private static bool IsKnownScene(string sceneName)
        {
            if (IsTransient(sceneName)) return false;
            try
            {
                if (string.Equals(AsyncLoader.targetLevelString, sceneName, System.StringComparison.Ordinal))
                    return true;
            }
            catch { }
            try
            {
                if (string.Equals(NameForBuildIndex(AsyncLoader.targetLevel), sceneName, System.StringComparison.Ordinal))
                    return true;
            }
            catch { }
            try
            {
                var zones = WorldLookup.All<LoadLevelZone>();
                if (zones != null)
                {
                    for (int i = 0; i < zones.Length; i++)
                    {
                        if (zones[i] != null && string.Equals(zones[i].SceneName, sceneName, System.StringComparison.Ordinal))
                            return true;
                    }
                }
            }
            catch { }
            try
            {
                var loads = WorldLookup.All<LoadLevelInteraction>();
                if (loads != null)
                {
                    for (int i = 0; i < loads.Length; i++)
                    {
                        if (loads[i] != null && string.Equals(loads[i].targetLevel, sceneName, System.StringComparison.Ordinal))
                            return true;
                    }
                }
            }
            catch { }
            try
            {
                var helpers = WorldLookup.All<SceneHelper>();
                if (helpers != null)
                {
                    for (int i = 0; i < helpers.Length; i++)
                    {
                        if (helpers[i] == null) continue;
                        if (string.Equals(helpers[i].targetScene, sceneName, System.StringComparison.Ordinal))
                            return true;
                    }
                }
            }
            catch { }
            try
            {
                var air = WorldLookup.All<PenroseAirlock>();
                if (air != null)
                {
                    for (int i = 0; i < air.Length; i++)
                    {
                        if (air[i] == null) continue;
                        if (string.Equals(NameForBuildIndex(air[i].targetLevel), sceneName, System.StringComparison.Ordinal))
                            return true;
                    }
                }
            }
            catch { }
            try
            {
                var doors = WorldLookup.All<AirlockDoorLoadZone>();
                if (doors != null)
                {
                    for (int i = 0; i < doors.Length; i++)
                    {
                        if (doors[i] == null) continue;
                        if (string.Equals(NameForBuildIndex(doors[i].targetLevel), sceneName, System.StringComparison.Ordinal))
                            return true;
                    }
                }
            }
            catch { }
            return InBuildSettings(sceneName);
        }

        static bool InBuildSettings(string sceneName)
        {
            try
            {
                int n = SceneManager.sceneCountInBuildSettings;
                for (int i = 0; i < n; i++)
                {
                    if (string.Equals(NameForBuildIndex(i), sceneName, System.StringComparison.Ordinal))
                        return true;
                }
            }
            catch { }
            return false;
        }

        static string NameForBuildIndex(int index)
        {
            if (index < 0) return "";
            try
            {
                string path = SceneUtility.GetScenePathByBuildIndex(index);
                if (string.IsNullOrEmpty(path)) return "";
                int slash = path.LastIndexOf('/');
                int bs = path.LastIndexOf('\\');
                int start = (slash > bs ? slash : bs) + 1;
                int dot = path.LastIndexOf('.');
                if (dot <= start) return path.Substring(start);
                return path.Substring(start, dot - start);
            }
            catch { return ""; }
        }

        static string _pending;
        static float _pendingAt;
        static string _requested;
        static float _requestedAt;
        const float InflightWindow = 12f;

        public static void Reset()
        {
            _pending = null;
            _pendingAt = 0f;
            _requested = null;
            _requestedAt = 0f;
            _queued = null;
            _queuedAt = 0f;
        }

        public static bool IsTransient(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return true;
            return string.Equals(sceneName, "LoadingScreen", System.StringComparison.Ordinal);
        }

        public static bool AlreadyGoingTo(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName) || IsTransient(sceneName)) return false;
            // Only trust our NoteGoingTo window. AsyncLoader.targetLevelString alone is
            // stale after StopNetwork.Reset while native still holds the last target —
            // that used to coalesce peer requests and skip SendSceneFollow forever.
            return string.Equals(_pending, sceneName, System.StringComparison.Ordinal)
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
            if (string.IsNullOrEmpty(sceneName) || IsTransient(sceneName)) return;
            _pending = sceneName;
            _pendingAt = Time.unscaledTime;
        }

        public static void NoteArrived(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName) || IsTransient(sceneName)) return;
            if (string.Equals(_pending, sceneName, System.StringComparison.Ordinal))
                _pending = null;
            if (string.Equals(_requested, sceneName, System.StringComparison.Ordinal))
                _requested = null;
        }

        public static string ResolveLevelName(int index)
        {
            string named = NameForBuildIndex(index);
            if (!string.IsNullOrEmpty(named) && !IsTransient(named))
                return named;
            try
            {
                string stored = AsyncLoader.targetLevelString;
                if (!string.IsNullOrEmpty(stored) && !IsTransient(stored))
                    return stored;
            }
            catch { }
            return named ?? "";
        }

        public static void Apply(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName) || IsTransient(sceneName)) return;
            string cur = SceneManager.GetActiveScene().name ?? "";
            if (string.Equals(cur, sceneName, System.StringComparison.Ordinal))
            {
                _pending = null;
                return;
            }
            if (AlreadyGoingTo(sceneName))
                return;

            NoteGoingTo(sceneName);
            PlaytestLog.Event("Scene", "follow load '" + sceneName + "' (was '" + cur + "')");
            // Mid-inventory / menu / dialogue sticky: unload alone does not always
            // restore play before AsyncLoader. Mirror disconnect restore (Dig AJ).
            // Also ends an active cutscene / Dialoguer: their coroutines die with the unloaded scene and left
            // gameStates.cutscene / PlayerState.cutscene / dialogue sticky on the follower.
            try { DroppedItemManager.RestorePlayForLoad(); }
            catch (System.Exception ex) { PlaytestLog.Warn("Scene", "RestorePlayForLoad: " + ex.Message); }
            if (string.Equals(sceneName, MainMenuScene, System.StringComparison.Ordinal))
            {
                // SceneHelper.resetGame / CreditsEnd run ResetNow before the menu load; a peer that is dragged
                // there by the host never ran it.
                NetGate.BeginApply();
                try { ResetGame.ResetNow(); }
                catch (System.Exception ex) { PlaytestLog.Warn("Scene", "ResetGame.ResetNow: " + ex.Message); }
                finally { NetGate.EndApply(); }
            }
            NetGate.BeginApply();
            try
            {
                try { AsyncLoader.LoadLevel(sceneName); return; } catch { }
                try
                {
                    var helpers = WorldLookup.All<SceneHelper>();
                    if (helpers != null && helpers.Length > 0 && helpers[0] != null)
                    {
                        helpers[0].LoadScene(sceneName);
                        return;
                    }
                }
                catch { }
                try { NewApplication.LoadLevel(sceneName); return; } catch { }
                SceneManager.LoadScene(sceneName);
            }
            catch (System.Exception ex)
            {
                PlaytestLog.Warn("Scene", "load failed: " + ex.Message);
            }
            finally
            {
                NetGate.EndApply();
            }
        }

        public static void HandleMessage(SceneFollowMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (net == null) return;

            if (msg.IsRequest)
            {
                if (net.Role != NetworkRole.Host) return;
                TryApplyRequest(msg.SceneName);
                return;
            }

            if (net.Role == NetworkRole.Host) return;
            if (AirlockCinematic.ShouldIgnoreHostFollow(msg.SceneName))
            {
                PlaytestLog.Event("Scene", "ignore follow '" + msg.SceneName + "' (airlock split)");
                return;
            }
            Apply(msg.SceneName);
        }
    }
}
