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
            try { if (PlayerState.eventScreen) return true; } catch (Exception e) { Guard.Swallow(e); }
            try
            {
                var gs = PlayerState.gameState;
                if (gs == PlayerState.gameStates.eventScreen || gs == PlayerState.gameStates.book)
                    return true;
            }
            catch (Exception e) { Guard.Swallow(e); }
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
            try
            {
                var story = LanNetworkManager.Instance?.StorySync;
                story?.ResetEndBase();
                // The live slot was replaced wholesale (wipe reload / Continue): clients get a FULL, authoritative dump.
                if (NetGate.Host) story?.RequestAuthoritativeFull();
            }
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
            try
            {
                var story = LanNetworkManager.Instance?.StorySync;
                story?.ResetEndBase();
                if (NetGate.Host) story?.RequestAuthoritativeFull();
            }
            catch (System.Exception ex) { StorySyncService.WarnOnce("NewGame ResetEndBase", ex); }
        }
    }

    // Per-player state that the game writes into SProgress *outside* SaveManager.Save/Load (unload-time OnDisable
    // hooks, UI/minimap savers). Pseudo-C verified (Ghidra): EnemyController.OnDisable -> Save (hp / revives /
    // pos / rot / queuedForRespawn / permadeath per enemy), RadioManager.OnDisable + SaveState (RadioFreq),
    // HelpInputPrompts.OnDisable + SaveState (prompt flag), InventoryBase.SaveState (selectedSlot),
    // MinimapPOIManager.Save / PersistentMinimapManager.Save (minimap discovery), SaveGameScreenshotMaker.save
    // (screenshot path). A client scene change used to flood the host with these (client puppets' default
    // permadeath=false could overwrite the host's dead state; radio freq became shared): never forward them.
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

        /// <summary>SessionReset: re-entrancy flag, cleared in case an exception path ever left it set.</summary>
        internal static void ResetSession() => _counting = false;

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
                    try { InventoryManager.RemoveItem(item, n); } catch (Exception e) { Guard.Swallow(e); }
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
                unchecked((ulong)StorageTxn.CurrentTxn),
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
                try { if (InventoryManager.boxContainsItem(item)) have = 1; } catch (Exception e) { Guard.Swallow(e); }
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
            catch (Exception e) { Guard.Swallow(e); }
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
            // The dialogue (and its end callback) is over or replaced. NOT _pendingCb: a client request for the next
            // start can be in flight while an unrelated End finalizer runs this.
            _activeCb = false;
        }

        /// <summary>SessionReset (session scope): flavor + every held callback + the re-entrancy / local-end counters.</summary>
        internal static void ResetSession()
        {
            ClearFlavor();
            _pendingCb = null;
            _pendingCbId = 0;
            _depth = 0;
            _localEnd = 0;
        }

        /// <summary>Scene change: a held callback targets an object (Dialogue component) of the unloaded scene.</summary>
        internal static void OnSceneChanged()
        {
            _pendingCb = null;
            _pendingCbId = 0;
            _activeCb = false;
        }

        // --- Re-entrancy (finding 1) ---------------------------------------------------------------------
        // Dialoguer.StartDialogue(int) and (DialoguerDialogues) share RVA 0x42AF20, and the callback overloads share
        // 0x426320 (script.json): two detours on one native function would run both prefixes per call, the second
        // HostDialogueStart(id) returning false inside its dedupe window and skipping the native body on the host.
        // Only the int overloads are patched now (the enum overloads are the same machine code, so the one detour
        // covers them) and this depth guard makes any nested start of the same id a pass-through regardless.
        static int _depth;
        static int _depthId;
        static float _depthAt;

        // Dialogue callbacks (StartDialogue(int, DialoguerCallback)) of a client-initiated start: the client only
        // *requests* the start, so the callback is kept and handed to the replayed start from the host.
        static DialoguerCallback _pendingCb;
        static int _pendingCbId;
        static float _pendingCbAt;

        public static DialoguerCallback TakeCallback(int dialogueId)
        {
            var cb = _pendingCb;
            bool ok = cb != null && _pendingCbId == dialogueId && Time.unscaledTime - _pendingCbAt < 15f;
            _pendingCb = null;
            _pendingCbId = 0;
            // A destroyed target would throw inside native code when the end callback runs.
            return ok && CallbackTargetAlive(cb) ? cb : null;
        }

        static bool CallbackTargetAlive(DialoguerCallback cb)
        {
            try
            {
                var target = cb.m_target;
                if (target == null) return true; // static callback
                var uo = target.TryCast<UnityEngine.Object>();
                if ((object)uo == null) return true; // plain managed target, nothing Unity-destroyable
                return uo != null; // overloaded != : false once the native object is destroyed
            }
            catch (Exception e) { Guard.Swallow(e); return false; }
        }

        // The callback of a client-initiated start is running on this peer: its Continue / End apply runs it under
        // apply, so the apply gets an author scope (its SProgress writes are the dialogue's consequences, and the host
        // started the dialogue without the callback).
        static bool _activeCb;
        public static bool HoldsCallback => _activeCb;
        public static void NoteActiveCallback(int dialogueId) => _activeCb = true;

        static int _localEnd;
        /// <summary>Scope: this peer alone leaves the dialogue (damage cancel); never forwarded to the host / party.</summary>
        public static void BeginLocalEnd() => _localEnd++;
        public static void EndLocalEnd() { if (_localEnd > 0) _localEnd--; }

        public static bool Start(int dialogueId, DialoguerCallback callback, out bool entered)
        {
            entered = false;
            if (_depth > 0 && _depthId == dialogueId && Time.unscaledTime - _depthAt < 2f)
                return true;
            // One line per start that reaches the gate: confirms in-game that the DialoguerDialogues overloads (same
            // native function as the int ones, only the int ones are patched) go through this detour as well.
            if (NetGate.Live && !NetGate.IsApplying)
                PlaytestLog.Event("Story", "StartDialogue gate id=" + dialogueId
                    + (callback != null ? " cb" : "") + " role=" + (NetGate.Host ? "host" : "client"));
            bool run = StartCore(dialogueId, callback);
            if (run)
            {
                _depth++;
                _depthId = dialogueId;
                _depthAt = Time.unscaledTime;
                entered = true;
            }
            return run;
        }

        public static void Exit(bool entered)
        {
            if (entered && _depth > 0) _depth--;
        }

        static bool StartCore(int dialogueId, DialoguerCallback callback)
        {
            if (NetGate.IsApplying)
            {
                // A story Start applied from the host / a relay replaces whatever local flavor line was up
                // (flavor never goes through ApplyPresentation). Left set, every later Continue on this peer
                // took the flavor branch and never reached the host.
                ClearFlavor();
                return true;
            }
            // Offline is vanilla. A host with nobody connected still does the replay bookkeeping (a later joiner replays
            // the dialogue) but never swallows a start and sends nothing (BroadcastPresentation only notes it).
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
                if (!story.HostDialogueStart(dialogueId) && NetGate.Party)
                    return false;
                story.BroadcastPresentation(StoryCmd.DialoguerStartId, 0, dialogueId, story.DialogueTag());
                return true;
            }
            if (!NetGate.Party) return true;
            PlaytestLog.Event("Story", "request Dialoguer " + dialogueId);
            if (callback != null)
            {
                _pendingCb = callback;
                _pendingCbId = dialogueId;
                _pendingCbAt = Time.unscaledTime;
            }
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
            if (!NetGate.Party) return true;
            // Carry the step this peer is on: with N players any of them may press, the host applies the first
            // request for a step and drops the rest as stale.
            LanNetworkManager.Instance.SendInteractionRequest(0, InteractionKind.DialogueContinue, choice,
                0, 0f, 0f, 0f, story.DialogueTag());
            return false;
        }

        public static bool End()
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (_localEnd > 0) return true; // damage cancel: only this peer leaves the dialogue
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
            if (!NetGate.Party) return true;
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
        public static bool Prefix(int dialogueId, out bool __state) => DialoguerGate.Start(dialogueId, null, out __state);

        // Flavor sticky set in Start — clear if native throws after Prefix returned true.
        [HarmonyFinalizer]
        public static void Finalizer(Exception __exception, bool __state)
        {
            DialoguerGate.Exit(__state);
            if (__exception != null) DialoguerGate.ClearFlavor();
        }
    }

    [HarmonyPatch(typeof(Dialoguer), nameof(Dialoguer.StartDialogue), new[] { typeof(int), typeof(DialoguerCallback) })]
    public static class DialoguerStartIntCbPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(int dialogueId, DialoguerCallback callback, out bool __state) =>
            DialoguerGate.Start(dialogueId, callback, out __state);

        [HarmonyFinalizer]
        public static void Finalizer(Exception __exception, bool __state)
        {
            DialoguerGate.Exit(__state);
            if (__exception != null) DialoguerGate.ClearFlavor();
        }
    }

    // Dialogue.CallDialogue (unique RVA 0x423F30) calls DialoguerDialogueManager.startDialogue directly, so neither
    // Dialoguer.StartDialogue detour sees it (a scene UnityEvent / EventScreen can call it). Same start gate: a client
    // asks the host (native body skipped, no half-set dialogue state), the host dedupes + broadcasts; flavor / EventScreen
    // lines stay local through the gate's own checks.
    [HarmonyPatch(typeof(Dialogue), nameof(Dialogue.CallDialogue))]
    public static class DialogueCallDialoguePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Dialogue __instance, out bool __state)
        {
            __state = false;
            if (__instance == null) return true;
            int id;
            try { id = (int)__instance._dialogue; }
            catch (Exception e) { Guard.Swallow(e); return true; }
            return DialoguerGate.Start(id, null, out __state);
        }

        [HarmonyFinalizer]
        public static void Finalizer(Exception __exception, bool __state)
        {
            DialoguerGate.Exit(__state);
            if (__exception != null) DialoguerGate.ClearFlavor();
        }
    }

    // BlackSleekGuiSubs listens to the LOCAL player's damage and ends the dialogue through Dialoguer.EndDialogue,
    // which the End gate would forward (DialogueEnd ends it for everyone). Damage only takes this peer out.
    [HarmonyPatch(typeof(BlackSleekGuiSubs), "CancelDialogueOnDamageReceived")]
    public static class CancelDialogueOnDamagePatch
    {
        [HarmonyPrefix] public static void Prefix() => DialoguerGate.BeginLocalEnd();
        [HarmonyFinalizer] public static void Finalizer() => DialoguerGate.EndLocalEnd();
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
