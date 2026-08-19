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
            return GateSet(key, 0, val ? 1 : 0, 0f, 0f, 0f);
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(SProgress.SetInt))]
        public static bool PrefixInt(string key, int val)
        {
            return GateSet(key, 1, val, 0f, 0f, 0f);
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(SProgress.SetFloat))]
        public static bool PrefixFloat(string key, float val)
        {
            return GateSet(key, 2, 0, val, 0f, 0f);
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(SProgress.SetString))]
        public static bool PrefixString(string key, string val)
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (NetGate.Host)
            {
                LanNetworkManager.Instance.StorySync.NoteString(key, val);
                return true;
            }
            if (IsInspectOrigin())
            {
                LanNetworkManager.Instance.SendInteractionRequest(
                    0, InteractionKind.InspectFlag, 3, 0, 0f, 0f, 0f, (key ?? "") + "\n" + (val ?? ""));
                return true;
            }
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(SProgress.SetVector))]
        public static bool PrefixVector(string key, Vector3 val)
        {
            return GateSet(key, 4, 0, val.x, val.y, val.z);
        }

        static bool GateSet(string key, int kind, int int1, float f0, float f1, float f2)
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (NetGate.Host)
            {
                var story = LanNetworkManager.Instance.StorySync;
                if (kind == 0) story.NoteBool(key, int1 != 0);
                else if (kind == 1) story.NoteInt(key, int1);
                else if (kind == 2) story.NoteFloat(key, f0);
                else story.NoteVector(key, new Vector3(f0, f1, f2));
                return true;
            }
            if (IsInspectOrigin())
            {
                LanNetworkManager.Instance.SendInteractionRequest(
                    0, InteractionKind.InspectFlag, kind, int1, f0, f1, f2, key ?? "");
                return true;
            }
            return false;
        }

        static bool IsInspectOrigin()
        {
            try { if (PlayerState.eventScreen) return true; } catch { }
            try
            {
                var gs = PlayerState.gameState;
                if (gs == PlayerState.gameStates.eventScreen || gs == PlayerState.gameStates.book)
                    return true;
            }
            catch { }
            return false;
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

        [HarmonyPrefix]
        [HarmonyPatch(nameof(END_Manager.EvaluateEnding))]
        public static bool PrefixEvaluate()
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            return !NetGate.Client;
        }

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

    [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.hasItem), new[] { typeof(Items.itemlist) })]
    public static class InventoryHasItemEnumPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Items.itemlist item, ref bool __result)
        {
            if (__result || !NetGate.Live) return;
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

    static class DialoguerGate
    {
        static bool _flavorActive;

        public static bool Start(int dialogueId)
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (LocalInspect.DialoguerFlavor(dialogueId) || LocalInspect.InspectScreen())
            {
                if (dialogueId == (int)DialoguerDialogues.useItemDialogue)
                    BindUseItemName();
                _flavorActive = true;
                return true;
            }
            _flavorActive = false;
            if (NetGate.Host)
            {
                LanNetworkManager.Instance.StorySync.BroadcastPresentation(
                    StoryCmd.DialoguerStartId, 0, dialogueId, "");
                return true;
            }
            PlaytestLog.Event("Story", "request Dialoguer " + dialogueId);
            LanNetworkManager.Instance.SendInteractionRequest(0, InteractionKind.DialogueStart, dialogueId);
            return false;
        }

        public static bool Continue(int choice)
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (_flavorActive || LocalInspect.InspectScreen())
            {
                if (_flavorActive)
                    BindUseItemName();
                return true;
            }
            if (NetGate.Host)
            {
                LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.DialogueContinue, 0, choice, "");
                return true;
            }
            LanNetworkManager.Instance.SendInteractionRequest(0, InteractionKind.DialogueContinue, choice);
            return false;
        }

        public static bool End()
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (_flavorActive || LocalInspect.InspectScreen())
            {
                _flavorActive = false;
                return true;
            }
            if (NetGate.Host)
            {
                LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.DialogueEnd, 0, 0, "");
                return true;
            }
            LanNetworkManager.Instance.SendInteractionRequest(0, InteractionKind.DialogueEnd);
            return false;
        }

        static void BindUseItemName()
        {
            try
            {
                PartyKeyRing.BindUseDialogue(UseItemInteraction.currentUseItem);
            }
            catch { }
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<UseItemInteraction>();
                if (all == null) return;
                for (int i = 0; i < all.Length; i++)
                {
                    var u = all[i];
                    if (u == null) continue;
                    bool inRange = false;
                    try { inRange = u.inter != null && u.inter.inRange; } catch { }
                    if (!inRange && !u.unlocked) continue;
                    UseItemDialogueNamePatch.Bind(u);
                    return;
                }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(Dialoguer), nameof(Dialoguer.StartDialogue), new[] { typeof(int) })]
    public static class DialoguerStartIntPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(int dialogueId) => DialoguerGate.Start(dialogueId);
    }

    [HarmonyPatch(typeof(Dialoguer), nameof(Dialoguer.StartDialogue), new[] { typeof(DialoguerDialogues) })]
    public static class DialoguerStartEnumPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(DialoguerDialogues dialogue) => DialoguerGate.Start((int)dialogue);
    }

    [HarmonyPatch(typeof(Dialoguer), nameof(Dialoguer.StartDialogue), new[] { typeof(int), typeof(DialoguerCallback) })]
    public static class DialoguerStartIntCbPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(int dialogueId) => DialoguerGate.Start(dialogueId);
    }

    [HarmonyPatch(typeof(Dialoguer), nameof(Dialoguer.StartDialogue), new[] { typeof(DialoguerDialogues), typeof(DialoguerCallback) })]
    public static class DialoguerStartEnumCbPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(DialoguerDialogues dialogue) => DialoguerGate.Start((int)dialogue);
    }

    [HarmonyPatch(typeof(Dialoguer), nameof(Dialoguer.ContinueDialogue), new[] { typeof(int) })]
    public static class DialoguerContinueIntPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(int choice) => DialoguerGate.Continue(choice);
    }

    [HarmonyPatch(typeof(Dialoguer), nameof(Dialoguer.ContinueDialogue), new System.Type[0])]
    public static class DialoguerContinuePatch
    {
        [HarmonyPrefix]
        public static bool Prefix() => DialoguerGate.Continue(0);
    }

    [HarmonyPatch(typeof(Dialoguer), nameof(Dialoguer.EndDialogue))]
    public static class DialoguerEndPatch
    {
        [HarmonyPrefix]
        public static bool Prefix() => DialoguerGate.End();
    }
}
