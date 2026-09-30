// Host writes SProgress directly; client writes are forwarded to the host (host applies + commits).
using System;
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
            return GateSet(new StoryFlagEntry { Kind = 0, Key = key, BoolVal = val });
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(SProgress.SetInt))]
        public static bool PrefixInt(string key, int val)
        {
            return GateSet(new StoryFlagEntry { Kind = 1, Key = key, IntVal = val });
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(SProgress.SetFloat))]
        public static bool PrefixFloat(string key, float val)
        {
            return GateSet(new StoryFlagEntry { Kind = 2, Key = key, FloatVal = val });
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(SProgress.SetString))]
        public static bool PrefixString(string key, string val)
        {
            return GateSet(new StoryFlagEntry { Kind = 3, Key = key, StringVal = val ?? "" });
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(SProgress.SetVector))]
        public static bool PrefixVector(string key, Vector3 val)
        {
            return GateSet(new StoryFlagEntry { Kind = 4, Key = key, FloatVal = val.x, VecY = val.y, VecZ = val.z });
        }

        static bool GateSet(StoryFlagEntry e)
        {
            if (!NetGate.Live) return true;
            var story = LanNetworkManager.Instance.StorySync;
            if (NetGate.IsApplying)
            {
                // ApplyCommit writes must never echo back. A client-applied presentation (cutscene skip,
                // EventZone / MultiCondition / Proceed UnityEvents) is the one apply scope whose writes are
                // *consequences* the host may not have run (host in another room): author them on the host.
                if (NetGate.Client && StorySyncService.ClientAuthorScope)
                    story.ClientForward(e);
                return true;
            }
            if (NetGate.Host)
            {
                switch (e.Kind)
                {
                    case 0: story.NoteBool(e.Key, e.BoolVal); break;
                    case 1: story.NoteInt(e.Key, e.IntVal); break;
                    case 2: story.NoteFloat(e.Key, e.FloatVal); break;
                    case 3: story.NoteString(e.Key, e.StringVal); break;
                    default: story.NoteVector(e.Key, new Vector3(e.FloatVal, e.VecY, e.VecZ)); break;
                }
                return true;
            }
            // Client write outside an apply scope. Book / EventScreen flags keep their immediate request;
            // everything else (Interaction.trigger UnityEvents, cutscene coroutines, NPC / pickup flags) is
            // coalesced and forwarded so it is no longer silently dropped. Local write always happens.
            if (IsInspectOrigin())
            {
                if (!StorySyncService.ForwardSuppressed && !StorySyncService.SameAsLocal(e))
                    StorySyncService.SendFlag(LanNetworkManager.Instance, e);
                return true;
            }
            story.ClientForward(e);
            return true;
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

    // SaveManager.Save/Load/NewGame dump and restore *per-player* state (hp, position, enemies, minimap,
    // inventory, END statics) through SProgress.Set*: never forward those to the host.
    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.Save))]
    public static class SaveManagerSaveScopePatch
    {
        [HarmonyPrefix] public static void Prefix() => StorySyncService.BeginSuppressForward();
        [HarmonyFinalizer] public static void Finalizer() => StorySyncService.EndSuppressForward();
    }

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.Load))]
    public static class SaveManagerLoadScopePatch
    {
        [HarmonyPrefix] public static void Prefix() => StorySyncService.BeginSuppressForward();
        [HarmonyFinalizer]
        public static void Finalizer()
        {
            StorySyncService.EndSuppressForward();
            try { LanNetworkManager.Instance?.StorySync.ResetEndBase(); }
            catch (System.Exception ex) { StorySyncService.WarnOnce("Load ResetEndBase", ex); }
        }
    }

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.NewGame))]
    public static class SaveManagerNewGameScopePatch
    {
        [HarmonyPrefix] public static void Prefix() => StorySyncService.BeginSuppressForward();
        [HarmonyFinalizer]
        public static void Finalizer()
        {
            StorySyncService.EndSuppressForward();
            try { LanNetworkManager.Instance?.StorySync.ResetEndBase(); }
            catch (System.Exception ex) { StorySyncService.WarnOnce("NewGame ResetEndBase", ex); }
        }
    }

    // END_Manager: Add* and the direct static writes (NPC_Tracker, InteractiveLockSingle, PlayerState heal,
    // MEM_ChecklistLogic) run natively on every peer; the client's *delta* is sent to the host
    // (StorySyncService.FlushEndDelta) and the host commits one shared tally. OBS_Tracker.Trigger is an
    // empty method in this build (shares RVA 0x2CB6B0 with every empty stub) - never patch it.
    [HarmonyPatch(typeof(END_Manager))]
    public static class EndManagerPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(END_Manager.EvaluateEnding))]
        public static bool PrefixEvaluate()
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            return !NetGate.Client;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(END_Manager.CalculatePlaystyle))]
        public static bool PrefixCalculatePlaystyle()
        {
            // Host owns playstyle inputs; client receives them via StoryCommit.
            if (NetGate.IsApplying || !NetGate.Live) return true;
            return !NetGate.Client;
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(END_Manager.EvaluateEnding))]
        public static void PostfixEvaluate()
        {
            if (!NetGate.Host || NetGate.IsApplying) return;
            LanNetworkManager.Instance.StorySync.MarkDirty();
        }
    }

    // Secret ending: END_Graves.Graves() writes END_Manager.Ending = 1 directly.
    [HarmonyPatch(typeof(END_Graves), nameof(END_Graves.Graves))]
    public static class EndGravesPatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            if (!NetGate.Live || NetGate.IsApplying) return;
            var story = LanNetworkManager.Instance.StorySync;
            if (NetGate.Host)
            {
                story.MarkDirty();
                return;
            }
            LanNetworkManager.Instance.SendInteractionRequest(
                0, InteractionKind.InspectFlag, 100 + (int)StoryCmd.EndGraves);
        }
    }

    // Ending start is party-wide: host broadcasts (after a fresh commit), a client asks the host.
    [HarmonyPatch(typeof(Finale), nameof(Finale.determineEnding))]
    public static class FinaleDetermineEndingPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            var net = LanNetworkManager.Instance;
            if (NetGate.Host)
            {
                net.StorySync.HostBroadcastEnding(net);
                return true;
            }
            net.SendInteractionRequest(0, InteractionKind.InspectFlag, 100 + (int)StoryCmd.DetermineEnding);
            return false;
        }
    }

    [HarmonyPatch(typeof(Finale), nameof(Finale.goToPenny))]
    public static class FinaleGoToPennyPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            var net = LanNetworkManager.Instance;
            if (NetGate.Host)
            {
                if (net.StorySync.Throttled("@goToPenny", 5f)) return false;
                net.StorySync.BroadcastPresentation(StoryCmd.GoToPenny, 0, 0, "");
                return true;
            }
            net.SendInteractionRequest(0, InteractionKind.InspectFlag, 100 + (int)StoryCmd.GoToPenny);
            return false;
        }
    }

    // Cutscene scripts run console strings (`goto <room>`, `sethp 1`) through Cheats.cheat. Peers outside the
    // cutscene room skip the cutscene and would never move: relay so the whole party is gathered.
    [HarmonyPatch(typeof(global::Cheats), nameof(global::Cheats.cheat))]
    public static class ScriptedCheatPatch
    {
        [HarmonyPrefix]
        public static void Prefix(string cheat)
        {
            if (NetGate.IsApplying || !NetGate.Live) return;
            LanNetworkManager.Instance.StorySync.OnScriptedCheat(cheat);
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

    [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.getCount), new[] { typeof(AnItem) })]
    public static class InventoryGetCountPatch
    {
        static bool _counting;

        [HarmonyPostfix]
        public static void Postfix(AnItem item, ref int __result)
        {
            if (__result > 0 || item == null || !NetGate.Live || _counting) return;
            var held = PartyKeyRing.FindInBag(item);
            if (held != null && held != item)
            {
                _counting = true;
                try { __result = InventoryManager.getCount(held); }
                catch { __result = 1; }
                finally { _counting = false; }
                return;
            }
            if (PartyKeyRing.Has(item))
                __result = 1;
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
            int n = number > 0 ? number : 1;
            if (PartyKeyRing.IsKeyOrObject(item) && n > 1) n = 1;
            if (NetGate.Host)
            {
                // Host put of unique already in box: absorb bag copy, do not stack.
                if (put && PartyKeyRing.IsKeyOrObject(item) && HostBoxStock(item) >= 1)
                {
                    try { InventoryManager.RemoveItem(item, n); } catch { }
                    PlaytestLog.Event("StorageBox", "host put absorb unique item="
                        + (int)SafeEnum(item));
                    FlushHostBoxBlob();
                    return false;
                }
                // Prefix only flags; Postfix pushes blob after native mutates host box.
                LanNetworkManager.Instance.StorageSync.RequestSend();
                return true;
            }
            int enumVal = SafeEnum(item);
            if (enumVal < 0) return false;
            // One storage transaction in flight per client: a double press before the ack would
            // otherwise box twice but only remove once. Put reserves the bag copy up front.
            if (!StorageTxn.TryBegin(put, item, enumVal, ref n)) return false;
            LanNetworkManager.Instance.SendInteractionRequest(
                0,
                put ? InteractionKind.StoragePut : InteractionKind.StorageTake,
                enumVal,
                n);
            return false;
        }

        static int SafeEnum(AnItem item)
        {
            try { return (int)item._item; } catch { return -1; }
        }

        static int HostBoxStock(AnItem item)
        {
            int have = 0;
            try { have = InventoryManager.boxContainsItemCount(item); }
            catch
            {
                try { if (InventoryManager.boxContainsItem(item)) have = 1; } catch { }
            }
            return have;
        }

        static void FlushHostBoxBlob()
        {
            if (NetGate.IsApplying || !NetGate.Live || !NetGate.Host) return;
            var net = LanNetworkManager.Instance;
            if (net == null) return;
            try
            {
                net.StorageSync.RequestSend();
                net.StorageSync.SendNow(net);
            }
            catch { }
        }

        // No-count overloads — storage UI can call these and would bypass the int gates.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.storeItem), new[] { typeof(AnItem) })]
        public static bool PrefixStore1(AnItem item) => GateBox(item, 1, true);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.retrieveItem), new[] { typeof(AnItem) })]
        public static bool PrefixRetrieve1(AnItem item) => GateBox(item, 1, false);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.boxItem), new[] { typeof(AnItem) })]
        public static bool PrefixBox1(AnItem item) => GateBox(item, 1, true);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.storeItem), new[] { typeof(AnItem) })]
        public static void PostStore1(AnItem item) => FlushHostBoxBlob();

        [HarmonyPostfix]
        [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.retrieveItem), new[] { typeof(AnItem) })]
        public static void PostRetrieve1(AnItem item) => FlushHostBoxBlob();

        [HarmonyPostfix]
        [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.boxItem), new[] { typeof(AnItem) })]
        public static void PostBox1(AnItem item) => FlushHostBoxBlob();

        [HarmonyPostfix]
        [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.storeItem), new[] { typeof(AnItem), typeof(int) })]
        public static void PostStore(AnItem item, int number) => FlushHostBoxBlob();

        [HarmonyPostfix]
        [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.retrieveItem), new[] { typeof(AnItem), typeof(int) })]
        public static void PostRetrieve(AnItem item, int number) => FlushHostBoxBlob();

        [HarmonyPostfix]
        [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.boxItem), new[] { typeof(AnItem), typeof(int) })]
        public static void PostBox(AnItem item, int number) => FlushHostBoxBlob();

        [HarmonyPostfix]
        [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.unboxItem), new[] { typeof(AnItem) })]
        public static void PostUnbox(AnItem item) => FlushHostBoxBlob();
    }

    static class DialoguerGate
    {
        static bool _flavorActive;
        static int _flavorId;

        public static void ClearFlavor()
        {
            _flavorActive = false;
            _flavorId = 0;
        }

        public static bool Start(int dialogueId)
        {
            if (NetGate.IsApplying)
            {
                // A story Start applied from the host / a relay replaces whatever local flavor line was up
                // (flavor never goes through ApplyPresentation). Left set, every later Continue on this peer
                // took the flavor branch and never reached the host.
                ClearFlavor();
                return true;
            }
            if (!NetGate.Live) return true;
            if (LocalInspect.DialoguerFlavor(dialogueId) || LocalInspect.InspectScreen())
            {
                _flavorId = dialogueId;
                if (dialogueId == (int)DialoguerDialogues.useItemDialogue)
                    BindUseItemName();
                else
                    PartyKeyRing.RestoreUiNames();
                _flavorActive = true;
                return true;
            }
            _flavorActive = false;
            if (NetGate.Host)
            {
                var story = LanNetworkManager.Instance.StorySync;
                // Duplicate Start of the same dialogue (a client request landed just before) is swallowed.
                if (!story.HostDialogueStart(dialogueId))
                    return false;
                story.BroadcastPresentation(StoryCmd.DialoguerStartId, 0, dialogueId, story.DialogueTag());
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
                if (_flavorId == (int)DialoguerDialogues.useItemDialogue)
                    BindUseItemName();
                else
                    PartyKeyRing.RestoreUiNames();
                return true;
            }
            var story = LanNetworkManager.Instance.StorySync;
            if (NetGate.Host)
            {
                // Host press: always valid, advances the step so in-flight client Continues for the old step drop.
                story.HostDialogueAdvance(-1, 0, false);
                story.BroadcastPresentation(StoryCmd.DialogueContinue, 0, choice, story.DialogueTag());
                return true;
            }
            // Carry the step this peer is on: with N players any of them may press, the host applies the first
            // request for a step and drops the rest as stale.
            LanNetworkManager.Instance.SendInteractionRequest(0, InteractionKind.DialogueContinue, choice,
                0, 0f, 0f, 0f, story.DialogueTag());
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
            var story = LanNetworkManager.Instance.StorySync;
            if (NetGate.Host)
            {
                story.HostDialogueAdvance(-1, 0, true);
                story.BroadcastPresentation(StoryCmd.DialogueEnd, 0, 0, story.DialogueTag());
                return true;
            }
            LanNetworkManager.Instance.SendInteractionRequest(0, InteractionKind.DialogueEnd,
                0, 0, 0f, 0f, 0f, story.DialogueTag());
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
                var all = WorldLookup.All<UseItemInteraction>();
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

        // Flavor sticky set in Start — clear if native throws after Prefix returned true.
        [HarmonyFinalizer]
        public static void Finalizer(Exception __exception)
        {
            if (__exception != null) DialoguerGate.ClearFlavor();
        }
    }

    [HarmonyPatch(typeof(Dialoguer), nameof(Dialoguer.StartDialogue), new[] { typeof(DialoguerDialogues) })]
    public static class DialoguerStartEnumPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(DialoguerDialogues dialogue) => DialoguerGate.Start((int)dialogue);

        [HarmonyFinalizer]
        public static void Finalizer(Exception __exception)
        {
            if (__exception != null) DialoguerGate.ClearFlavor();
        }
    }

    [HarmonyPatch(typeof(Dialoguer), nameof(Dialoguer.StartDialogue), new[] { typeof(int), typeof(DialoguerCallback) })]
    public static class DialoguerStartIntCbPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(int dialogueId) => DialoguerGate.Start(dialogueId);

        [HarmonyFinalizer]
        public static void Finalizer(Exception __exception)
        {
            if (__exception != null) DialoguerGate.ClearFlavor();
        }
    }

    [HarmonyPatch(typeof(Dialoguer), nameof(Dialoguer.StartDialogue), new[] { typeof(DialoguerDialogues), typeof(DialoguerCallback) })]
    public static class DialoguerStartEnumCbPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(DialoguerDialogues dialogue) => DialoguerGate.Start((int)dialogue);

        [HarmonyFinalizer]
        public static void Finalizer(Exception __exception)
        {
            if (__exception != null) DialoguerGate.ClearFlavor();
        }
    }

    [HarmonyPatch(typeof(Dialoguer), nameof(Dialoguer.ContinueDialogue), new[] { typeof(int) })]
    public static class DialoguerContinueIntPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(int choice) => DialoguerGate.Continue(choice);

        [HarmonyFinalizer]
        public static void Finalizer(Exception __exception)
        {
            if (__exception != null) DialoguerGate.ClearFlavor();
        }
    }

    [HarmonyPatch(typeof(Dialoguer), nameof(Dialoguer.ContinueDialogue), new System.Type[0])]
    public static class DialoguerContinuePatch
    {
        [HarmonyPrefix]
        public static bool Prefix() => DialoguerGate.Continue(0);

        [HarmonyFinalizer]
        public static void Finalizer(Exception __exception)
        {
            if (__exception != null) DialoguerGate.ClearFlavor();
        }
    }

    [HarmonyPatch(typeof(Dialoguer), nameof(Dialoguer.EndDialogue))]
    public static class DialoguerEndPatch
    {
        [HarmonyPrefix]
        public static bool Prefix() => DialoguerGate.End();

        // NetGate.IsApplying End path skips DialoguerGate.End clear — Finalizer always clears sticky.
        [HarmonyFinalizer]
        public static void Finalizer() => DialoguerGate.ClearFlavor();
    }
}
