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
            if (AirlockCinematic.ClientSplitFromHost()) return true; // the host has no such object (wreck / hole split)
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
            if (AirlockCinematic.ClientSplitFromHost()) return true; // the host has no such object (wreck / hole split)
            LanNetworkManager.Instance.SendInteractionRequest(
                WorldId.FromGameObject(__instance.gameObject), InteractionKind.MultiCondition, 1);
            return false;
        }

        // No host relay: TryTrigger is a counter (tried++ per call, OnTryDone on the call reaching tries), and its
        // callers are often replayed on peers already (EventZone onInRange, cutscene Proceed), so a live relay counted
        // those calls twice and fired OnTryDone early. The host's tried rides the PuzzleState poll instead, and
        // StorySyncService.ApplyMultiConditionEvent fires OnTryDone when it crosses tries past the local count.
    }
}
