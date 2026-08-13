// Host commands chapter loads; clients apply the same AsyncLoader.LoadLevel.
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
            if (string.IsNullOrEmpty(name)) return;
            net.SendSceneFollow(name, false);
        }

        public static void RequestFollow(string sceneName)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            if (string.IsNullOrEmpty(sceneName)) return;
            net.SendSceneFollow(sceneName, true);
        }

        public static void Apply(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return;
            string cur = SceneManager.GetActiveScene().name ?? "";
            if (string.Equals(cur, sceneName, System.StringComparison.Ordinal))
                return;

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
                Apply(msg.SceneName);
                return;
            }

            if (net.Role == NetworkRole.Host) return;
            Apply(msg.SceneName);
        }
    }
}
