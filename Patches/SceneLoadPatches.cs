// Host LoadLevel broadcasts SceneFollow; client LoadLevel becomes a request.
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Patches
{
    [HarmonyPatch]
    public static class SceneLoadPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(AsyncLoader), nameof(AsyncLoader.LoadLevel), new[] { typeof(string) })]
        public static bool PrefixAsyncString(string target)
        {
            return GateLevel(target);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AsyncLoader), nameof(AsyncLoader.LoadLevel), new[] { typeof(int) })]
        public static bool PrefixAsyncInt(int target)
        {
            if (NetGate.IsApplying) return true;
            if (!NetGate.Live) return true;
            string name = "";
            try { name = AsyncLoader.targetLevelString ?? ""; } catch { }
            if (string.IsNullOrEmpty(name))
                name = "index:" + target;
            return GateLevel(name);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NewApplication), nameof(NewApplication.LoadLevel), new[] { typeof(string) })]
        public static bool PrefixAppString(string scene)
        {
            return GateLevel(scene);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NewApplication), nameof(NewApplication.LoadLevel), new[] { typeof(int) })]
        public static bool PrefixAppInt(int scene)
        {
            if (NetGate.IsApplying) return true;
            if (!NetGate.Live) return true;
            return GateLevel("index:" + scene);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(SceneHelper), nameof(SceneHelper.LoadScene))]
        public static bool PrefixHelper(string scene)
        {
            return GateLevel(scene);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(SceneHelper), nameof(SceneHelper.LoadSceneDirect))]
        public static bool PrefixHelperDirect(string scene)
        {
            return GateLevel(scene);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(SceneHelper), nameof(SceneHelper.resetGame))]
        public static bool PrefixResetGame()
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            return NetGate.Host;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(LoadLevelZone), "OnTriggerEnter2D")]
        public static bool PrefixLoadZone(LoadLevelZone __instance)
        {
            if (NetGate.IsApplying) return true;
            if (!NetGate.Live) return true;
            if (__instance == null) return true;
            string scene = __instance.SceneName;
            if (string.IsNullOrEmpty(scene)) return true;
            if (NetGate.Host)
            {
                LanNetworkManager.Instance.SendSceneFollow(scene, false);
                return true;
            }
            SceneFollowService.RequestFollow(scene);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(LoadLevelInteraction), "load")]
        public static bool PrefixLoadInteraction(LoadLevelInteraction __instance)
        {
            if (__instance == null) return true;
            string scene = __instance.targetLevel;
            if (string.IsNullOrEmpty(scene)) return true;
            return GateLevel(scene);
        }

        private static bool GateLevel(string scene)
        {
            if (NetGate.IsApplying) return true;
            if (!NetGate.Live) return true;
            if (string.IsNullOrEmpty(scene)) return true;
            if (scene.StartsWith("index:"))
            {
                // Host still loads by index; clients get the resulting scene via SceneHello/Follow after load.
                if (NetGate.Host)
                    return true;
                return false;
            }

            if (NetGate.Host)
            {
                LanNetworkManager.Instance.SendSceneFollow(scene, false);
                return true;
            }

            SceneFollowService.RequestFollow(scene);
            return false;
        }
    }
}
