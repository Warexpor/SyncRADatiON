// Host validates client interaction intents and applies native world mutations.
using System.Reflection;
using SyncRADation.Patches;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    public static class InteractionSyncService
    {
        public static void HandleRequest(InteractionRequestMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || net.Role != NetworkRole.Host) return;

            ulong id = unchecked((ulong)msg.WorldId);
            bool ok = false;
            string reason = "";

            // Peer-gone mid-flight: DroppedPickup / StorageTake must not mutate shared
            // stock when the claimer can no longer receive grant/ack (floor/box item loss).
            // StoragePut still applies — boxing preserves the item for the party.
            // Mirrors WorldPickupNetHandlers HasPeer guard (0.5.11).
            if (msg.SenderPlayerId != net.LocalPlayerId
                && (msg.Kind == InteractionKind.DroppedPickup
                    || msg.Kind == InteractionKind.StorageTake)
                && !net.HasPeer(msg.SenderPlayerId))
            {
                reason = "peer gone";
                PlaytestLog.Event("Interact", "FAIL " + msg.Kind
                    + " from=" + msg.SenderPlayerId
                    + " id=" + id.ToString("X16")
                    + " peer gone");
                long earlyAck = msg.WorldId;
                if (msg.Kind == InteractionKind.DroppedPickup && earlyAck == 0 && msg.Int0 != 0)
                    earlyAck = msg.Int0;
                net.SendInteractionAck(msg.SenderPlayerId, earlyAck, msg.Kind, false, reason);
                return;
            }

            try
            {
                switch (msg.Kind)
                {
                    case InteractionKind.EventZone:
                        ok = ApplyEventZone(id);
                        break;
                    case InteractionKind.UseItem:
                        ok = ApplyUseItem(id, msg.SenderPlayerId, out string consumeReason);
                        if (!ok) reason = "no key";
                        else if (!string.IsNullOrEmpty(consumeReason)) reason = consumeReason;
                        break;
                    case InteractionKind.UseItemMulti:
                        ok = ApplyUseItemMulti(id, msg.SenderPlayerId, out string multiReason);
                        if (!ok) reason = "no key";
                        else if (!string.IsNullOrEmpty(multiReason)) reason = multiReason;
                        break;
                    case InteractionKind.KeypadSubmit:
                        ok = ApplyKeypad(id);
                        break;
                    case InteractionKind.DialogueStart:
                        if (LocalInspect.DialoguerFlavor(msg.Int0))
                        {
                            ok = true;
                            break;
                        }
                        if (id == 0 && msg.Int0 != 0)
                        {
                            // Two players can trigger the same story dialogue at once: first Start wins.
                            if (!net.StorySync.HostDialogueStart(msg.Int0))
                            {
                                reason = "dup-start";
                                ok = true;
                                break;
                            }
                            NetGate.BeginApply();
                            try { Dialoguer.StartDialogue(msg.Int0); }
                            finally { NetGate.EndApply(); }
                            net.StorySync.BroadcastPresentation(StoryCmd.DialoguerStartId, 0, msg.Int0,
                                net.StorySync.DialogueTag());
                            ok = true;
                        }
                        else
                            ok = true;
                        break;
                    case InteractionKind.CutsceneStart:
                        ok = ApplyCutscene(id, net);
                        break;
                    case InteractionKind.EventScreenStart:
                    case InteractionKind.EventScreenExit:
                    case InteractionKind.BookMemory:
                    case InteractionKind.BookOpen:
                        ok = true;
                        break;
                    case InteractionKind.CutsceneSkip:
                        ok = ApplyCutsceneSkip(id, net);
                        break;
                    case InteractionKind.DialogueContinue:
                    {
                        // Text = "dialogueId:step" the sender was on. Any player may advance; only the
                        // first request for a given step is applied, the other N-1 are stale and dropped.
                        int reqId, reqStep;
                        if (!StorySyncService.TryParseTag(msg.Text, out reqId, out reqStep))
                        {
                            reqId = -1;
                            reqStep = 0;
                        }
                        if (!net.StorySync.HostDialogueAdvance(reqId, reqStep, false))
                        {
                            reason = "stale";
                            ok = true;
                            break;
                        }
                        NetGate.BeginApply();
                        try
                        {
                            if (msg.Int0 != 0) Dialoguer.ContinueDialogue(msg.Int0);
                            else Dialoguer.ContinueDialogue();
                        }
                        finally { NetGate.EndApply(); }
                        net.StorySync.BroadcastPresentation(StoryCmd.DialogueContinue, 0, msg.Int0,
                            net.StorySync.DialogueTag());
                        ok = true;
                        break;
                    }
                    case InteractionKind.DialogueEnd:
                    {
                        int reqId, reqStep;
                        if (!StorySyncService.TryParseTag(msg.Text, out reqId, out reqStep))
                        {
                            reqId = -1;
                            reqStep = 0;
                        }
                        if (!net.StorySync.HostDialogueAdvance(reqId, reqStep, true))
                        {
                            reason = "stale";
                            ok = true;
                            break;
                        }
                        NetGate.BeginApply();
                        try { Dialoguer.EndDialogue(); }
                        finally { NetGate.EndApply(); }
                        net.StorySync.BroadcastPresentation(StoryCmd.DialogueEnd, 0, 0, net.StorySync.DialogueTag());
                        ok = true;
                        break;
                    }
                    case InteractionKind.StoragePut:
                        ok = ApplyStorage(msg, put: true, out reason);
                        break;
                    case InteractionKind.StorageTake:
                        ok = ApplyStorage(msg, put: false, out reason);
                        break;
                    case InteractionKind.Gunshot:
                        ApplyGunshot(new Vector3(msg.Float0, msg.Float1, msg.Float2), net);
                        ok = true;
                        break;
                    case InteractionKind.MultiCondition:
                        ok = ApplyMultiCondition(id, msg.Int0);
                        break;
                    case InteractionKind.SceneFollowRequest:
                        ok = SceneFollowService.TryApplyRequest(msg.Text);
                        if (!ok) reason = "unknown scene";
                        break;
                    case InteractionKind.CutsceneProceed:
                        ok = ApplyCutsceneProceed(id, net);
                        break;
                    case InteractionKind.DroppedPickup:
                        ok = net.TryClaimDropped(msg.Int0, msg.SenderPlayerId, out reason);
                        break;
                    case InteractionKind.InspectFlag:
                        ok = ApplyInspectFlag(msg);
                        break;
                }
            }
            catch (System.Exception ex)
            {
                reason = ex.Message;
                ModRuntime.Log?.Warning("[Interact] " + msg.Kind + ": " + ex.Message);
            }

            if (msg.Kind != InteractionKind.Gunshot)
                PlaytestLog.Event("Interact", (ok ? "ok " : "FAIL ") + msg.Kind
                    + " from=" + msg.SenderPlayerId
                    + " id=" + unchecked((ulong)msg.WorldId).ToString("X16")
                    + (string.IsNullOrEmpty(reason) ? "" : " " + reason));
            // DroppedPickup: echo drop key in WorldId (request Int0) so client FAIL revert matches.
            long ackId = msg.WorldId;
            if (msg.Kind == InteractionKind.DroppedPickup && ackId == 0 && msg.Int0 != 0)
                ackId = msg.Int0;
            net.SendInteractionAck(msg.SenderPlayerId, ackId, msg.Kind, ok, reason);
        }

        private static bool ApplyEventZone(ulong id)
        {
            var z = Find<EventZone>(id);
            if (z == null) return false;
            if (LocalInspect.LockWorld(z.gameObject)) return true;
            if (z.triggered) return true;
            z.triggered = true;
            SyncRADation.Patches.EventZonePatch.MarkFired(id);
            if (LocalInspect.InLocalRoom(z.gameObject))
            {
                try { if (z.onInRange != null) z.onInRange.Invoke(); } catch { }
            }
            else
                PlaytestLog.Event("Interact", "EventZone other-room id=" + id.ToString("X16"));
            LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.EventZoneFire, id, 0, "");
            return true;
        }

        private static bool ApplyUseItem(ulong id, int senderId, out string consumeReason)
        {
            consumeReason = "";
            var u = Find<UseItemInteraction>(id);
            if (u == null)
            {
                PlaytestLog.Event("Interact", "UseItem other-scene id=" + id.ToString("X16")
                    + " from=" + senderId);
                return true;
            }
            // Already unlocked (e.g. host-local Dialoguer path): still revoke if
            // ConsumesKey — host OnLocalUnlocked pre-0.5.17 never ran ApplyUseItem.
            if (u.unlocked && !u.repeatable)
            {
                TryRevokeUseItemKey(u);
                return true;
            }

            AnItem key = u.key;
            if (key != null && !PartyKeyRing.LocalOrRingHas(key))
            {
                var net = LanNetworkManager.Instance;
                int localId = net != null ? net.LocalPlayerId : 0;
                if (senderId == localId || senderId < 0) return false;
                try { PartyKeyRing.Note(key._item); } catch { return false; }
                PlaytestLog.Event("KeyRing", "trust UseItem from=" + senderId + " " + key._item);
            }

            // Do not latch unlocked here — SnapUseItemWorld rising-edge needs
            // wasUnlocked=false so host Apply (client UseItem) Invokes onSuccessful
            // (Disk InsertDisk* / Tarot PlaceCard*). Snap sets unlocked + doors.
            PuzzleSyncService.TryUnlockDoors(u.gameObject);
            try { PuzzleSyncService.SnapUseItemWorld(u); } catch { }
            try
            {
                var netPuzzle = LanNetworkManager.Instance;
                if (netPuzzle != null)
                    netPuzzle.PuzzleSync.Emit(PuzzleType.UseItemInteraction, id, u);
            }
            catch { }

            bool consumes = UnlockInteractiveLocks(u, key);

            if (consumes)
            {
                bool hostHad = PartyKeyRing.InLocalBag(key);
                ConsumeKey(key);
                if (!hostHad)
                    consumeReason = "consume:" + (int)key._item;
            }
            else
            {
                PartyKeyRing.Note(key);
            }
            PartyKeyRing.Broadcast();
            return true;
        }

        private static bool ApplyUseItemMulti(ulong id, int senderId, out string consumeReason)
        {
            consumeReason = "";
            var m = Find<UseItemMultiInteraction>(id);
            if (m == null) return false;

            try
            {
                var list = m.Interactions;
                if (list != null)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        var u = list[i];
                        if (u == null) continue;
                        AnItem key = u.key;
                        if (key == null || PartyKeyRing.LocalOrRingHas(key)) continue;
                        var net = LanNetworkManager.Instance;
                        int localId = net != null ? net.LocalPlayerId : 0;
                        if (senderId == localId || senderId < 0) return false;
                        try { PartyKeyRing.Note(key._item); } catch { return false; }
                        PlaytestLog.Event("KeyRing", "trust UseItemMulti from=" + senderId + " " + key._item);
                    }
                }
            }
            catch { return false; }

            var ready = typeof(UseItemMultiInteraction).GetMethod("ready",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (ready == null) return false;

            NetGate.BeginApply();
            try { ready.Invoke(m, null); }
            finally { NetGate.EndApply(); }

            var parts = new System.Collections.Generic.List<string>();
            try
            {
                var list = m.Interactions;
                if (list != null)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        var u = list[i];
                        if (u == null) continue;
                        AnItem key = u.key;
                        if (key == null) continue;
                        bool consumes = UnlockInteractiveLocks(u, key);
                        if (consumes)
                        {
                            bool hostHad = PartyKeyRing.InLocalBag(key);
                            ConsumeKey(key);
                            if (!hostHad)
                                parts.Add((int)key._item + ":1");
                        }
                        else
                            PartyKeyRing.Note(key);
                    }
                }
            }
            catch { }
            if (parts.Count > 0)
                consumeReason = "consume:" + string.Join("|", parts);
            PartyKeyRing.Broadcast();
            return true;
        }

        private static bool ApplyCutsceneProceed(ulong id, LanNetworkManager net)
        {
            var cut = Find<CutsceneCut>(id);
            if (cut == null) return false;
            if (LocalInspect.InLocalRoom(cut.gameObject))
            {
                NetGate.BeginApply();
                try { cut.Proceed(); }
                finally { NetGate.EndApply(); }
            }
            else
                PlaytestLog.Event("Interact", "CutsceneProceed other-room id=" + id.ToString("X16"));
            net.StorySync.BroadcastPresentation(StoryCmd.CutsceneProceed, id, 0, "");
            return true;
        }

        private static void ConsumeKey(AnItem key)
        {
            // Ring drop + EnsureInBag mirror strip on all peers via CraftRevokeSentinel fan-out.
            try { PartyKeyRing.RevokeConsumed(key._item); } catch { }
        }

        /// <summary>
        /// Host-local UseItem unlock (Dialoguer onMessageEvent/dialogueOver) never sends
        /// InteractionRequest — only clients do. Run the same ConsumesKey ring revoke
        /// ApplyUseItem would, so N-peer EnsureInBag mirrors drop (protocol 10).
        /// Idempotent with ApplyUseItem / CraftRevokeSentinel.
        /// </summary>
        public static void HostRevokeIfConsumed(UseItemInteraction u)
        {
            if (u == null) return;
            TryRevokeUseItemKey(u);
        }

        /// <summary>
        /// Host-local UseItemMulti.ready() runs natively (Prefix allows Host) without
        /// ApplyUseItemMulti — revoke each part ConsumesKey the same way.
        /// </summary>
        public static void HostRevokeUseItemMulti(UseItemMultiInteraction m)
        {
            if (m == null) return;
            try
            {
                var list = m.Interactions;
                if (list == null) return;
                bool any = false;
                for (int i = 0; i < list.Count; i++)
                {
                    var u = list[i];
                    if (u == null) continue;
                    if (TryRevokeUseItemKey(u)) any = true;
                }
                if (any)
                    PartyKeyRing.Broadcast();
            }
            catch { }
        }

        /// <returns>true if a ConsumesKey revoke ran</returns>
        static bool TryRevokeUseItemKey(UseItemInteraction u)
        {
            if (u == null) return false;
            AnItem key = null;
            try { key = u.key; } catch { }
            if (key == null) return false;
            bool consumes = UnlockInteractiveLocks(u, key);
            if (!consumes) return false;
            ConsumeKey(key);
            PartyKeyRing.Broadcast();
            return true;
        }

        static bool UnlockInteractiveLocks(UseItemInteraction u, AnItem key)
        {
            bool consumes = false;
            if (u == null) return false;
            try
            {
                var lockComp = u.GetComponent<InteractiveLock>()
                    ?? u.GetComponentInChildren<InteractiveLock>(true);
                if (lockComp != null)
                {
                    lockComp.locked = false;
                    consumes = consumes || (lockComp.ConsumesKey && key != null);
                }
            }
            catch { }
            try
            {
                var single = u.GetComponent<InteractiveLockSingle>()
                    ?? u.GetComponentInChildren<InteractiveLockSingle>(true)
                    ?? u.GetComponentInParent<InteractiveLockSingle>();
                if (single != null)
                {
                    consumes = consumes || (single.ConsumesKey && key != null);
                    try
                    {
                        if (single.master != null && DoorNative.AllowUnlock(single.master))
                            single.master.locked = false;
                    }
                    catch { }
                    try
                    {
                        if (single.door != null)
                        {
                            var master = single.master;
                            if (master == null || DoorNative.AllowUnlock(master))
                                single.door.locked = false;
                        }
                    }
                    catch { }
                }
            }
            catch { }

            // ConnectedDoors.ConsumesKey: native Unlock() (DoorNative.ApplyConnectedDoors)
            // only copies the flag + key onto AutoTraverseDoor InteractiveLockSingle
            // siblings — it never RemoveItem. UseItem is often a CD descendant while
            // those ILS sit on A/B siblings, so GetComponentInParent/Children from
            // UseItem never sees them. Same EnsureInBag bag-mirror softlock class as
            // InteractiveLock.ConsumesKey (0.5.14). Require key enum match so default
            // ConsumesKey=true templates with null key do not false-revoke.
            try
            {
                var cd = PuzzleDomainUtil.FindInParents<ConnectedDoors>(u.gameObject);
                if (cd != null && key != null)
                {
                    Items.itemlist want = key._item;
                    try
                    {
                        if (cd.ConsumesKey && cd.key != null && cd.key._item == want)
                            consumes = true;
                    }
                    catch { }
                    try
                    {
                        var singles = cd.GetComponentsInChildren<InteractiveLockSingle>(true);
                        if (singles != null)
                        {
                            for (int i = 0; i < singles.Length; i++)
                            {
                                var s = singles[i];
                                if (s == null || !s.ConsumesKey) continue;
                                try
                                {
                                    if (s.key != null && s.key._item == want)
                                        consumes = true;
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return consumes;
        }

        // Mirror LockSyncService.ApplyKeypad3D / ApplyRotKeypad: host KeypadSubmit
        // used to set solved/opening only, then poll-Emit to peers. Peers ran
        // openDoor + TryUnlockDoors; host never self-Applied → ConnectedDoors /
        // door mesh stayed sealed on host. Invoke consequences here (BeginApply
        // so Harmony Prefix/NoteSolved do not re-submit). Emit after for late join.
        private static bool ApplyKeypad(ulong id)
        {
            var k = Find<Keypad3D>(id);
            if (k != null)
            {
                bool was = k.solved;
                k.solved = true;
                k.opening = true;
                if (!was)
                {
                    NetGate.BeginApply();
                    try { k.openDoor(); }
                    catch { }
                    finally { NetGate.EndApply(); }
                }
                PuzzleSyncService.TryUnlockDoors(k.gameObject);
                try
                {
                    var net = LanNetworkManager.Instance;
                    if (net != null)
                        net.PuzzleSync.Emit(PuzzleType.Keypad3D, id, k);
                }
                catch { }
                return true;
            }
            var r = Find<ROT_Keypad>(id);
            if (r != null)
            {
                bool was = r.solved;
                r.solved = true;
                r.opening = true;
                // Peel: onSuccess → ConnectedDoors.Unlock + exitEvent + SetActive
                // + dimPOI. TryUnlockDoors covers CD parent walk; Invoke covers
                // the rest of the UnityEvent gate list (only when newly solved).
                if (!was)
                {
                    NetGate.BeginApply();
                    try
                    {
                        if (r.onSuccess != null)
                            r.onSuccess.Invoke();
                    }
                    catch { }
                    finally { NetGate.EndApply(); }
                }
                PuzzleSyncService.TryUnlockDoors(r.gameObject);
                try
                {
                    var net = LanNetworkManager.Instance;
                    if (net != null)
                        net.PuzzleSync.Emit(PuzzleType.ROT_Keypad, id, r);
                }
                catch { }
                return true;
            }
            var p = Find<PEN_Codepad>(id);
            if (p != null)
            {
                PuzzleSyncService.ApplyCodepadConsequences(p);
                return true;
            }
            return false;
        }

        private static bool ApplyCutscene(ulong id, LanNetworkManager net)
        {
            var c = Find<CutsceneManager>(id);
            if (c == null) return true;
            if (LocalInspect.AirlockCinematic(c.gameObject))
                return true;
            // N players can request the same cutscene at once (or the host just started it natively): the first
            // start inside the window wins, the rest are duplicates and must not restart / rebroadcast it.
            if (!InteractionSyncService.RememberStart(id))
            {
                PlaytestLog.Event("Interact", "CutsceneStart dup id=" + id.ToString("X16"));
                return true;
            }
            if (LocalInspect.InLocalRoom(c.gameObject))
            {
                NetGate.BeginApply();
                try { c.StartCutscene(); }
                finally { NetGate.EndApply(); }
                try
                {
                    if (c.unskippable) CutsceneSkippingUI.skippableCutscene = false;
                    else CutsceneSkippingUI.skippableCutscene = true;
                }
                catch { }
            }
            else
                PlaytestLog.Event("Interact", "CutsceneStart other-room id=" + id.ToString("X16"));
            net.StorySync.BroadcastPresentation(StoryCmd.CutsceneStart, id, 0, "");
            return true;
        }

        // Time-windowed stamps (not permanent sets): a repeatable cutscene must be able to start / skip
        // again, while duplicates of one trigger (N players, relay echo, resync replay) inside the window drop.
        private static readonly System.Collections.Generic.Dictionary<ulong, float> _skipStamp
            = new System.Collections.Generic.Dictionary<ulong, float>();
        private static readonly System.Collections.Generic.Dictionary<ulong, float> _startStamp
            = new System.Collections.Generic.Dictionary<ulong, float>();
        private static readonly System.Collections.Generic.Dictionary<ulong, float> _requestStamp
            = new System.Collections.Generic.Dictionary<ulong, float>();
        const float StartDedupeSeconds = 4f;
        const float SkipDedupeSeconds = 3f;
        const float SkipBlocksStartSeconds = 30f;
        const float RequestThrottleSeconds = 2f;

        public static void OnSceneChanged()
        {
            _skipStamp.Clear();
            _startStamp.Clear();
            _requestStamp.Clear();
        }

        private static bool ApplyCutsceneSkip(ulong id, LanNetworkManager net)
        {
            var c = Find<CutsceneManager>(id);
            if (c != null && LocalInspect.AirlockCinematic(c.gameObject))
                return true;
            if (!RememberSkip(id))
            {
                PlaytestLog.Event("Interact", "CutsceneSkip already done id=" + id.ToString("X16"));
                return true;
            }
            net.StorySync.BroadcastPresentation(StoryCmd.CutsceneSkip, id, 0, "");
            // Same gate as Start: a host that is not in the room / never started it does not run the skip events.
            if (c != null && SkipApplicable(c, id))
            {
                StorySyncService.BeginAuthorScope();
                try { NativeSkip(c); }
                finally { StorySyncService.EndAuthorScope(); }
            }
            else
                PlaytestLog.Event("Interact", "CutsceneSkip other-room / never-started id=" + id.ToString("X16"));
            return true;
        }

        internal static bool RememberSkip(ulong id)
        {
            if (id == 0) return true;
            float now = Time.unscaledTime, last;
            if (_skipStamp.TryGetValue(id, out last) && now - last < SkipDedupeSeconds)
                return false;
            _skipStamp[id] = now;
            return true;
        }

        internal static bool WasSkipped(ulong id)
        {
            if (id == 0) return false;
            float last;
            return _skipStamp.TryGetValue(id, out last) && Time.unscaledTime - last < SkipBlocksStartSeconds;
        }

        /// <summary>True the first time a start for this id is seen inside the dedupe window.</summary>
        internal static bool RememberStart(ulong id)
        {
            if (id == 0) return true;
            float now = Time.unscaledTime, last;
            if (_startStamp.TryGetValue(id, out last) && now - last < StartDedupeSeconds)
                return false;
            _startStamp[id] = now;
            return true;
        }

        /// <summary>
        /// Client: throttle repeat CutsceneStart *requests* only. Must not touch the start stamp, otherwise the
        /// host's presentation replay looks like a duplicate and the requester never plays the cutscene.
        /// </summary>
        internal static bool ShouldRequestStart(ulong id)
        {
            if (id == 0) return true;
            float now = Time.unscaledTime, last;
            if (_requestStamp.TryGetValue(id, out last) && now - last < RequestThrottleSeconds)
                return false;
            _requestStamp[id] = now;
            return true;
        }

        /// <summary>This peer's coroutine for the cutscene is live (started here, not completed).</summary>
        internal static bool StartedHere(CutsceneManager c)
        {
            if (c == null) return false;
            try { return c.cutscene != null && !c.completed; }
            catch (System.Exception ex) { StorySyncService.WarnOnce("StartedHere", ex); }
            return false;
        }

        /// <summary>A skip only means something on a peer that is in the room and started (or just started) the cutscene.</summary>
        internal static bool SkipApplicable(CutsceneManager c, ulong id)
        {
            if (c == null) return false;
            try { if (!LocalInspect.InLocalRoom(c.gameObject)) return false; }
            catch (System.Exception ex) { StorySyncService.WarnOnce("SkipApplicable room", ex); return false; }
            if (StartedHere(c)) return true;
            float last;
            return id != 0 && _startStamp.TryGetValue(id, out last) && Time.unscaledTime - last < StartDedupeSeconds * 4f;
        }

        internal static void NativeSkip(CutsceneManager c)
        {
            if (c == null) return;
            try { if (c.completed) return; } catch { }
            bool running = false;
            try { running = c.cutscene != null; } catch { }
            if (!running)
            {
                try { c.completed = true; } catch { }
                try
                {
                    if (c.onCutsceneSkip != null)
                        c.onCutsceneSkip.Invoke();
                }
                catch { }
                try { CutsceneSkippingUI.skippableCutscene = false; } catch { }
                return;
            }
            NetGate.BeginApply();
            try { c.Skip(); }
            catch
            {
                try
                {
                    if (c.skipper != null && c.skipper.skipEvent != null)
                        c.skipper.skipEvent.Invoke();
                }
                catch { }
            }
            finally { NetGate.EndApply(); }
            try { CutsceneSkippingUI.skippableCutscene = false; } catch { }
        }

        private static bool ApplyStorage(InteractionRequestMessage msg, bool put, out string reasonOut)
        {
            reasonOut = "";
            var item = InventoryManager.getItem((Items.itemlist)msg.Int0);
            if (item == null) return false;
            int n = msg.Int1 > 0 ? msg.Int1 : 1;

            int have = BoxStock(item);
            int enumVal = 0;
            try { enumVal = (int)item._item; } catch { }
            bool unique = PartyKeyRing.IsKeyOrObject(item);
            if (unique && n > 1) n = 1;
            var net = LanNetworkManager.Instance;

            // Host-authoritative stock. The client already reserved (put) or clamped (take) against
            // its own bag before sending, so put acks carry no consume — only take grants items.
            if (!put)
            {
                if (have <= 0)
                {
                    reasonOut = "empty";
                    PlaytestLog.Event("StorageBox", "take FAIL have=" + have + " need=" + n
                        + " item=" + msg.Int0 + " from=" + msg.SenderPlayerId);
                    // Refresh loser's LWW view so UI does not keep a ghost stack.
                    net?.StorageSync.RequestSend();
                    net?.StorageSync.SendNow(net);
                    return false;
                }
                // Partial take like native retrieveItem (clamps to the box stack).
                if (n > have) n = have;
            }
            else if (unique && have >= 1)
            {
                // Unique Key/Object already boxed (often via PartyKeyRing.EnsureInBag
                // re-seeding a bag copy). Absorb sender bag; do not stack the box.
                PlaytestLog.Event("StorageBox", "put absorb unique have=" + have
                    + " item=" + msg.Int0 + " from=" + msg.SenderPlayerId);
                net?.StorageSync.RequestSend();
                net?.StorageSync.SendNow(net);
                return true;
            }

            NetGate.BeginApply();
            try
            {
                // Box only — never host Elster bag. Sender bag was adjusted client-side.
                if (put) InventoryManager.boxItem(item, n);
                else n = TakeFromBox(item, n);
            }
            finally { NetGate.EndApply(); }
            net?.StorageSync.RequestSend();
            net?.StorageSync.SendNow(net);
            if (!put)
            {
                if (n <= 0)
                {
                    reasonOut = "empty";
                    return false;
                }
                reasonOut = "grant:" + enumVal + ":" + n;
            }
            return true;
        }

        /// <summary>
        /// Remove n from the box stack (native unboxItem drops the whole entry, retrieveItem would
        /// AddItem to the host bag — neither is right for a remote taker). Returns units removed.
        /// </summary>
        static int TakeFromBox(AnItem item, int n)
        {
            try
            {
                var dict = InventoryManager.boxItems;
                if (dict != null)
                {
                    AnItem key = null;
                    int cur = 0;
                    var en = dict.GetEnumerator();
                    while (en.MoveNext())
                    {
                        var k = en.Current.key;
                        if (k != null && k._item == item._item)
                        {
                            key = k;
                            cur = en.Current.value;
                            break;
                        }
                    }
                    en.Dispose();
                    if (key != null && cur > 0)
                    {
                        int take = n < cur ? n : cur;
                        if (cur - take <= 0) dict.Remove(key);
                        else dict[key] = cur - take;
                        return take;
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[StorageBox] take dict: " + ex.Message);
            }
            int have = BoxStock(item);
            if (have <= 0) return 0;
            try { InventoryManager.unboxItem(item); } catch { return 0; }
            return have < n ? have : n;
        }

        static int BoxStock(AnItem item)
        {
            if (item == null) return 0;
            int have = 0;
            try { have = InventoryManager.boxContainsItemCount(item); }
            catch
            {
                try { if (InventoryManager.boxContainsItem(item)) have = 1; } catch { }
            }
            return have;
        }

        private static void ApplyGunshot(Vector3 pos, LanNetworkManager net)
        {
            float range = 25f;
            try
            {
                var settingsArr = WorldLookup.All<ElsterSettings>();
                if (settingsArr != null)
                {
                    for (int i = 0; i < settingsArr.Length; i++)
                    {
                        if (settingsArr[i] != null)
                        {
                            range = settingsArr[i].hearRange;
                            break;
                        }
                    }
                }
            }
            catch { }
            float r2 = range * range;
            foreach (var kvp in WorldRegistry.AllEnemies())
            {
                var e = kvp.Value;
                if (e == null) continue;
                try
                {
                    if (e.state == EnemyController.enemystate.dead) continue;
                    if ((e.transform.position - pos).sqrMagnitude > r2) continue;
                    EnemySyncService.WakeForCombat(e);
                    Transform t = FindShooterTransform(pos, net);
                    if (t != null) e.playerPos = t;
                    e.WakeUpfromGunShot();
                }
                catch { }
            }
        }

        private static Transform FindShooterTransform(Vector3 pos, LanNetworkManager net)
        {
            Transform best = null;
            float bestD = 4f;
            var local = net.GetLocalPlayer();
            if (local != null)
            {
                float d = (local.transform.position - pos).sqrMagnitude;
                if (d < bestD) { bestD = d; best = local.transform; }
            }
            var pm = net.ProxyManager;
            foreach (int pid in net.GetRemotePlayerIds())
            {
                var p = pm.GetProxy(pid);
                if (p == null || p.GameObject == null) continue;
                float d = (p.GameObject.transform.position - pos).sqrMagnitude;
                if (d < bestD) { bestD = d; best = p.GameObject.transform; }
            }
            return best;
        }

        private static bool ApplyMultiCondition(ulong id, int kind)
        {
            var m = Find<MultiConditionEvent>(id);
            if (m == null) return false;
            NetGate.BeginApply();
            try
            {
                if (kind == 1) m.TryTrigger();
                else m.TryOnce();
            }
            finally { NetGate.EndApply(); }
            LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.MultiConditionFire, id, kind, "");
            return true;
        }

        /// <summary>InspectFlag Int0 &gt;= 100 carries a story request (100 + StoryCmd) on the same wire kind.</summary>
        private static bool ApplyStoryRequest(InteractionRequestMessage msg)
        {
            var net = LanNetworkManager.Instance;
            var story = net.StorySync;
            var cmd = (StoryCmd)(msg.Int0 - 100);
            switch (cmd)
            {
                case StoryCmd.EndDelta:
                    story.HostApplyEndDelta(msg.Text, msg.SenderPlayerId);
                    return true;
                case StoryCmd.PartyCheat:
                    story.HostPartyCheat(msg.Text, msg.SenderPlayerId);
                    return true;
                case StoryCmd.EndGraves:
                    // END_Graves.Graves() writes END_Manager.Ending = 1 directly (secret ending).
                    END_Manager.Ending = 1;
                    story.MarkDirty();
                    return true;
                case StoryCmd.DetermineEnding:
                    HostDetermineEnding(net);
                    return true;
                case StoryCmd.GoToPenny:
                    HostGoToPenny(net);
                    return true;
            }
            return false;
        }

        static Finale FirstFinale()
        {
            try
            {
                var finales = WorldLookup.All<Finale>();
                if (finales == null) return null;
                for (int i = 0; i < finales.Length; i++)
                {
                    if (finales[i] != null) return finales[i];
                }
            }
            catch (System.Exception ex) { StorySyncService.WarnOnce("FirstFinale", ex); }
            return null;
        }

        internal static void HostDetermineEnding(LanNetworkManager net)
        {
            // Finale.determineEnding has a per-object `once`; the broadcast has the same once-per-scene guard,
            // so two players reaching the finale at once start the ending a single time for everyone.
            if (!net.StorySync.HostBroadcastEnding(net)) return;
            var f = FirstFinale();
            if (f == null) return;
            NetGate.BeginApply();
            try { f.determineEnding(); }
            catch (System.Exception ex) { StorySyncService.WarnOnce("Host determineEnding", ex); }
            finally { NetGate.EndApply(); }
        }

        internal static void HostGoToPenny(LanNetworkManager net)
        {
            if (net.StorySync.Throttled("@goToPenny", 5f)) return;
            net.StorySync.BroadcastPresentation(StoryCmd.GoToPenny, 0, 0, "");
            var f = FirstFinale();
            if (f == null) return;
            NetGate.BeginApply();
            try { f.goToPenny(); }
            catch (System.Exception ex) { StorySyncService.WarnOnce("Host goToPenny", ex); }
            finally { NetGate.EndApply(); }
        }

        private static bool ApplyInspectFlag(InteractionRequestMessage msg)
        {
            if (msg.Int0 >= 100) return ApplyStoryRequest(msg);
            string key = msg.Text;
            string strVal = "";
            if (msg.Int0 == 3)
            {
                int split = key.IndexOf('\n');
                if (split >= 0)
                {
                    strVal = key.Substring(split + 1);
                    key = key.Substring(0, split);
                }
            }
            if (string.IsNullOrEmpty(key)) return false;
            var story = LanNetworkManager.Instance.StorySync;
            // N clients forwarding the same consequence: apply/commit only when it changes the host value.
            var probe = new StoryFlagEntry
            {
                Kind = (byte)msg.Int0,
                Key = key,
                BoolVal = msg.Int1 != 0,
                IntVal = msg.Int1,
                FloatVal = msg.Float0,
                VecY = msg.Float1,
                VecZ = msg.Float2,
                StringVal = strVal
            };
            if (StorySyncService.SameAsLocal(probe)) return true;
            NetGate.BeginApply();
            try
            {
                switch (msg.Int0)
                {
                    case 0:
                        SProgress.SetBool(key, msg.Int1 != 0);
                        story.NoteBool(key, msg.Int1 != 0);
                        break;
                    case 1:
                        SProgress.SetInt(key, msg.Int1);
                        story.NoteInt(key, msg.Int1);
                        break;
                    case 2:
                        SProgress.SetFloat(key, msg.Float0);
                        story.NoteFloat(key, msg.Float0);
                        break;
                    case 3:
                        SProgress.SetString(key, strVal);
                        story.NoteString(key, strVal);
                        break;
                    case 4:
                        var v = new Vector3(msg.Float0, msg.Float1, msg.Float2);
                        SProgress.SetVector(key, v);
                        story.NoteVector(key, v);
                        break;
                }
            }
            finally { NetGate.EndApply(); }
            return true;
        }

        private static T Find<T>(ulong worldId) where T : Component
        {
            var c = WorldLookup.Find<T>(worldId, "Interact");
            if (c == null) return null;
            try
            {
                if (c.gameObject == null) return null;
            }
            catch { return null; }
            return c;
        }
    }
}
