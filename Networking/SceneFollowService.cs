// Host commands chapter loads; clients apply the same AsyncLoader.LoadLevel.
using SyncRADation.Patches;
using SyncRADation.Sync;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SyncRADation.Networking
{
    public static class SceneFollowService
    {
        public static void BroadcastHostScene()
        {
            var net = LanNetworkManager.Instance;
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected) return;
            string name = SceneManager.GetActiveScene().name ?? "";
            if (string.IsNullOrEmpty(name) || IsTransient(name)) return;
            net.SendSceneFollow(name, false);
        }

        public static void RequestFollow(string sceneName)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            if (string.IsNullOrEmpty(sceneName) || IsTransient(sceneName)) return;
            net.SendSceneFollow(sceneName, true);
        }

        public static bool TryApplyRequest(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return false;
            if (!IsKnownScene(sceneName))
            {
                ModRuntime.Log?.Warning("[SceneFollow] Rejected unknown scene '" + sceneName + "'");
                return false;
            }
            if (AirlockCinematic.DeferFollowWhileAirlockPresent())
            {
                ModRuntime.Log?.Msg("[SceneFollow] Ignore peer airlock load '" + sceneName + "'");
                return true;
            }
            Apply(sceneName);
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

        public static bool IsTransient(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return true;
            return string.Equals(sceneName, "LoadingScreen", System.StringComparison.Ordinal)
                || sceneName.StartsWith("index:", System.StringComparison.Ordinal);
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
            if (string.Equals(_pending, sceneName, System.StringComparison.Ordinal)
                && Time.unscaledTime - _pendingAt < 10f)
                return;

            _pending = sceneName;
            _pendingAt = Time.unscaledTime;
            ModRuntime.Log?.Msg("[SceneFollow] Loading '" + sceneName + "' (was '" + cur + "')");
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
                ModRuntime.Log?.Warning("[SceneFollow] Load failed: " + ex.Message);
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
