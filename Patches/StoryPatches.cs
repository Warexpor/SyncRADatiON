// Host writes SProgress; clients only apply via StoryCommit.
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Patches
{
    [HarmonyPatch(typeof(SProgress))]
    public static class SProgressPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(SProgress.SetBool))]
        public static bool PrefixBool(string key, bool val)
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (NetGate.Client) return false;
            LanNetworkManager.Instance.StorySync.NoteBool(key, val);
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(SProgress.SetInt))]
        public static bool PrefixInt(string key, int val)
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (NetGate.Client) return false;
            LanNetworkManager.Instance.StorySync.NoteInt(key, val);
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(SProgress.SetFloat))]
        public static bool PrefixFloat(string key, float val)
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (NetGate.Client) return false;
            LanNetworkManager.Instance.StorySync.NoteFloat(key, val);
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(SProgress.SetVector))]
        public static bool PrefixVector(string key, Vector3 val)
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (NetGate.Client) return false;
            LanNetworkManager.Instance.StorySync.NoteVector(key, val);
            return true;
        }
    }

    [HarmonyPatch(typeof(END_Manager))]
    public static class EndManagerPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(END_Manager.AddCircle))]
        public static bool PrefixCircle() => AllowHostStat();

        [HarmonyPrefix]
        [HarmonyPatch(nameof(END_Manager.AddDeath))]
        public static bool PrefixDeath() => AllowHostStat();

        [HarmonyPrefix]
        [HarmonyPatch(nameof(END_Manager.AddGraves))]
        public static bool PrefixGraves() => AllowHostStat();

        [HarmonyPrefix]
        [HarmonyPatch(nameof(END_Manager.AddLeave))]
        public static bool PrefixLeave() => AllowHostStat();

        [HarmonyPostfix]
        [HarmonyPatch(nameof(END_Manager.EvaluateEnding))]
        public static void PostfixEvaluate()
        {
            if (!NetGate.Host || NetGate.IsApplying) return;
            LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.DetermineEnding, 0, END_Manager.Ending, "");
        }

        private static bool AllowHostStat()
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            return !NetGate.Client;
        }
    }

    [HarmonyPatch(typeof(Dialoguer))]
    public static class DialoguerPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Dialoguer.StartDialogue), new[] { typeof(int) })]
        public static bool PrefixStartInt(int dialogue)
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (NetGate.Host)
            {
                LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.DialoguerStartId, 0, dialogue, "");
                return true;
            }
            LanNetworkManager.Instance.SendInteractionRequest(0, InteractionKind.DialogueStart, dialogue);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(Dialoguer.StartDialogue), new[] { typeof(DialoguerDialogues) })]
        public static bool PrefixStartEnum(DialoguerDialogues dialogue)
        {
            return PrefixStartInt((int)dialogue);
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(Dialoguer.ContinueDialogue), new System.Type[0])]
        public static bool PrefixContinue()
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (NetGate.Host)
            {
                LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.DialogueContinue, 0, 0, "");
                return true;
            }
            LanNetworkManager.Instance.SendInteractionRequest(0, InteractionKind.DialogueContinue);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(Dialoguer.ContinueDialogue), new[] { typeof(int) })]
        public static bool PrefixContinueChoice(int choice)
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (NetGate.Host)
            {
                LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.DialogueContinue, 0, choice, "");
                return true;
            }
            LanNetworkManager.Instance.SendInteractionRequest(0, InteractionKind.DialogueContinue, choice);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(Dialoguer.EndDialogue))]
        public static bool PrefixEnd()
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (NetGate.Host)
            {
                LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.DialogueEnd, 0, 0, "");
                return true;
            }
            LanNetworkManager.Instance.SendInteractionRequest(0, InteractionKind.DialogueEnd);
            return false;
        }
    }

    [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.hasItem), new[] { typeof(AnItem) })]
    public static class InventoryHasItemPatch
    {
        [HarmonyPostfix]
        public static void Postfix(AnItem item, ref bool __result)
        {
            if (__result || item == null || !NetGate.Live) return;
            if (PartyKeyRing.Has(item))
                __result = true;
        }
    }

    [HarmonyPatch]
    public static class StorageBoxInventoryPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.storeItem), new[] { typeof(AnItem), typeof(int) })]
        public static bool PrefixStore(AnItem item, int number)
        {
            return GateBox(item, number, true);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.retrieveItem), new[] { typeof(AnItem), typeof(int) })]
        public static bool PrefixRetrieve(AnItem item, int number)
        {
            return GateBox(item, number, false);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.boxItem), new[] { typeof(AnItem), typeof(int) })]
        public static bool PrefixBox(AnItem item, int number)
        {
            return GateBox(item, number, true);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.unboxItem), new[] { typeof(AnItem) })]
        public static bool PrefixUnbox(AnItem item)
        {
            return GateBox(item, 1, false);
        }

        private static bool GateBox(AnItem item, int number, bool put)
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (item == null) return true;
            if (NetGate.Host)
            {
                LanNetworkManager.Instance.StorageSync.RequestSend();
                return true;
            }
            int enumVal = 0;
            try { enumVal = (int)item._item; } catch { return false; }
            LanNetworkManager.Instance.SendInteractionRequest(
                0,
                put ? InteractionKind.StoragePut : InteractionKind.StorageTake,
                enumVal,
                number > 0 ? number : 1);
            return false;
        }
    }
}
