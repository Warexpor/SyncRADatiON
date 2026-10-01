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
                // Host-run apply scopes (ApplyMultiCondition / ApplyCutscene / ApplyEventZone / party cheats) write
                // consequence flags too: they are committed so out-of-room peers get them before the next full dump.
                else if (NetGate.Host)
                    NoteEntry(story, e);
                return true;
            }
            if (NetGate.Host)
            {
                NoteEntry(story, e);
                return true;
            }
            // Client write outside an apply scope. Book / EventScreen flags keep their immediate request;
            // everything else (Interaction.trigger UnityEvents, cutscene coroutines, NPC / pickup flags) is
            // coalesced and forwarded, so the shared story gets it. The local write always happens.
            if (IsInspectOrigin())
            {
                if (!StorySyncService.ForwardSuppressed && !StorySyncService.SameAsLocal(e))
                    StorySyncService.SendFlag(LanNetworkManager.Instance, e);
                return true;
            }
            story.ClientForward(e);
            return true;
        }

        internal static void NoteEntry(StorySyncService story, StoryFlagEntry e)
        {
            switch (e.Kind)
            {
                case 0: story.NoteBool(e.Key, e.BoolVal); break;
                case 1: story.NoteInt(e.Key, e.IntVal); break;
                case 2: story.NoteFloat(e.Key, e.FloatVal); break;
                case 3: story.NoteString(e.Key, e.StringVal); break;
                default: story.NoteVector(e.Key, new Vector3(e.FloatVal, e.VecY, e.VecZ)); break;
            }
        }

        static bool IsInspectOrigin()
        {
            try
            {
                if (PlayerState.eventScreen) return true;
                var gs = PlayerState.gameState;
                return gs == PlayerState.gameStates.eventScreen || gs == PlayerState.gameStates.book;
            }
            catch (Exception e) { Guard.Swallow(e); }
            return false;
        }
    }

    // SaveManager.Save/Load/NewGame dump and restore *per-player* state (hp, position, enemies, minimap,
    // inventory, END statics) through SProgress.Set*: never forward those to the host.
    // (The key-ring masquerade is off for Save too: Inventory/Patches SaveManagerKeyRingScopePatch.)
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
            LanNetworkManager.Instance?.StorySync.OnSlotReplaced();
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
            LanNetworkManager.Instance?.StorySync.OnSlotReplaced();
        }
    }

    // Per-player state that the game writes into SProgress *outside* SaveManager.Save/Load (unload-time OnDisable
    // hooks, UI/minimap savers). Pseudo-C verified (Ghidra): EnemyController.OnDisable -> Save (hp / revives /
    // pos / rot / queuedForRespawn / permadeath per enemy), RadioManager.OnDisable + SaveState (RadioFreq),
    // HelpInputPrompts.OnDisable + SaveState (prompt flag), InventoryBase.SaveState (selectedSlot),
    // MinimapPOIManager.Save / PersistentMinimapManager.Save (minimap discovery), SaveGameScreenshotMaker.save
    // (screenshot path). Forwarded, a client scene change would flood the host with these (client puppets' default
    // permadeath=false would overwrite the host's dead state; radio freq would become shared): never forward them.
    // Shared puzzle/door *SaveState/OnDisable writers (Keypad3D, ConnectedDoors, CryoDoorLock, ...) are deliberately
    // NOT here: those are world state the host dedupes (SameAsLocal) and commits.
    [HarmonyPatch(typeof(EnemyController), "Save")]
    public static class EnemySaveScopePatch
    {
        [HarmonyPrefix] public static void Prefix() => StorySyncService.BeginSuppressForward();
        [HarmonyFinalizer] public static void Finalizer() => StorySyncService.EndSuppressForward();
    }

    [HarmonyPatch(typeof(RadioManager), "OnDisable")]
    public static class RadioDisableScopePatch
    {
        [HarmonyPrefix] public static void Prefix() => StorySyncService.BeginSuppressForward();
        [HarmonyFinalizer] public static void Finalizer() => StorySyncService.EndSuppressForward();
    }

    [HarmonyPatch(typeof(RadioManager), "SaveState")]
    public static class RadioSaveScopePatch
    {
        [HarmonyPrefix] public static void Prefix() => StorySyncService.BeginSuppressForward();
        [HarmonyFinalizer] public static void Finalizer() => StorySyncService.EndSuppressForward();
    }

    [HarmonyPatch(typeof(HelpInputPrompts), "OnDisable")]
    public static class HelpPromptsDisableScopePatch
    {
        [HarmonyPrefix] public static void Prefix() => StorySyncService.BeginSuppressForward();
        [HarmonyFinalizer] public static void Finalizer() => StorySyncService.EndSuppressForward();
    }

    [HarmonyPatch(typeof(HelpInputPrompts), "SaveState")]
    public static class HelpPromptsSaveScopePatch
    {
        [HarmonyPrefix] public static void Prefix() => StorySyncService.BeginSuppressForward();
        [HarmonyFinalizer] public static void Finalizer() => StorySyncService.EndSuppressForward();
    }

    [HarmonyPatch(typeof(InventoryBase), "SaveState")]
    public static class InventoryBaseSaveScopePatch
    {
        [HarmonyPrefix] public static void Prefix() => StorySyncService.BeginSuppressForward();
        [HarmonyFinalizer] public static void Finalizer() => StorySyncService.EndSuppressForward();
    }

    [HarmonyPatch(typeof(MinimapPOIManager), "Save")]
    public static class MinimapPoiSaveScopePatch
    {
        [HarmonyPrefix] public static void Prefix() => StorySyncService.BeginSuppressForward();
        [HarmonyFinalizer] public static void Finalizer() => StorySyncService.EndSuppressForward();
    }

    [HarmonyPatch(typeof(PersistentMinimapManager), "Save")]
    public static class PersistentMinimapSaveScopePatch
    {
        [HarmonyPrefix] public static void Prefix() => StorySyncService.BeginSuppressForward();
        [HarmonyFinalizer] public static void Finalizer() => StorySyncService.EndSuppressForward();
    }

    [HarmonyPatch(typeof(SaveGameScreenshotMaker), "save")]
    public static class ScreenshotSaveScopePatch
    {
        [HarmonyPrefix] public static void Prefix() => StorySyncService.BeginSuppressForward();
        [HarmonyFinalizer] public static void Finalizer() => StorySyncService.EndSuppressForward();
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
            // CalculatePlaystyle mutates Circle/Death from the local per-player GlobalStats and is only called from
            // Finale.determineEnding (Ghidra). The host runs it once; clients must NOT recompute, including inside
            // the DetermineEnding apply scope: they already hold the host's post-calculation END values
            // (StoryCommit sent from the determineEnding postfix) and only replay the presentation.
            if (!NetGate.Live) return true;
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

    // Ending start is party-wide: the host runs the native determineEnding FIRST (it calls CalculatePlaystyle, which
    // mutates Circle/Death), then a FINALIZER broadcasts the final END values + verdict (a finalizer, not a postfix:
    // the native body sets `once` first and has a null-ref path after it, and a throw must not leave the ending
    // unbroadcast and unrepeatable); a client asks the host. `once` (per Finale) means a second call is a native
    // no-op, so only the call that actually ran broadcasts. A host with nobody connected only records the ending.
    [HarmonyPatch(typeof(Finale), nameof(Finale.determineEnding))]
    public static class FinaleDetermineEndingPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Finale __instance, out bool __state)
        {
            __state = false;
            if (NetGate.IsApplying || !NetGate.Live) return true;
            var net = LanNetworkManager.Instance;
            if (NetGate.Host)
            {
                try { __state = __instance != null && !__instance.once; }
                catch (Exception e) { Guard.Swallow(e); __state = true; }
                return true;
            }
            if (!NetGate.Party) return true;
            net.SendInteractionRequest(0, InteractionKind.InspectFlag, 100 + (int)StoryCmd.DetermineEnding);
            return false;
        }

        [HarmonyFinalizer]
        public static void Finalizer(bool __state)
        {
            if (!__state || !NetGate.Host || NetGate.IsApplying) return;
            try
            {
                var net = LanNetworkManager.Instance;
                net.StorySync.HostBroadcastEnding(net);
            }
            catch (Exception e) { Guard.Swallow(e); }
        }
    }

    [HarmonyPatch(typeof(Finale), nameof(Finale.goToPenny))]
    public static class FinaleGoToPennyPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (NetGate.IsApplying || !NetGate.Party) return true;
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
            if (!NetGate.Party) return;
            var story = LanNetworkManager.Instance.StorySync;
            // A cheat run inside a replayed presentation (goToPenny on a client) must not relay again, but it must
            // stamp the dedupe window: the host's own PartyCheat relay of the same goto / sethp arrives right after
            // and would otherwise run it a second time on this peer.
            if (NetGate.IsApplying)
            {
                story.StampPartyCheat(cheat);
                return;
            }
            story.OnScriptedCheat(cheat);
        }
    }

    // Every Dialoguer dialogue is local to the peer that opened it: shipped content only starts flavor ids (all 1282
    // serialized Dialogue._dialogue are 0 / 20 / 22, InteractiveLockSingle forces 20, ItemPickup uses 6 / 17 / 25 / 26),
    // so there is no host-authored dialogue to mirror. Continue / End always run natively on the pressing peer: a
    // forwarded hold-cancel would close every peer's line / yes-no prompt. What is left is the local key-ring name
    // binding: Dialoguer global strings s0 / s3 hold the item name a use / inspect line shows, and story dumps overwrite
    // them with the host's last use.
    // Detours reaching this gate (RVA folding, script.json): Dialoguer.StartDialogue(int, cb) = 0x426320 =
    // DialoguerDialogueManager.startDialogueWithCallback (every native Dialogue.StartDialogue / ItemPickup line), and
    // Dialoguer.ContinueDialogue(int) = 0x425C60 = DialoguerDialogueManager.continueDialogue (every native continue).
    // Both prefixes always return true, so a nested start / continue through the shared function is harmless.
    static class DialoguerGate
    {
        static bool _flavorActive;
        static int _flavorId;

        public static void ClearFlavor()
        {
            _flavorActive = false;
            _flavorId = 0;
        }

        /// <summary>SessionReset (session scope): the local line bookkeeping.</summary>
        internal static void ResetSession() => ClearFlavor();

        public static void Start(int dialogueId)
        {
            if (!NetGate.Live) return;
            if (NetGate.IsApplying)
            {
                // A line the mod opens itself (WorldPickup "gone" message) binds its own name first: leave it alone.
                ClearFlavor();
                return;
            }
            _flavorId = dialogueId;
            _flavorActive = true;
            BindNames();
        }

        public static void Continue()
        {
            if (NetGate.IsApplying || !NetGate.Live) return;
            if (_flavorActive || LocalInspect.InspectScreen()) BindNames();
        }

        static void BindNames()
        {
            if (_flavorId == (int)DialoguerDialogues.useItemDialogue)
                BindUseItemName();
            else
                PartyKeyRing.RestoreUiNames();
        }

        static void BindUseItemName()
        {
            try
            {
                PartyKeyRing.BindUseDialogue(UseItemInteraction.currentUseItem);
            }
            catch (Exception e) { Guard.Swallow(e); }
            try
            {
                var all = WorldLookup.All<UseItemInteraction>();
                if (all == null) return;
                for (int i = 0; i < all.Length; i++)
                {
                    var u = all[i];
                    if (u == null) continue;
                    bool inRange = false;
                    try { inRange = u.inter != null && u.inter.inRange; } catch (Exception e) { Guard.Swallow(e); }
                    if (!inRange && !u.unlocked) continue;
                    UseItemDialogueNamePatch.Bind(u);
                    return;
                }
            }
            catch (Exception e) { Guard.Swallow(e); }
        }
    }

    // Only the int overloads: the DialoguerDialogues overloads are the same native functions (see DialoguerGate).
    [HarmonyPatch(typeof(Dialoguer), nameof(Dialoguer.StartDialogue), new[] { typeof(int) })]
    public static class DialoguerStartIntPatch
    {
        [HarmonyPrefix]
        public static void Prefix(int dialogueId) => DialoguerGate.Start(dialogueId);
    }

    [HarmonyPatch(typeof(Dialoguer), nameof(Dialoguer.StartDialogue), new[] { typeof(int), typeof(DialoguerCallback) })]
    public static class DialoguerStartIntCbPatch
    {
        [HarmonyPrefix]
        public static void Prefix(int dialogueId) => DialoguerGate.Start(dialogueId);
    }

    // Dialogue.CallDialogue (unique RVA 0x423F30, EventScreen lines) calls DialoguerDialogueManager.startDialogue
    // directly, so neither Dialoguer.StartDialogue detour sees it.
    [HarmonyPatch(typeof(Dialogue), nameof(Dialogue.CallDialogue))]
    public static class DialogueCallDialoguePatch
    {
        [HarmonyPrefix]
        public static void Prefix(Dialogue __instance)
        {
            if (__instance == null) return;
            int id;
            try { id = (int)__instance._dialogue; }
            catch (Exception e) { Guard.Swallow(e); return; }
            DialoguerGate.Start(id);
        }
    }

    [HarmonyPatch(typeof(Dialoguer), nameof(Dialoguer.ContinueDialogue), new[] { typeof(int) })]
    public static class DialoguerContinueIntPatch
    {
        [HarmonyPrefix]
        public static void Prefix() => DialoguerGate.Continue();
    }
}
