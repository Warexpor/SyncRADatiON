// Door state by WorldId: any peer emits, the host judges and relays (DoorNetHandlers).
using System.Collections.Generic;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    public static class DoorSyncService
    {
        // Last state this peer sent or applied per door: only changes go out.
        private static readonly Dictionary<ulong, bool> LastDoubleOpen = new Dictionary<ulong, bool>();
        private static readonly Dictionary<ulong, bool> LastDoubleLocked = new Dictionary<ulong, bool>();
        // ConnectedDoors: lock only. Never sync inProgress/forwards (those are room-traverse).
        private static readonly Dictionary<ulong, bool> LastCdLocked = new Dictionary<ulong, bool>();
        private static readonly Dictionary<ulong, bool> LastSdOpened = new Dictionary<ulong, bool>();
        private static readonly Dictionary<ulong, bool> LastSdMoving = new Dictionary<ulong, bool>();

        // Unlock facts outlive scene reloads (WorldId hashes the scene, so other scenes' entries never resolve).
        // Only unlock transitions are held: a solved key/code/lock door must stay open for late joiners and after
        // a reload. A later Locked=true for the same door drops the entry (falling edge). Cleared when the session ends.
        private static readonly HashSet<ulong> HeldDoubleUnlock = new HashSet<ulong>();
        private static readonly HashSet<ulong> HeldCdUnlock = new HashSet<ulong>();
        private static readonly List<ulong> _heldScratch = new List<ulong>(8);

        // Client open of a still-locked door whose unlocker may simply not have been polled yet: judged again
        // after a short grace instead of being rejected at once.
        private struct PendingOpen
        {
            public DoorStateMessage Msg;
            public float DueAt;
        }
        private static readonly Dictionary<ulong, PendingOpen> _pendingOpens = new Dictionary<ulong, PendingOpen>();
        private static readonly List<ulong> _pendingScratch = new List<ulong>(4);
        private const float OpenGraceSeconds = 1f;

        private static float _scanTimer;
        private const float ScanInterval = 0.3f;
        private static bool _ready;

        // Last open flag each door instance reported through the per-frame hook (local instance id, never sent):
        // only edges go further, so the WorldId lookup runs on edges only.
        private static readonly Dictionary<int, bool> _instOpen = new Dictionary<int, bool>();

        public static void RefreshScene()
        {
            ResetScene();
            // Baseline of the mounted scene: only deltas go out after connect.
            foreach (var kvp in WorldRegistry.AllDoubleDoors())
            {
                if (kvp.Value == null) continue;
                LastDoubleOpen[kvp.Key] = kvp.Value.open;
                LastDoubleLocked[kvp.Key] = kvp.Value.locked;
            }
            foreach (var kvp in WorldRegistry.AllConnectedDoors())
            {
                if (kvp.Value == null) continue;
                LastCdLocked[kvp.Key] = kvp.Value.locked;
            }
            foreach (var kvp in WorldRegistry.AllSlidingDoors())
            {
                if (kvp.Value == null) continue;
                LastSdOpened[kvp.Key] = kvp.Value.opened;
                LastSdMoving[kvp.Key] = kvp.Value.moving;
            }
            _ready = true;
            // A reloaded scene starts with default lock flags: re-snap what was already solved.
            if (HeldDoubleUnlock.Count > 0 || HeldCdUnlock.Count > 0)
            {
                var netq = LanNetworkManager.Instance;
                if (netq != null && netq.IsConnected)
                    netq.PuzzleSync.QueueReapply();
            }
            ModRuntime.Log?.Msg("[DoorSync] Ready with WorldIds: double=" + LastDoubleOpen.Count
                + " connected=" + LastCdLocked.Count
                + " sliding=" + LastSdOpened.Count);
        }

        /// <summary>Session end (StopNetwork): nothing solved in one session may carry into the next.</summary>
        public static void Reset()
        {
            HeldDoubleUnlock.Clear();
            HeldCdUnlock.Clear();
            ResetScene();
        }

        private static void ResetScene()
        {
            _pendingOpens.Clear();
            LastDoubleOpen.Clear();
            LastDoubleLocked.Clear();
            LastCdLocked.Clear();
            LastSdOpened.Clear();
            LastSdMoving.Clear();
            _instOpen.Clear();
            SyncRADation.Patches.DoubleDoorOpenPatch.ResetScene();
            _scanTimer = 0f;
            _ready = false;
        }

        public static void Tick()
        {
            if (!_ready)
            {
                if (WorldRegistry.DoorCount > 0)
                    RefreshScene();
                return;
            }

            _scanTimer += Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            if (_scanTimer < ScanInterval) return;
            _scanTimer = 0f;

            // Zero-peer host (or a client without a ready host): nothing to tell anyone, no poll work.
            if (!NetGate.Party) return;
            var net = LanNetworkManager.Instance;
            if (net == null) return;
            Poll(net);
        }

        /// <summary>
        /// Send pending lock edges to everyone now (before a unicast join dump, whose full send must not be the only
        /// place a change goes out). Open/slide edges are sent from the hooks the moment they happen.
        /// </summary>
        public static void FlushDiffNow()
        {
            if (!_ready || !NetGate.Party) return;
            var net = LanNetworkManager.Instance;
            if (net == null || net.UnicastActive) return;
            Poll(net);
        }

        /// <summary>Lock-edge poll: ConnectedDoors lock flips, script relocks of double doors, deferred client opens.</summary>
        private static void Poll(LanNetworkManager net)
        {
            foreach (var kvp in WorldRegistry.AllConnectedDoors())
            {
                var cd = kvp.Value;
                if (cd == null) continue;
                ulong id = kvp.Key;
                bool lk = cd.locked;
                bool llk;
                LastCdLocked.TryGetValue(id, out llk);
                if (lk != llk)
                {
                    NoteLockEdge(HeldCdUnlock, id, lk);
                    SendDoorChange(DoorType.ConnectedDoors, id, false, lk, false, false, false);
                    LastCdLocked[id] = lk;
                }
            }

            // A script can flip Doorway_Double.locked without going through openDoors/closeDoors (boss arena seal):
            // keep the held unlock honest, and let the host tell everyone about a relock.
            bool host = NetGate.HostRole;
            foreach (var kvp in WorldRegistry.AllDoubleDoors())
            {
                var d = kvp.Value;
                if (d == null) continue;
                ulong id = kvp.Key;
                bool lk;
                try { lk = d.locked; }
                catch (System.Exception ex) { Guard.Swallow("Door.PollLocked", ex); continue; }
                bool prev;
                if (!LastDoubleLocked.TryGetValue(id, out prev)) { LastDoubleLocked[id] = lk; continue; }
                if (prev == lk) continue;
                LastDoubleLocked[id] = lk;
                NoteLockEdge(HeldDoubleUnlock, id, lk);
                if (host && lk)
                {
                    PlaytestLog.Event("Door", "script relock " + d.gameObject.name);
                    SendDoorChange(DoorType.DoorwayDouble, id, d.open, true, false, false, false);
                }
            }

            if (host && _pendingOpens.Count > 0)
                ResolvePendingOpens(net);
        }

        private static void SendDoorChange(DoorType type, ulong worldId, bool open, bool locked,
            bool inProgress, bool forwards, bool moving)
        {
            var net = LanNetworkManager.Instance;
            if (net == null) return;
            var msg = new DoorStateMessage
            {
                SenderPlayerId = net.LocalPlayerId,
                Type = type,
                WorldId = unchecked((long)worldId),
                Open = open,
                Locked = locked,
                InProgress = inProgress,
                Forwards = forwards,
                Moving = moving
            };
            net.DoorHandlers.SendDoorState(msg);
        }

        public static void NotifyDoubleDoor(Doorway_Double d, bool open)
        {
            if (d == null || NetGate.IsApplying) return;
            if (!NetGate.Live) return;
            int inst;
            try { inst = d.GetInstanceID(); } catch { return; }
            // Per-frame hook: same flag as last frame = nothing happened. Lock edges ride the 0.3 s poll.
            bool was;
            if (_instOpen.TryGetValue(inst, out was) && was == open) return;
            _instOpen[inst] = open;
            ulong id = WorldId.FromGameObject(d.gameObject);
            if (id == 0) return;
            bool lo;
            LastDoubleOpen.TryGetValue(id, out lo);
            bool lockedNow = d.locked;
            bool prevLocked;
            if (LastDoubleLocked.TryGetValue(id, out prevLocked) && prevLocked != lockedNow)
                NoteLockEdge(HeldDoubleUnlock, id, lockedNow);
            if (lo == open && LastDoubleLocked.ContainsKey(id))
            {
                LastDoubleLocked[id] = lockedNow;
                return;
            }
            LastDoubleOpen[id] = open;
            LastDoubleLocked[id] = lockedNow;
            PlaytestLog.Event("Door", (open ? "open" : "close") + " id=" + id.ToString("X16"));
            SendDoorChange(DoorType.DoorwayDouble, id, open, lockedNow, false, false, false);
        }

        public static void NotifySlidingDoor(EventSlidingDoor sd)
        {
            if (sd == null || NetGate.IsApplying) return;
            if (!NetGate.Live) return;
            ulong id = WorldId.FromGameObject(sd.gameObject);
            if (id == 0) return;
            bool opened = sd.opened, moving = sd.moving;
            bool lo, lm;
            LastSdOpened.TryGetValue(id, out lo);
            LastSdMoving.TryGetValue(id, out lm);
            if (lo == opened && lm == moving) return;
            LastSdOpened[id] = opened;
            LastSdMoving[id] = moving;
            PlaytestLog.Event("Door", (opened ? "slide-open" : "slide-close") + " id=" + id.ToString("X16"));
            SendDoorChange(DoorType.EventSlidingDoor, id, opened, false, false, false, moving);
        }

        /// <summary>
        /// Host: push every door state (join resync / scene load). Inside a unicast dump only the joiner hears it, so
        /// the Last* records stay as they were: a change still pending for the others goes out with the next poll.
        /// </summary>
        public static void ForceFullSend()
        {
            if (!_ready)
            {
                if (WorldRegistry.DoorCount > 0)
                    RefreshScene();
                else
                    return;
            }

            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            bool record = !net.UnicastActive;

            foreach (var kvp in WorldRegistry.AllDoubleDoors())
            {
                var d = kvp.Value;
                if (d == null) continue;
                bool open = d.open, locked = d.locked;
                SendDoorChange(DoorType.DoorwayDouble, kvp.Key, open, locked, false, false, false);
                if (!record) continue;
                LastDoubleOpen[kvp.Key] = open;
                LastDoubleLocked[kvp.Key] = locked;
            }
            foreach (var kvp in WorldRegistry.AllConnectedDoors())
            {
                var cd = kvp.Value;
                if (cd == null) continue;
                bool locked = cd.locked;
                SendDoorChange(DoorType.ConnectedDoors, kvp.Key, false, locked, false, false, false);
                if (record) LastCdLocked[kvp.Key] = locked;
            }
            foreach (var kvp in WorldRegistry.AllSlidingDoors())
            {
                var sd = kvp.Value;
                if (sd == null) continue;
                bool opened = sd.opened, moving = sd.moving;
                SendDoorChange(DoorType.EventSlidingDoor, kvp.Key, opened, false, false, false, moving);
                if (!record) continue;
                LastSdOpened[kvp.Key] = opened;
                LastSdMoving[kvp.Key] = moving;
            }
            ModRuntime.Log?.Msg("[DoorSync] Full dump sent" + (record ? "" : " (unicast)"));
        }

        /// <summary>Record (unlock) or drop (relock) the held solve for a door. Only edges are held.</summary>
        private static void NoteLockEdge(HashSet<ulong> held, ulong id, bool locked)
        {
            if (locked) held.Remove(id);
            else held.Add(id);
        }

        /// <summary>
        /// A door message that cannot be applied now (loading / other scene): keep its lock fact so the
        /// door is unlocked when that scene is mounted. Unlock = held, relock = dropped.
        /// </summary>
        public static void HoldMessage(DoorStateMessage msg)
        {
            ulong id = unchecked((ulong)msg.WorldId);
            // Host: only a client message reaches here, and a client can never drop a held unlock.
            if (NetGate.HostRole && msg.Locked) return;
            switch (msg.Type)
            {
                case DoorType.DoorwayDouble: NoteLockEdge(HeldDoubleUnlock, id, msg.Locked); break;
                case DoorType.ConnectedDoors: NoteLockEdge(HeldCdUnlock, id, msg.Locked); break;
            }
        }

        /// <summary>
        /// Re-apply held unlocks to the mounted scene (QueueReapply / room enter / scene load).
        /// Flag snap only: never opens, never traverses.
        /// </summary>
        public static void ReapplyHeldDoors()
        {
            if (HeldDoubleUnlock.Count == 0 && HeldCdUnlock.Count == 0) return;
            // Native Unlock below can run door hooks that edit the held sets: walk a copy.
            _heldScratch.Clear();
            foreach (ulong id in HeldDoubleUnlock) _heldScratch.Add(id);
            for (int i = 0; i < _heldScratch.Count; i++)
            {
                ulong id = _heldScratch[i];
                Doorway_Double d;
                if (!WorldRegistry.TryGetDoubleDoor(id, out d) || d == null) continue;
                try
                {
                    if (!d.locked) continue; // polled live: a door the script relocked and the poll dropped is not held
                    if (DoorNative.IsFlavorSeal(d.gameObject) || DoorNative.HasUnsolvedLock(d)) continue;
                    d.locked = false;
                    LastDoubleLocked[id] = false;
                    PlaytestLog.Event("Door", "reapply unlock " + d.gameObject.name);
                }
                catch (System.Exception ex) { Guard.Swallow("Door.ReapplyDouble", ex); }
            }
            _heldScratch.Clear();
            foreach (ulong id in HeldCdUnlock) _heldScratch.Add(id);
            for (int i = 0; i < _heldScratch.Count; i++)
            {
                ulong id = _heldScratch[i];
                ConnectedDoors cd;
                if (!WorldRegistry.TryGetConnectedDoor(id, out cd) || cd == null) continue;
                try
                {
                    if (!cd.locked) continue;
                    DoorNative.ApplyConnectedDoors(cd, false);
                    LastCdLocked[id] = cd.locked;
                }
                catch (System.Exception ex) { Guard.Swallow("Door.ReapplyConnected", ex); }
            }
            _heldScratch.Clear();
        }

        /// <summary>
        /// Apply a received door state. Returns true when the (possibly corrected) message is to be relayed (host);
        /// false when the host deferred it. On a host rejection msg holds the corrective state (broadcast to
        /// everyone, incl. the sender).
        /// </summary>
        public static bool HandleMessage(ref DoorStateMessage msg)
        {
            ulong id = unchecked((ulong)msg.WorldId);
            switch (msg.Type)
            {
                case DoorType.DoorwayDouble: return ApplyDoorwayDouble(id, ref msg);
                case DoorType.ConnectedDoors: return ApplyConnectedDoors(id, ref msg);
                case DoorType.EventSlidingDoor: ApplySlidingDoor(id, msg); break;
            }
            return true;
        }

        private static bool ApplyDoorwayDouble(ulong id, ref DoorStateMessage msg)
        {
            Doorway_Double d;
            if (!WorldRegistry.TryGetDoubleDoor(id, out d) || d == null)
            {
                HoldMessage(msg);
                return true;
            }

            var net = LanNetworkManager.Instance;
            bool isHost = NetGate.HostRole;
            bool hostLocked = false;
            try { hostLocked = d.locked; } catch (System.Exception ex) { Guard.Swallow("Door.ReadLocked", ex); }
            if (isHost && !msg.Open)
                _pendingOpens.Remove(id); // a later close/relock supersedes a deferred open
            if (isHost && hostLocked)
            {
                if (msg.Open)
                {
                    // A client opening a door is not proof it was solved: its flag can be stale (scene load,
                    // dropped message). Unlock only when a real lock on this door (key / code / DoorLockControl)
                    // is solved on the host. Otherwise the unlocker may just not have been polled yet (puzzle
                    // state rides a 0.5 s poll): judge again after a short grace before sealing the door.
                    if (DoorNative.IsFlavorSeal(d.gameObject))
                        return RejectOpen(net, d, ref msg);
                    if (!HostLockSolved(d))
                    {
                        _pendingOpens[id] = new PendingOpen { Msg = msg, DueAt = Time.unscaledTime + OpenGraceSeconds };
                        PlaytestLog.Event("Door", "defer client open (lock not solved yet) " + d.gameObject.name);
                        return false; // neither relayed nor rejected yet
                    }
                    try { d.locked = false; } catch (System.Exception ex) { Guard.Swallow("Door.Unlock", ex); }
                    msg.Locked = false;
                    _pendingOpens.Remove(id);
                    PlaytestLog.Event("Door", "honor client open (lock solved) " + d.gameObject.name);
                }
                else
                    msg.Locked = true;
            }
            else if (isHost)
            {
                _pendingOpens.Remove(id);
                if (msg.Locked)
                {
                    // A client can never lock a door the host holds unlocked (a script relock on its side, a stale
                    // flag): the host's flag wins and the corrected state goes back to everyone incl. the sender.
                    PlaytestLog.Event("Door", "ignore client relock (host door unlocked) " + d.gameObject.name);
                    msg.Locked = false;
                    msg.SenderPlayerId = net.LocalPlayerId;
                }
            }

            DoorNative.ApplyDoubleDoor(d, msg.Open, msg.Locked);
            LastDoubleOpen[id] = msg.Open;
            // ApplyDoubleDoor keeps flavor seals sealed: record the door's real flag (or the lock poll reads a phantom
            // edge for a flavor seal), and hold the solve from it. Unlock edge holds, relock drops.
            bool nowLocked = msg.Locked;
            try { nowLocked = d.locked; } catch (System.Exception ex) { Guard.Swallow("Door.ReadLocked", ex); }
            LastDoubleLocked[id] = nowLocked;
            if (!nowLocked) NoteLockEdge(HeldDoubleUnlock, id, false);
            else if (msg.Locked) NoteLockEdge(HeldDoubleUnlock, id, true);
            return true;
        }

        static bool HostLockSolved(Doorway_Double d)
            => DoorNative.LockSolvedFor(d) || DoorNative.DoorLockUnlocked(d);

        /// <summary>Host: keep the door sealed and push the sealed state to everyone (the sender re-syncs too).</summary>
        static bool RejectOpen(LanNetworkManager net, Doorway_Double d, ref DoorStateMessage msg)
        {
            PlaytestLog.Event("Door", "reject client open (lock not solved) " + d.gameObject.name);
            msg.Open = d.open;
            msg.Locked = true;
            msg.SenderPlayerId = net.LocalPlayerId;
            DoorNative.ApplyDoubleDoor(d, d.open, true);
            return true;
        }

        /// <summary>Host: settle deferred client opens (unlocker solved meanwhile = honor, grace over = reject).</summary>
        static void ResolvePendingOpens(LanNetworkManager net)
        {
            _pendingScratch.Clear();
            foreach (var kvp in _pendingOpens) _pendingScratch.Add(kvp.Key);
            float now = Time.unscaledTime;
            for (int i = 0; i < _pendingScratch.Count; i++)
            {
                ulong id = _pendingScratch[i];
                PendingOpen p;
                if (!_pendingOpens.TryGetValue(id, out p)) continue;
                Doorway_Double d;
                if (!WorldRegistry.TryGetDoubleDoor(id, out d) || d == null)
                {
                    _pendingOpens.Remove(id);
                    continue;
                }
                bool locked = true;
                try { locked = d.locked; } catch (System.Exception ex) { Guard.Swallow("Door.PendingRead", ex); }
                bool solved = !locked || HostLockSolved(d);
                if (!solved && now < p.DueAt) continue;
                _pendingOpens.Remove(id);
                var msg = p.Msg;
                if (solved)
                {
                    try { d.locked = false; } catch (System.Exception ex) { Guard.Swallow("Door.Unlock", ex); }
                    msg.Locked = false;
                    msg.SenderPlayerId = net.LocalPlayerId;
                    PlaytestLog.Event("Door", "honor deferred client open " + d.gameObject.name);
                    DoorNative.ApplyDoubleDoor(d, msg.Open, false);
                    LastDoubleOpen[id] = msg.Open;
                    LastDoubleLocked[id] = false;
                    NoteLockEdge(HeldDoubleUnlock, id, false);
                }
                else
                    RejectOpen(net, d, ref msg);
                net.DoorHandlers.SendDoorState(msg);
            }
            _pendingScratch.Clear();
        }

        private static bool ApplyConnectedDoors(ulong id, ref DoorStateMessage msg)
        {
            ConnectedDoors cd;
            if (!WorldRegistry.TryGetConnectedDoor(id, out cd) || cd == null)
            {
                HoldMessage(msg);
                return true;
            }

            var net = LanNetworkManager.Instance;
            // The host echoes a client's own change back to it (DoorNetHandlers): already applied natively here,
            // and re-running Unlock + ReleaseTraverse mid-traverse would re-arm the door's interactions.
            if (NetGate.ClientRole && msg.SenderPlayerId == net.LocalPlayerId)
            {
                bool cur = msg.Locked;
                try { cur = cd.locked; } catch (System.Exception ex) { Guard.Swallow("Door.CdReadEcho", ex); }
                if (cur == msg.Locked)
                {
                    LastCdLocked[id] = cur;
                    return true;
                }
            }
            if (NetGate.HostRole && msg.Locked)
            {
                bool hostLocked = true;
                try { hostLocked = cd.locked; } catch (System.Exception ex) { Guard.Swallow("Door.CdReadHost", ex); }
                if (!hostLocked)
                {
                    // Same rule as Doorway_Double: a client never locks a connected door the host holds unlocked.
                    PlaytestLog.Event("Door", "ignore client relock (host connected door unlocked) " + cd.gameObject.name);
                    msg.Locked = false;
                    msg.SenderPlayerId = net.LocalPlayerId;
                    return true;
                }
            }

            // InProgress/Forwards are never applied: room entry stays local.
            DoorNative.ApplyConnectedDoors(cd, msg.Locked);
            // ApplyConnectedDoors refuses to unlock a door without an unlocker: record the real flag, or the
            // next poll reads a phantom unlock edge and sends Locked=true for a door the host unlocked.
            bool realLocked = msg.Locked;
            try { realLocked = cd.locked; } catch (System.Exception ex) { Guard.Swallow("Door.CdRead", ex); }
            NoteLockEdge(HeldCdUnlock, id, realLocked);
            LastCdLocked[id] = realLocked;
            return true;
        }

        private static void ApplySlidingDoor(ulong id, DoorStateMessage msg)
        {
            EventSlidingDoor sd;
            if (!WorldRegistry.TryGetSlidingDoor(id, out sd) || sd == null) return;

            DoorNative.ApplySlidingDoor(sd, msg.Open, msg.Moving);
            LastSdOpened[id] = msg.Open;
            LastSdMoving[id] = msg.Moving;
        }
    }
}
