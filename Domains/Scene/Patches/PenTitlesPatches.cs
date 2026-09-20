// PEN_Titles airlock cinematic — local skip arming + Interaction.trigger begin.
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;

namespace SyncRADation.Patches
{
    [HarmonyPatch(typeof(PEN_Titles), "Update")]
    public static class PenTitlesCinematicPatch
    {
        [HarmonyPostfix]
        public static void Postfix(PEN_Titles __instance)
        {
            if (__instance == null || !NetGate.Live) return;
            try
            {
                if (__instance.started)
                {
                    CutsceneSkippingUI.skippableCutscene = false;
                    AirlockCinematic.ArmTitlesSkip(__instance);
                }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(PEN_Titles), "Skip")]
    public static class PenTitlesSkipPatch
    {
        [HarmonyPrefix]
        public static void Prefix(PEN_Titles __instance)
        {
            if (__instance == null || !NetGate.Live) return;
            AirlockCinematic.ArmTitlesSkip(__instance);
        }
    }

    [HarmonyPatch(typeof(Interaction), nameof(Interaction.trigger))]
    public static class AirlockLocalTriggerPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Interaction __instance)
        {
            if (NetGate.IsApplying || !NetGate.Live) return;
            AirlockCinematic.TryBeginLocal(__instance);
        }
    }
}
