// Host commands chapter loads; clients apply the same AsyncLoader.LoadLevel.
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
                PlaytestLog.Event("Scene", "coalesce load '" + sceneName + "'");
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
                var zones = Object.FindObjectsOfType<LoadLevelZone>();
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
                var loads = Object.FindObjectsOfType<LoadLevelInteraction>();
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
                var helpers = Object.FindObjectsOfType<SceneHelper>();
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
                var air = Object.FindObjectsOfType<PenroseAirlock>();
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
                var doors = Object.FindObjectsOfType<AirlockDoorLoadZone>();
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

        public static bool IsTransient(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return true;
            return string.Equals(sceneName, "LoadingScreen", System.StringComparison.Ordinal);
        }

        public static bool AlreadyGoingTo(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName) || IsTransient(sceneName)) return false;
            if (string.Equals(_pending, sceneName, System.StringComparison.Ordinal)
                && Time.unscaledTime - _pendingAt < InflightWindow)
                return true;
            try
            {
                if (string.Equals(AsyncLoader.targetLevelString, sceneName, System.StringComparison.Ordinal))
                    return true;
            }
            catch { }
            return false;
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
            NetGate.BeginApply();
            try
            {
                try { AsyncLoader.LoadLevel(sceneName); return; } catch { }
                try
                {
                    var helpers = Object.FindObjectsOfType<SceneHelper>();
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
