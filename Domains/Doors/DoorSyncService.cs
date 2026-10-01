// Door state tracking via WorldId; host/any peer can emit; host relays.
using System.Collections.Generic;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    public static class DoorSyncService
    {
        private static readonly Dictionary<ulong, bool> LastDoubleOpen = new Dictionary<ulong, bool>();
        private static readonly Dictionary<ulong, bool> LastDoubleLocked = new Dictionary<ulong, bool>();
        // ConnectedDoors: lock only. Never sync inProgress/forwards (those are room-traverse).
        private static readonly Dictionary<ulong, bool> LastCdLocked = new Dictionary<ulong, bool>();
        private static readonly Dictionary<ulong, bool> LastSdOpened = new Dictionary<ulong, bool>();
        private static readonly Dictionary<ulong, bool> LastSdMoving = new Dictionary<ulong, bool>();

        // Unlock facts outlive scene reloads (WorldId hashes the scene, so other scenes' entries never resolve).
        // Only unlock transitions are held: a solved key/code/lock door must stay open for late joiners and after
        // a reload. A later Locked=true for the same door drops the entry (falling edge). Cleared when the session ends.
        private static readonly Dictionary<ulong, bool> HeldDoubleUnlock = new Dictionary<ulong, bool>();
        private static readonly Dictionary<ulong, bool> HeldCdUnlock = new Dictionary<ulong, bool>();

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

        // Local Unity instance id -> registry WorldId (never sent), rebuilt when WorldRegistry.Generation moves.
        // The open/close hooks fire every frame per active door: no hierarchy hashing there.
        private static readonly Dictionary<int, ulong> _doubleIdByInst = new Dictionary<int, ulong>();
        private static readonly Dictionary<int, ulong> _slidingIdByInst = new Dictionary<int, ulong>();
        private static int _idGeneration = -1;
        // Last open flag each door instance reported through the per-frame hook (only edges go further).
        private static readonly Dictionary<int, bool> _instOpen = new Dictionary<int, bool>();

        static void EnsureIdMaps()
        {
            int g = WorldRegistry.Generation;
            if (g == _idGeneration) return;
            _idGeneration = g;
            _doubleIdByInst.Clear();
            _slidingIdByInst.Clear();
            _instOpen.Clear();
            foreach (var kvp in WorldRegistry.AllDoubleDoors())
            {
                try { if (kvp.Value != null) _doubleIdByInst[kvp.Value.GetInstanceID()] = kvp.Key; }
                catch (System.Exception e) { Guard.Swallow(e); }
            }
            foreach (var kvp in WorldRegistry.AllSlidingDoors())
            {
                try { if (kvp.Value != null) _slidingIdByInst[kvp.Value.GetInstanceID()] = kvp.Key; }
                catch (System.Exception e) { Guard.Swallow(e); }
            }
        }

        static ulong DoubleDoorId(Doorway_Double d, int inst)
        {
            EnsureIdMaps();
            ulong id;
            if (_doubleIdByInst.TryGetValue(inst, out id)) return id;
            // Not registered (instantiated after the scan): hash once and remember.
            id = WorldId.FromGameObject(d.gameObject);
            _doubleIdByInst[inst] = id;
            return id;
        }

        static ulong SlidingDoorId(EventSlidingDoor sd)
        {
            EnsureIdMaps();
            int inst = sd.GetInstanceID();
            ulong id;
            if (_slidingIdByInst.TryGetValue(inst, out id)) return id;
            id = WorldId.FromGameObject(sd.gameObject);
            _slidingIdByInst[inst] = id;
            return id;
        }

        public static void RefreshScene()
        {
            ResetScene();
            // Snapshot current states so we only send deltas after connect
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
            _doubleIdByInst.Clear();
            _slidingIdByInst.Clear();
            _instOpen.Clear();
            _idGeneration = -1;
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
            bool host = net.Role == NetworkRole.Host;
            foreach (var kvp in WorldRegistry.AllDoubleDoors())
            {
                var d = kvp.Value;
                if (d == null) continue;
                ulong id = kvp.Key;
                bool lk;
                try { lk = d.locked; }
                catch (System.Exception ex) { PuzzleSyncService.WarnOnce("door-poll-locked", ex.Message); continue; }
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
            net.SendDoorState(msg);
        }

        public static void NotifyDoubleDoor(Doorway_Double d, bool open)
        {
            if (d == null || NetGate.IsApplying) return;
            if (!NetGate.Live) return;
            int inst;
            try { inst = d.GetInstanceID(); } catch { return; }
            EnsureIdMaps();
            // Per-frame hook: same flag as last frame = nothing happened. Lock edges ride the 0.3 s Tick poll.
            bool was;
            if (_instOpen.TryGetValue(inst, out was) && was == open) return;
            _instOpen[inst] = open;
            ulong id = DoubleDoorId(d, inst);
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
            LastDoubleLocked[id] = d.locked;
            PlaytestLog.Event("Door", (open ? "open" : "close") + " id=" + id.ToString("X16"));
            SendDoorChange(DoorType.DoorwayDouble, id, open, d.locked, false, false, false);
        }

        public static void NotifySlidingDoor(EventSlidingDoor sd)
        {
            if (sd == null || NetGate.IsApplying) return;
            if (!NetGate.Live) return;
            ulong id = SlidingDoorId(sd);
            if (id == 0) return;
            bool lo, lm;
            LastSdOpened.TryGetValue(id, out lo);
            LastSdMoving.TryGetValue(id, out lm);
            if (lo == sd.opened && lm == sd.moving) return;
            LastSdOpened[id] = sd.opened;
            LastSdMoving[id] = sd.moving;
            PlaytestLog.Event("Door", (sd.opened ? "slide-open" : "slide-close") + " id=" + id.ToString("X16"));
            SendDoorChange(DoorType.EventSlidingDoor, id, sd.opened, false, false, false, sd.moving);
        }

        /// <summary>Host: push every door state (join resync / scene load).</summary>
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

            foreach (var kvp in WorldRegistry.AllDoubleDoors())
            {
                if (kvp.Value == null) continue;
                SendDoorChange(DoorType.DoorwayDouble, kvp.Key, kvp.Value.open, kvp.Value.locked, false, false, false);
                LastDoubleOpen[kvp.Key] = kvp.Value.open;
                LastDoubleLocked[kvp.Key] = kvp.Value.locked;
            }
            foreach (var kvp in WorldRegistry.AllConnectedDoors())
            {
                if (kvp.Value == null) continue;
                SendDoorChange(DoorType.ConnectedDoors, kvp.Key, false, kvp.Value.locked, false, false, false);
                LastCdLocked[kvp.Key] = kvp.Value.locked;
            }
            foreach (var kvp in WorldRegistry.AllSlidingDoors())
            {
                if (kvp.Value == null) continue;
                SendDoorChange(DoorType.EventSlidingDoor, kvp.Key, kvp.Value.opened, false, false, false, kvp.Value.moving);
                LastSdOpened[kvp.Key] = kvp.Value.opened;
                LastSdMoving[kvp.Key] = kvp.Value.moving;
            }
            ModRuntime.Log?.Msg("[DoorSync] Full dump sent");
        }

        /// <summary>Record (unlock) or drop (relock) the held solve for a door. Only edges are held.</summary>
        private static void NoteLockEdge(Dictionary<ulong, bool> held, ulong id, bool locked)
        {
            if (locked)
                held.Remove(id);
            else held[id] = true;
        }

        /// <summary>
        /// A door message that cannot be applied now (loading / other scene): keep its lock fact so the
        /// door is unlocked when that scene is mounted. Unlock = held, relock = dropped.
        /// </summary>
        public static void HoldMessage(DoorStateMessage msg)
        {
            ulong id = unchecked((ulong)msg.WorldId);
            // Host: only a client message reaches here, and a client can never drop a held unlock.
            var net = LanNetworkManager.Instance;
            if (net != null && net.Role == NetworkRole.Host && msg.Locked) return;
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
            foreach (var id in new List<ulong>(HeldDoubleUnlock.Keys))
            {
                Doorway_Double d;
                if (!WorldRegistry.TryGetDoubleDoor(id, out d) || d == null) continue;
                try
                {
                    if (!d.locked) continue; // polled live: a door the script relocked and the poll dropped is not in the map
                    if (DoorNative.IsFlavorSeal(d.gameObject) || DoorNative.HasUnsolvedLock(d)) continue;
                    d.locked = false;
                    LastDoubleLocked[id] = false;
                    PlaytestLog.Event("Door", "reapply unlock " + d.gameObject.name);
                }
                catch (System.Exception ex) { PuzzleSyncService.WarnOnce("door-reapply-double", ex.Message); }
            }
            foreach (var id in new List<ulong>(HeldCdUnlock.Keys))
            {
                ConnectedDoors cd;
                if (!WorldRegistry.TryGetConnectedDoor(id, out cd) || cd == null) continue;
                try
                {
                    if (!cd.locked) continue;
                    DoorNative.ApplyConnectedDoors(cd, false);
                    LastCdLocked[id] = cd.locked;
                }
                catch (System.Exception ex) { PuzzleSyncService.WarnOnce("door-reapply-cd", ex.Message); }
            }
        }

        /// <summary>
        /// Apply a received door state. Returns the message to relay (host), or false when the host rejected it.
        /// On a host rejection msg holds the corrective state (broadcast to everyone, incl. the sender).
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
            bool hostLocked = false;
            try { hostLocked = d.locked; } catch (System.Exception ex) { PuzzleSyncService.WarnOnce("door-read-locked", ex.Message); }
            if (net != null && net.Role == NetworkRole.Host && !msg.Open)
                _pendingOpens.Remove(id); // a later close/relock supersedes a deferred open
            if (net != null && net.Role == NetworkRole.Host && hostLocked)
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
                    try { d.locked = false; } catch (System.Exception ex) { PuzzleSyncService.WarnOnce("door-unlock", ex.Message); }
                    msg.Locked = false;
                    _pendingOpens.Remove(id);
                    PlaytestLog.Event("Door", "honor client open (lock solved) " + d.gameObject.name);
                }
                else
                    msg.Locked = true;
            }
            else if (net != null && net.Role == NetworkRole.Host)
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
            LastDoubleLocked[id] = msg.Locked;
            // Held solve: unlock edge holds, relock drops. ApplyDoubleDoor keeps flavor seals sealed, so
            // record the door's real flag instead of the message.
            bool nowLocked = msg.Locked;
            try { nowLocked = d.locked; } catch (System.Exception ex) { PuzzleSyncService.WarnOnce("door-read-locked2", ex.Message); }
            LastDoubleLocked[id] = nowLocked; // the real flag, or the lock poll reads a phantom edge for a flavor seal
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
                try { locked = d.locked; } catch (System.Exception ex) { PuzzleSyncService.WarnOnce("door-pending-read", ex.Message); }
                bool solved = !locked || HostLockSolved(d);
                if (!solved && now < p.DueAt) continue;
                _pendingOpens.Remove(id);
                var msg = p.Msg;
                if (solved)
                {
                    try { d.locked = false; } catch (System.Exception ex) { PuzzleSyncService.WarnOnce("door-unlock", ex.Message); }
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
                net.SendDoorState(msg);
            }
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
            if (net != null && net.Role == NetworkRole.Client && msg.SenderPlayerId == net.LocalPlayerId)
            {
                bool cur = msg.Locked;
                try { cur = cd.locked; } catch (System.Exception ex) { PuzzleSyncService.WarnOnce("door-cd-read-echo", ex.Message); }
                if (cur == msg.Locked)
                {
                    LastCdLocked[id] = cur;
                    return true;
                }
            }
            if (net != null && net.Role == NetworkRole.Host && msg.Locked)
            {
                bool hostLocked = true;
                try { hostLocked = cd.locked; } catch (System.Exception ex) { PuzzleSyncService.WarnOnce("door-cd-read-host", ex.Message); }
                if (!hostLocked)
                {
                    // Same rule as Doorway_Double: a client never locks a connected door the host holds unlocked.
                    PlaytestLog.Event("Door", "ignore client relock (host connected door unlocked) " + cd.gameObject.name);
                    msg.Locked = false;
                    msg.SenderPlayerId = net.LocalPlayerId;
                    return true;
                }
            }

            // Ignore InProgress/Forwards — those must stay local (room entry).
            DoorNative.ApplyConnectedDoors(cd, msg.Locked);
            // ApplyConnectedDoors refuses to unlock a door without an unlocker: record the real flag, or the
            // next poll reads a phantom unlock edge and sends Locked=true for a door the host unlocked.
            bool realLocked = msg.Locked;
            try { realLocked = cd.locked; } catch (System.Exception ex) { PuzzleSyncService.WarnOnce("door-cd-read", ex.Message); }
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
