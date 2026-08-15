// Host LoadLevel broadcasts SceneFollow; client LoadLevel becomes a request.
// One Harmony class per method — a single missing IL2CPP overload used to skip the whole file.
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Patches
{
    internal static class SceneLoadGate
    {
        public static bool GateLevel(string scene)
        {
            if (NetGate.IsApplying) return true;
            if (!NetGate.Live) return true;
            if (string.IsNullOrEmpty(scene)) return true;
            if (SceneFollowService.IsTransient(scene)) return true;
            if (AirlockCinematic.IsPersonalChapterLoad(scene))
            {
                AirlockCinematic.NotePersonalLoad(scene);
                PlaytestLog.Event("Scene", "local airlock load '" + scene + "'");
                return true;
            }
            if (scene.StartsWith("index:"))
            {
                if (NetGate.Host)
                    return true;
                return false;
            }

            if (NetGate.Host)
            {
                PlaytestLog.Event("Scene", "host load '" + scene + "'");
                LanNetworkManager.Instance.SendSceneFollow(scene, false);
                return true;
            }

            PlaytestLog.Event("Scene", "client blocked local load, request '" + scene + "'");
            SceneFollowService.RequestFollow(scene);
            return false;
        }
    }

    [HarmonyPatch(typeof(AsyncLoader), nameof(AsyncLoader.LoadLevel), new[] { typeof(string) })]
    public static class AsyncLoaderStringPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(string target) => SceneLoadGate.GateLevel(target);
    }

    [HarmonyPatch(typeof(AsyncLoader), nameof(AsyncLoader.LoadLevel), new[] { typeof(int) })]
    public static class AsyncLoaderIntPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(int target)
        {
            if (NetGate.IsApplying) return true;
            if (!NetGate.Live) return true;
            string name = "";
            try { name = AsyncLoader.targetLevelString ?? ""; } catch { }
            if (string.IsNullOrEmpty(name))
                name = "index:" + target;
            return SceneLoadGate.GateLevel(name);
        }
    }

    [HarmonyPatch(typeof(NewApplication), nameof(NewApplication.LoadLevel), new[] { typeof(string) })]
    public static class NewApplicationStringPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(string scene) => SceneLoadGate.GateLevel(scene);
    }

    [HarmonyPatch(typeof(NewApplication), nameof(NewApplication.LoadLevel), new[] { typeof(int) })]
    public static class NewApplicationIntPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(int scene)
        {
            if (NetGate.IsApplying) return true;
            if (!NetGate.Live) return true;
            return SceneLoadGate.GateLevel("index:" + scene);
        }
    }

    [HarmonyPatch(typeof(SceneHelper), nameof(SceneHelper.LoadScene))]
    public static class SceneHelperLoadPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(string scene) => SceneLoadGate.GateLevel(scene);
    }

    [HarmonyPatch(typeof(SceneHelper), nameof(SceneHelper.LoadSceneDirect))]
    public static class SceneHelperLoadDirectPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(string scene) => SceneLoadGate.GateLevel(scene);
    }

    [HarmonyPatch(typeof(SceneHelper), nameof(SceneHelper.resetGame))]
    public static class SceneHelperResetPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            return NetGate.Host;
        }
    }

    [HarmonyPatch(typeof(LoadLevelZone), "OnTriggerEnter2D")]
    public static class LoadLevelZonePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(LoadLevelZone __instance)
        {
            if (NetGate.IsApplying) return true;
            if (!NetGate.Live) return true;
            if (__instance == null) return true;
            string scene = __instance.SceneName;
            if (string.IsNullOrEmpty(scene)) return true;
            return SceneLoadGate.GateLevel(scene);
        }
    }

    [HarmonyPatch(typeof(LoadLevelInteraction), "load")]
    public static class LoadLevelInteractionPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(LoadLevelInteraction __instance)
        {
            if (__instance == null) return true;
            string scene = __instance.targetLevel;
            if (string.IsNullOrEmpty(scene)) return true;
            return SceneLoadGate.GateLevel(scene);
        }
    }
}
