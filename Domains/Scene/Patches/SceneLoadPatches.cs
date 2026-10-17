// Level loads are personal: the host announces its own (SceneFollow), a client loads alone (or leaves the party for the title).
// One Harmony class per method: a missing IL2CPP overload then fails only its own class, not every load gate.
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
                // Personal like every level load. The announcement only moves a client that just joined (or sits
                // in a title-flow scene).
                SceneFollowService.NoteGoingTo(scene);
                PlaytestLog.Event("Scene", "host load '" + scene + "'");
                SceneOwners.HostRecompute(LanNetworkManager.Instance);
                LanNetworkManager.Instance.SceneHandlers.SendSceneFollow(scene, false);
                return true;
            }

            // Quit to title (pause menu, restart, own credits done): this client leaves the party and loads it
            // offline. Stop synchronously so the load runs ungated.
            if (SceneFollowService.IsLeaveGame(scene))
            {
                PlaytestLog.Event("Scene", "client load '" + scene + "' - leaving the session");
                LanNetworkManager.Instance.EndSession("Left to the main menu");
                return true;
            }

            // Continue / New Game from a title-flow scene while connected would load this player's own slot: in a
            // party the world is the host's, so go there instead.
            if (SceneFollowService.LocalInTitleFlow())
            {
                string host = LanNetworkManager.Instance.HostSceneName;
                // CreditsEnd-style repeats: one log line per scene per 5 s.
                float now = Time.unscaledTime;
                if (!string.Equals(_lastBlockedScene, scene, System.StringComparison.Ordinal) || now - _lastBlockedLog > 5f)
                {
                    _lastBlockedScene = scene;
                    _lastBlockedLog = now;
                    PlaytestLog.Event("Scene", "client blocked title load '" + scene + "', going to the host's '" + host + "'");
                }
                if (!SceneFollowService.IsTransient(host)) SceneFollowService.FollowHost(host);
                return false;
            }

            // Every other level load is this player's own: the party does not come along.
            PlaytestLog.Event("Scene", "own load '" + scene + "'");
            SceneFollowService.NoteOwnLoad(scene);
            return true;
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

    [HarmonyPatch(typeof(UnityEngine.SceneManagement.SceneManager), nameof(UnityEngine.SceneManagement.SceneManager.LoadScene), new[] { typeof(string) })]
    public static class SceneManagerLoadStringPatch
    {
        // Direct SceneManager.LoadScene(string) callers (PenroseAirlock goes through AsyncLoader.LoadLevel(int)).
        // AirlockDoorLoadZone.Update inlines AsyncLoader.LoadLevel(int) and only loads "LoadingScreen" here
        // (transient, passes); no scene or prefab places that component.
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
