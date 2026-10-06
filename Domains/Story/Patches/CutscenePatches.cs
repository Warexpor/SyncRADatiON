// Cutscene Start / Skip / OnDisable detours and the pause-during-cutscene skip remap. Logic: Story/CutsceneSync.
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;

namespace SyncRADation.Patches
{
    [HarmonyPatch(typeof(CutsceneManager), nameof(CutsceneManager.Skip))]
    public static class CutsceneSkipMethodPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(CutsceneManager __instance)
        {
            if (__instance == null) return true;
            // A mod-driven skip (CutsceneSync.NativeSkip) only reaches a cutscene that is running here.
            if (NetGate.IsApplying) return CutsceneSync.StartedHere(__instance);
            if (!NetGate.Party) return true;
            return CutsceneSync.LocalSkip(__instance);
        }
    }

    [HarmonyPatch(typeof(CutsceneManager), nameof(CutsceneManager.StartCutscene))]
    public static class CutsceneStartPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(CutsceneManager __instance)
        {
            if (__instance == null) return true;
            if (!NetGate.Party)
            {
                if (NetGate.Host && !NetGate.IsApplying) CutsceneSync.SoloHostStart(__instance);
                return true;
            }
            if (NetGate.IsApplying)
                return !StorySyncService.InEndingApply || CutsceneSync.EndingReplayStart(__instance);
            return CutsceneSync.LocalStart(__instance);
        }

        [HarmonyPostfix]
        public static void Postfix(CutsceneManager __instance)
        {
            if (__instance == null || !NetGate.Party) return;
            try
            {
                if (!LocalInspect.AirlockCinematic(__instance.gameObject) && !__instance.unskippable)
                    CutsceneSkippingUI.skippableCutscene = true;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }
    }

    [HarmonyPatch(typeof(PauseMenu), nameof(PauseMenu.TogglePauseAnywhere))]
    public static class PauseDuringCutscenePatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (!NetGate.Party) return true;
            try
            {
                // PEN_Titles (airlock) has its own skipper: pausing arms it instead.
                var titles = AirlockCinematic.AllTitles();
                if (titles != null)
                {
                    for (int i = 0; i < titles.Length; i++)
                    {
                        var t = titles[i];
                        if (t == null || !t.started) continue;
                        CutsceneSkippingUI.skippableCutscene = false;
                        AirlockCinematic.ArmTitlesSkip(t);
                        return false;
                    }
                }
                // No PEN_HoleSnowblind / PEN_CodeRoomEnd branch: SkippableCutscene has no Update / OnEnable (only Check /
                // continousCheck), so "arming" one did nothing, and any not-done skipper in PEN_Hole blocked the pause menu.
                bool inCut = PlayerState.cutscene || PlayerState.gameState == PlayerState.gameStates.cutscene
                    || CutsceneSkippingUI.skippableCutscene;

                // Only a manager whose coroutine is live counts (CutsceneManager.Cutscene sets completed after its last
                // cut): an unplayed one also has skipper.done == false, and skipping it stamped it skipped (blocking its
                // start) while the real one played on.
                CutsceneManager target = null;
                var managers = WorldLookup.All<CutsceneManager>();
                if (managers != null)
                {
                    for (int i = 0; i < managers.Length; i++)
                    {
                        var c = managers[i];
                        if (!CutsceneSync.StartedHere(c)) continue;
                        inCut = true;
                        if (target == null && !LocalInspect.AirlockCinematic(c.gameObject))
                            target = c;
                    }
                }
                if (!inCut || target == null) return true;
                target.Skip();
                return false;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return true;
        }
    }

    [HarmonyPatch(typeof(CutsceneManager), "OnDisable")]
    public static class CutsceneOnDisablePatch
    {
        [HarmonyPostfix]
        public static void Postfix(CutsceneManager __instance) => CutsceneSync.OnDisabled(__instance);
    }
}
