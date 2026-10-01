// UseItemInteraction unlock emit + dialogue/message/multi client requests.
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Patches
{
    [HarmonyPatch(typeof(UseItemInteraction), "Update")]
    public static class UseItemInteractionPatch
    {
        private static readonly System.Collections.Generic.HashSet<ulong> _sent
            = new System.Collections.Generic.HashSet<ulong>();
        // Local-only per-frame dedupe (never on the wire): unlocked UseItems whose Update postfix already ran
        // the full path, so the per-frame call stops before IsPenTitlesCard / WorldId work.
        private static readonly System.Collections.Generic.HashSet<int> _updateDone
            = new System.Collections.Generic.HashSet<int>();

        public static void OnSceneChanged()
        {
            _sent.Clear();
            _updateDone.Clear();
        }

        internal static void OnLocalUnlocked(UseItemInteraction u, bool fromUpdate)
        {
            if (NetGate.IsApplying || !NetGate.Live) return;
            if (u == null || !u.unlocked) return;
            int inst = 0;
            if (fromUpdate)
            {
                try { inst = u.GetInstanceID(); } catch (System.Exception e) { Guard.Swallow(e); }
                if (inst != 0 && _updateDone.Contains(inst)) return;
            }
            ulong id = WorldId.FromGameObject(u.gameObject);
            if (id == 0) return;
            if (inst != 0) _updateDone.Add(inst);
            if (!fromUpdate || !AirlockCinematic.IsPenTitlesCard(u))
                AirlockCinematic.NoteLocalUnlock(u);
            if (!_sent.Add(id)) return;
            // Host unlocks via native Dialoguer (no InteractionRequest). Revoke ConsumesKey
            // ring + bag copies the same as ApplyUseItem (0.5.17).
            if (NetGate.Host)
            {
                try { InteractionSyncService.HostRevokeIfConsumed(u); } catch (System.Exception e) { Guard.Swallow(e); }
                try
                {
                    var net = LanNetworkManager.Instance;
                    if (net != null)
                        net.PuzzleSync.Emit(PuzzleType.UseItemInteraction, id, u);
                }
                catch (System.Exception e) { Guard.Swallow(e); }
                return;
            }
            if (!NetGate.Client) return;
            PlaytestLog.Event("Interact", "request UseItem (unlocked) id=" + id.ToString("X16"));
            LanNetworkManager.Instance.InteractionHandlers.SendInteractionRequest(id, InteractionKind.UseItem);
        }

        [HarmonyPrefix]
        public static bool Prefix(UseItemInteraction __instance)
        {
            if (!NetGate.Live || __instance == null) return true;
            try
            {
                var inter = __instance.inter;
                if (inter != null && inter.inRange)
                {
                    // Native Update starts the use dialogue this frame (inter.triggered, Ghidra UseItemInteraction.c
                    // Update): bind the key then, not every in-range frame (FindInBag walks the bag).
                    if (inter.triggered)
                        __instance.key = PartyKeyRing.BindSceneKey(__instance.key);
                    if (InteractorDropUpdatePatch.InteractPressed())
                    {
                        string cur = "";
                        try
                        {
                            var c = InventoryManager.CurrentItem;
                            cur = c != null ? c._item.ToString() : "none";
                        }
                        catch { cur = "?"; }
                        PlaytestLog.Event("Interact", "UseItem press " + __instance.gameObject.name
                            + " key=" + (__instance.key != null ? __instance.key._item.ToString() : "null")
                            + " current=" + cur
                            + " unlocked=" + __instance.unlocked);
                    }
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(UseItemInteraction __instance) => OnLocalUnlocked(__instance, true);
    }

    [HarmonyPatch(typeof(UseItemInteraction), "dialogueOver")]
    public static class UseItemDialogueOverPatch
    {
        [HarmonyPostfix]
        public static void Postfix(UseItemInteraction __instance) => UseItemInteractionPatch.OnLocalUnlocked(__instance, false);
    }

    [HarmonyPatch(typeof(UseItemInteraction), nameof(UseItemInteraction.onMessageEvent))]
    public static class UseItemMessageEventPatch
    {
        [HarmonyPostfix]
        public static void Postfix(UseItemInteraction __instance) => UseItemInteractionPatch.OnLocalUnlocked(__instance, false);
    }

    [HarmonyPatch(typeof(UseItemMultiInteraction), nameof(UseItemMultiInteraction.ready))]
    public static class UseItemMultiPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(UseItemMultiInteraction __instance)
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (__instance == null) return true;
            if (NetGate.Host) return true;
            LanNetworkManager.Instance.InteractionHandlers.SendInteractionRequest(
                WorldId.FromGameObject(__instance.gameObject), InteractionKind.UseItemMulti);
            return false;
        }

        [HarmonyPostfix]
        public static void Postfix(UseItemMultiInteraction __instance)
        {
            if (NetGate.IsApplying || !NetGate.Live || !NetGate.Host) return;
            try { InteractionSyncService.HostRevokeUseItemMulti(__instance); } catch (System.Exception e) { Guard.Swallow(e); }
        }
    }
}
