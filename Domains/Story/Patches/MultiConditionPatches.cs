// MultiConditionEvent TryOnce / TryTrigger — client emit, host Presentation.
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;

namespace SyncRADation.Patches
{
    [HarmonyPatch(typeof(MultiConditionEvent), nameof(MultiConditionEvent.TryOnce))]
    public static class MultiConditionPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(MultiConditionEvent __instance)
        {
            if (NetGate.IsApplying || !NetGate.Party) return true;
            if (__instance == null) return true;
            if (NetGate.Host) return true;
            LanNetworkManager.Instance.SendInteractionRequest(
                WorldId.FromGameObject(__instance.gameObject), InteractionKind.MultiCondition, 0);
            return false;
        }

        [HarmonyPostfix]
        public static void Postfix(MultiConditionEvent __instance)
        {
            if (!NetGate.Host || NetGate.IsApplying || !NetGate.Party) return;
            if (__instance == null) return;
            ulong id = WorldId.FromGameObject(__instance.gameObject);
            if (id == 0 || !EventZonePatch.MarkMultiOnce(id)) return;
            LanNetworkManager.Instance.StorySync.BroadcastPresentation(
                StoryCmd.MultiConditionFire, id, 0, StoryWire.HostCounted);
        }
    }

    [HarmonyPatch(typeof(MultiConditionEvent), nameof(MultiConditionEvent.TryTrigger))]
    public static class MultiConditionTriggerPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(MultiConditionEvent __instance)
        {
            if (NetGate.IsApplying || !NetGate.Party) return true;
            if (__instance == null) return true;
            if (NetGate.Host) return true;
            LanNetworkManager.Instance.SendInteractionRequest(
                WorldId.FromGameObject(__instance.gameObject), InteractionKind.MultiCondition, 1);
            return false;
        }

        [HarmonyPostfix]
        public static void Postfix(MultiConditionEvent __instance)
        {
            if (!NetGate.Host || NetGate.IsApplying || !NetGate.Party) return;
            if (__instance == null) return;
            ulong id = WorldId.FromGameObject(__instance.gameObject);
            if (id == 0 || !EventZonePatch.MarkMultiTrigger(id)) return;
            LanNetworkManager.Instance.StorySync.BroadcastPresentation(
                StoryCmd.MultiConditionFire, id, 1, StoryWire.HostCounted);
        }
    }
}
