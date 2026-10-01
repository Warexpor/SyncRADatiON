// Host side of client interaction requests: validate, run the native world mutation, ack the sender.
// UseItem / UseItemMulti: InteractionSyncService.UseItem.cs. Cutscene start / skip / proceed: CutsceneSync.
using SyncRADation.Patches;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    public static partial class InteractionSyncService
    {
        public static void HandleRequest(InteractionRequestMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || !NetGate.HostRole) return;

            ulong id = unchecked((ulong)msg.WorldId);
            bool ok = false;
            string reason = "";

            // Peer-gone mid-flight: DroppedPickup / StorageTake must not mutate shared
            // stock when the claimer can no longer receive grant/ack (floor/box item loss).
            // StoragePut still applies — boxing preserves the item for the party.
            // Mirrors the WorldPickupNetHandlers HasPeer guard.
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
                        ok = CutsceneSync.HostApplyStart(id, net);
                        break;
                    case InteractionKind.CutsceneSkip:
                        ok = CutsceneSync.HostApplySkip(id, net, msg.SenderPlayerId);
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
                        ok = CutsceneSync.HostApplyProceed(id, net, msg.SenderPlayerId);
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
            EventZonePatch.MarkFired(id);
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

        private static void ApplyGunshot(Vector3 pos, LanNetworkManager net)
        {
            float r2 = HearRange();
            r2 *= r2;
            Transform shooter = null;
            bool shooterKnown = false;
            foreach (var kvp in WorldRegistry.AllEnemies())
            {
                var e = kvp.Value;
                if (e == null) continue;
                try
                {
                    if (e.state == EnemyController.enemystate.dead) continue;
                    if ((e.transform.position - pos).sqrMagnitude > r2) continue;
                    if (!EnemySyncService.WakeForCombat(e)) continue; // level-disabled enemies stay off
                    if (!shooterKnown)
                    {
                        shooter = FindShooterTransform(pos, net);
                        shooterKnown = true;
                    }
                    if (shooter != null) e.playerPos = shooter;
                    e.WakeUpfromGunShot();
                }
                catch (System.Exception ex) { Guard.Swallow(ex); }
            }
        }

        /// <summary>ElsterSettings.hearRange of the scene (25 without one).</summary>
        static float HearRange()
        {
            try
            {
                var settings = WorldLookup.All<ElsterSettings>();
                if (settings != null)
                {
                    for (int i = 0; i < settings.Length; i++)
                    {
                        if (settings[i] != null) return settings[i].hearRange;
                    }
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return 25f;
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
            var story = LanNetworkManager.Instance.StorySync;
            switch ((StoryCmd)(msg.Int0 - 100))
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
                    story.HostDetermineEnding(msg.SenderPlayerId);
                    return true;
                case StoryCmd.GoToPenny:
                    story.HostGoToPenny();
                    return true;
            }
            return false;
        }

        /// <summary>A client's SProgress write (Int0 = kind, see StorySyncService.SendFlag): the host applies and commits it.</summary>
        private static bool ApplyInspectFlag(InteractionRequestMessage msg)
        {
            if (msg.Int0 >= 100) return ApplyStoryRequest(msg);
            if (msg.Int0 < 0 || msg.Int0 > 4) return false;
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
            var e = new StoryFlagEntry
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
            // N clients forwarding the same consequence: write only when it changes the host value. The note always
            // runs: a host-side write made before the table existed may not be committed yet, and out-of-room peers
            // must converge.
            if (!StorySyncService.SameAsLocal(e))
            {
                NetGate.BeginApply();
                try { ProgressSlot.SetNative(e); }
                finally { NetGate.EndApply(); }
            }
            SProgressPatches.NoteEntry(LanNetworkManager.Instance.StorySync, e);
            return true;
        }

        /// <summary>Scene change / session reset of the request dedupe (cutscene start / skip stamps are per-scene WorldIds).</summary>
        public static void OnSceneChanged() => CutsceneSync.OnSceneChanged();

        private static T Find<T>(ulong worldId) where T : Component => FindAlive<T>(worldId, "Interact");

        /// <summary>WorldId lookup that also rejects a destroyed object (Unity's overloaded == null). Shared by the Story services.</summary>
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
