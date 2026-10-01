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

            int prevSender = NetGate.ApplySender;
            if (msg.SenderPlayerId >= 1 && msg.SenderPlayerId != net.LocalPlayerId)
                NetGate.ApplySender = msg.SenderPlayerId;
            try
            {
                switch (msg.Kind)
                {
                    case InteractionKind.EventZone:
                        ok = ApplyEventZone(id, msg.SenderPlayerId);
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
                    // KeypadSubmit / Dialogue* / EventScreen* / Book* / SceneFollowRequest are wire values only: no
                    // build sends them (keypads are live puzzle state, dialogues and inspect screens stay local, scene
                    // requests ride SceneFollow). Anything unhandled is acked FAIL below.
                    case InteractionKind.CutsceneStart:
                        ok = ApplyCutscene(id, net);
                        break;
                    case InteractionKind.CutsceneSkip:
                        ok = ApplyCutsceneSkip(id, net, msg.SenderPlayerId);
                        break;
                    case InteractionKind.StoragePut:
                    case InteractionKind.StorageTake:
                        ok = StorageService.HostApply(msg, out reason);
                        break;
                    case InteractionKind.Gunshot:
                        ApplyGunshot(new Vector3(msg.Float0, msg.Float1, msg.Float2), net);
                        ok = true;
                        break;
                    case InteractionKind.MultiCondition:
                        ok = ApplyMultiCondition(id, msg.Int0);
                        break;
                    case InteractionKind.CutsceneProceed:
                        ok = ApplyCutsceneProceed(id, net, msg.SenderPlayerId);
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
            finally
            {
                NetGate.ApplySender = prevSender;
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

        private static bool ApplyEventZone(ulong id, int senderId)
        {
            var z = Find<EventZone>(id);
            if (z == null) return false;
            if (LocalInspect.LockWorld(z.gameObject)) return true;
            if (z.triggered) return true;
            z.triggered = true;
            SyncRADation.Patches.EventZonePatch.MarkFired(id);
            bool hostRan = LocalInspect.InLocalRoom(z.gameObject);
            if (hostRan)
            {
                try { if (z.onInRange != null) z.onInRange.Invoke(); } catch (System.Exception e) { Guard.Swallow(e); }
            }
            else
                PlaytestLog.Event("Interact", "EventZone other-room id=" + id.ToString("X16"));
            // END attribution: host ran it natively (it counted) or only the requesting client counts.
            LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.EventZoneFire, id, 0,
                hostRan ? StoryWire.HostCounted : StoryWire.PlayerCounted(senderId));
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
            try { PuzzleSyncService.SnapUseItemWorld(u); } catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                var netPuzzle = LanNetworkManager.Instance;
                if (netPuzzle != null)
                    netPuzzle.PuzzleSync.Emit(PuzzleType.UseItemInteraction, id, u);
            }
            catch (System.Exception e) { Guard.Swallow(e); }

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
            catch (System.Exception e) { Guard.Swallow(e); }
            if (parts.Count > 0)
                consumeReason = "consume:" + string.Join("|", parts);
            PartyKeyRing.Broadcast();
            return true;
        }

        private static bool ApplyCutsceneProceed(ulong id, LanNetworkManager net, int senderId)
        {
            var cut = Find<CutsceneCut>(id);
            if (cut == null) return false;
            bool hostRan = LocalInspect.InLocalRoom(cut.gameObject);
            if (hostRan)
            {
                NetGate.BeginApply();
                try { cut.Proceed(); }
                finally { NetGate.EndApply(); }
            }
            else
                PlaytestLog.Event("Interact", "CutsceneProceed other-room id=" + id.ToString("X16"));
            net.StorySync.BroadcastPresentation(StoryCmd.CutsceneProceed, id, 0,
                hostRan ? StoryWire.HostCounted : StoryWire.PlayerCounted(senderId));
            return true;
        }

        private static void ConsumeKey(AnItem key)
        {
            // Ring drop + EnsureInBag mirror strip on all peers via CraftRevokeSentinel fan-out.
            try { PartyKeyRing.RevokeConsumed(key._item); } catch (System.Exception e) { Guard.Swallow(e); }
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
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        /// <returns>true if a ConsumesKey revoke ran</returns>
        static bool TryRevokeUseItemKey(UseItemInteraction u)
        {
            if (u == null) return false;
            AnItem key = null;
            try { key = u.key; } catch (System.Exception e) { Guard.Swallow(e); }
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
            catch (System.Exception e) { Guard.Swallow(e); }
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
                    catch (System.Exception e) { Guard.Swallow(e); }
                    try
                    {
                        if (single.door != null)
                        {
                            var master = single.master;
                            if (master == null || DoorNative.AllowUnlock(master))
                                single.door.locked = false;
                        }
                    }
                    catch (System.Exception e) { Guard.Swallow(e); }
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }

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
                    catch (System.Exception e) { Guard.Swallow(e); }
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
                                catch (System.Exception e) { Guard.Swallow(e); }
                            }
                        }
                    }
                    catch (System.Exception e) { Guard.Swallow(e); }
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return consumes;
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
                catch (System.Exception e) { Guard.Swallow(e); }
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

        private static bool ApplyCutsceneSkip(ulong id, LanNetworkManager net, int senderId)
        {
            var c = Find<CutsceneManager>(id);
            if (c != null && LocalInspect.AirlockCinematic(c.gameObject))
                return true;
            if (!RememberSkip(id))
            {
                PlaytestLog.Event("Interact", "CutsceneSkip already done id=" + id.ToString("X16"));
                return true;
            }
            // Same gate as Start: a host that is not in the room / never started it does not run the skip events.
            bool hostRuns = c != null && SkipApplicable(c, id);
            net.StorySync.BroadcastPresentation(StoryCmd.CutsceneSkip, id, 0,
                hostRuns ? StoryWire.HostCounted : StoryWire.PlayerCounted(senderId));
            if (hostRuns)
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

        /// <summary>Non-mutating: a start for this id was recorded inside the dedupe window (peek, unlike RememberStart).</summary>
        internal static bool StartedRecently(ulong id)
        {
            if (id == 0) return false;
            float last;
            return _startStamp.TryGetValue(id, out last) && Time.unscaledTime - last < StartDedupeSeconds;
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
            try { if (c.completed) return; } catch (System.Exception e) { Guard.Swallow(e); }
            bool running = false;
            try { running = c.cutscene != null; } catch (System.Exception e) { Guard.Swallow(e); }
            if (!running)
            {
                try { c.completed = true; } catch (System.Exception e) { Guard.Swallow(e); }
                try
                {
                    if (c.onCutsceneSkip != null)
                        c.onCutsceneSkip.Invoke();
                }
                catch (System.Exception e) { Guard.Swallow(e); }
                try { CutsceneSkippingUI.skippableCutscene = false; } catch (System.Exception e) { Guard.Swallow(e); }
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
                catch (System.Exception e) { Guard.Swallow(e); }
            }
            finally { NetGate.EndApply(); }
            try { CutsceneSkippingUI.skippableCutscene = false; } catch (System.Exception e) { Guard.Swallow(e); }
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
            catch (System.Exception e) { Guard.Swallow(e); }
            float r2 = range * range;
            foreach (var kvp in WorldRegistry.AllEnemies())
            {
                var e = kvp.Value;
                if (e == null) continue;
                try
                {
                    if (e.state == EnemyController.enemystate.dead) continue;
                    if ((e.transform.position - pos).sqrMagnitude > r2) continue;
                    if (!EnemySyncService.WakeForCombat(e)) continue; // level-disabled enemies stay off
                    Transform t = FindShooterTransform(pos, net);
                    if (t != null) e.playerPos = t;
                    e.WakeUpfromGunShot();
                }
                catch (System.Exception ex) { Guard.Swallow(ex); }
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
            // The host runs TryOnce / TryTrigger natively whatever room it is in, so it always counts END effects.
            var m = Find<MultiConditionEvent>(id);
            if (m == null) return false;
            NetGate.BeginApply();
            try
            {
                if (kind == 1) m.TryTrigger();
                else m.TryOnce();
            }
            finally { NetGate.EndApply(); }
            // TryTrigger's count reaches peers through the PuzzleState poll only (MultiConditionTriggerPatch).
            if (kind != 1)
                LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.MultiConditionFire, id, kind,
                    StoryWire.HostCounted);
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
                    HostDetermineEnding(net, msg.SenderPlayerId);
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

        internal static void HostDetermineEnding(LanNetworkManager net, int requesterId)
        {
            // Finale.determineEnding has a per-object `once`; the broadcast has the same once-per-scene guard,
            // so two players reaching the finale at once start the ending a single time for everyone.
            if (net.StorySync.EndingBroadcasted)
            {
                // The requester missed that broadcast (loading / not yet in the finale scene: presentations are dropped
                // there) and its own determineEnding is blocked: hand it the ending again, to that peer only. A peer that
                // did get it replays a native no-op (Finale.once) and the ending cutscene start is deduped.
                if (requesterId != net.LocalPlayerId && net.HasPeer(requesterId))
                    net.StorySync.ResendEnding(net, requesterId);
                return;
            }
            var f = FirstFinale();
            if (f == null)
            {
                // The host is not in the finale scene (a client reached it alone): there is no Finale to run, but the
                // ending must still be settled from the host's own tally and handed to everyone, or the requester
                // (whose native determineEnding is blocked and whose request is acked OK) waits forever.
                NetGate.BeginApply();
                try { END_Manager.CalculatePlaystyle(); }
                catch (System.Exception ex) { StorySyncService.WarnOnce("Host CalculatePlaystyle", ex); }
                finally { NetGate.EndApply(); }
                net.StorySync.HostBroadcastEnding(net);
                return;
            }
            // Native first: CalculatePlaystyle settles Circle/Death/Ending here; the broadcast then carries them.
            NetGate.BeginApply();
            try { f.determineEnding(); }
            catch (System.Exception ex) { StorySyncService.WarnOnce("Host determineEnding", ex); }
            finally { NetGate.EndApply(); }
            net.StorySync.HostBroadcastEnding(net);
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
            if (StorySyncService.SameAsLocal(probe))
            {
                // Host already holds this value; make sure it is committed (a host-side write made in an apply scope
                // or before the table existed may not have been noted) so out-of-room peers converge.
                SProgressPatches.NoteEntry(story, probe);
                return true;
            }
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

        private static T Find<T>(ulong worldId) where T : Component => FindAlive<T>(worldId, "Interact");

        /// <summary>WorldId lookup that also rejects a destroyed object (Unity's overloaded == null). Shared by both Story services.</summary>
        internal static T FindAlive<T>(ulong worldId, string tag) where T : Component
        {
            var c = WorldLookup.Find<T>(worldId, tag);
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
