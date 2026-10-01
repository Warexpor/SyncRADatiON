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
        // persistent: log throttle
        static string _lastBlockedScene;
        // persistent: log throttle
        static float _lastBlockedLog;

        /// <summary>True when the load must run untouched: a mod apply scope, or no live party (vanilla host / offline).</summary>
        public static bool Bypass()
        {
            if (SceneFollowService.LoadsSuppressed) return false;
            return NetGate.IsApplying || !NetGate.Party;
        }

        public static bool GateLevel(string scene)
        {
            if (SceneFollowService.LoadsSuppressed) return false;
            if (NetGate.IsApplying) return true;
            // A host with nobody connected is vanilla: no follow broadcast, no gate.
            if (!NetGate.Party) return true;
            if (string.IsNullOrEmpty(scene)) return true;
            if (SceneFollowService.IsTransient(scene)) return true;
            if (AirlockCinematic.IsPersonalChapterLoad(scene))
            {
                PlaytestLog.Event("Scene", "local airlock load '" + scene + "'");
                return true;
            }

            if (NetGate.Host)
            {
                // The host reaching the main menu (quit to menu / credits done) ends the session: clients that
                // follow to a menu still connected would Continue / New Game into a host that never loaded a slot.
                // Clients get the reason, go offline and stay where they are (or keep watching their own credits).
                if (SceneFollowService.IsMainMenu(scene))
                {
                    PlaytestLog.Event("Scene", "host load '" + scene + "' - ending session");
                    LanNetworkManager.Instance.EndSession("Host returned to the main menu");
                    return true;
                }
                SceneFollowService.NoteGoingTo(scene);
                PlaytestLog.Event("Scene", "host load '" + scene + "'");
                LanNetworkManager.Instance.SendSceneFollow(scene, false);
                return true;
            }

            // CreditsEnd re-issues its load every frame after the fade: one log line per scene per 5 s.
            float now = Time.unscaledTime;
            if (!string.Equals(_lastBlockedScene, scene, System.StringComparison.Ordinal) || now - _lastBlockedLog > 5f)
            {
                _lastBlockedScene = scene;
                _lastBlockedLog = now;
                PlaytestLog.Event("Scene", "client blocked local load, request '" + scene + "'");
            }
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
            if (SceneLoadGate.Bypass()) return true;
            return SceneLoadGate.GateLevel(SceneFollowService.ResolveLevelName(target));
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
            if (SceneLoadGate.Bypass()) return true;
            return SceneLoadGate.GateLevel(SceneFollowService.ResolveLevelName(scene));
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
            if (NetGate.IsApplying || !NetGate.Party) return true;
            // Host: allowed; its ResetNow + LoadLevel(MainMenu) ends the session (SceneLoadGate). Client: quit to menu
            // leaves the party. Stop synchronously so the native ResetNow + LoadLevel that follows run offline - a
            // deferred stop would let that load be gated as a follow request and the quit silently did nothing.
            if (NetGate.Client)
            {
                LanNetworkManager.Instance.EndSession("Left to the main menu");
                return true;
            }
            return true;
        }
    }

    // CreditsEnd.Update (every frame after the fade) runs ResetGame.ResetNow, then LoadLevel(MainMenu2). On a client in
    // the party that load is gated into a request, so ResetNow ran natively every frame (statics wiped repeatedly)
    // until the host's own credits end. The host's follow runs ResetNow itself (SceneFollowService.Apply, apply scope).
    // ResetGame.ResetNow is a unique RVA (0x776050); only the client's own un-applied call from the EndCredits scene is skipped.
    [HarmonyPatch(typeof(ResetGame), nameof(ResetGame.ResetNow))]
    public static class ResetNowCreditsPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (NetGate.IsApplying || !NetGate.Client || !NetGate.Party) return true;
            try
            {
                if (!string.Equals(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                        SceneFollowService.EndCreditsScene, System.StringComparison.Ordinal))
                    return true;
            }
            catch (System.Exception e) { Guard.Swallow(e); return true; }
            return false;
        }
    }

    [HarmonyPatch(typeof(UnityEngine.SceneManagement.SceneManager), nameof(UnityEngine.SceneManagement.SceneManager.LoadScene), new[] { typeof(string) })]
    public static class SceneManagerLoadStringPatch
    {
        // AirlockDoorLoadZone.Update calls SceneManager.LoadScene(string) directly
        // (PenroseAirlock already goes through AsyncLoader.LoadLevel(int)).
        [HarmonyPrefix]
        public static bool Prefix(string sceneName) => SceneLoadGate.GateLevel(sceneName);
    }

    [HarmonyPatch(typeof(UnityEngine.SceneManagement.SceneManager), nameof(UnityEngine.SceneManagement.SceneManager.LoadScene), new[] { typeof(int) })]
    public static class SceneManagerLoadIntPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(int sceneBuildIndex)
        {
            if (SceneLoadGate.Bypass()) return true;
            return SceneLoadGate.GateLevel(SceneFollowService.ResolveLevelName(sceneBuildIndex));
        }
    }

    [HarmonyPatch(typeof(LoadLevelZone), "OnTriggerEnter2D")]
    public static class LoadLevelZonePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(LoadLevelZone __instance)
        {
            if (SceneLoadGate.Bypass()) return true;
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
