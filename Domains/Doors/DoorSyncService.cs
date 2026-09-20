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

        private static float _scanTimer;
        private const float ScanInterval = 0.3f;
        private static bool _ready;

        public static void RefreshScene()
        {
            Reset();
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
            ModRuntime.Log?.Msg("[DoorSync] Ready with WorldIds: double=" + LastDoubleOpen.Count
                + " connected=" + LastCdLocked.Count
                + " sliding=" + LastSdOpened.Count);
        }

        public static void Reset()
        {
            LastDoubleOpen.Clear();
            LastDoubleLocked.Clear();
            LastCdLocked.Clear();
            LastSdOpened.Clear();
            LastSdMoving.Clear();
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

            _scanTimer += Mathf.Min(Time.deltaTime, 0.1f);
            if (_scanTimer < ScanInterval) return;
            _scanTimer = 0f;

            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;

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
                    SendDoorChange(DoorType.ConnectedDoors, id, false, lk, false, false, false);
                    LastCdLocked[id] = lk;
                }
            }
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
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            ulong id = WorldId.FromGameObject(d.gameObject);
            if (id == 0) return;
            bool lo;
            LastDoubleOpen.TryGetValue(id, out lo);
            if (lo == open && LastDoubleLocked.ContainsKey(id))
                return;
            LastDoubleOpen[id] = open;
            LastDoubleLocked[id] = d.locked;
            PlaytestLog.Event("Door", (open ? "open" : "close") + " id=" + id.ToString("X16"));
            SendDoorChange(DoorType.DoorwayDouble, id, open, d.locked, false, false, false);
        }

        public static void NotifySlidingDoor(EventSlidingDoor sd)
        {
            if (sd == null || NetGate.IsApplying) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            ulong id = WorldId.FromGameObject(sd.gameObject);
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

        public static void HandleMessage(DoorStateMessage msg)
        {
            ulong id = unchecked((ulong)msg.WorldId);
            switch (msg.Type)
            {
                case DoorType.DoorwayDouble: ApplyDoorwayDouble(id, msg); break;
                case DoorType.ConnectedDoors: ApplyConnectedDoors(id, msg); break;
                case DoorType.EventSlidingDoor: ApplySlidingDoor(id, msg); break;
            }
        }

        private static void ApplyDoorwayDouble(ulong id, DoorStateMessage msg)
        {
            Doorway_Double d;
            if (!WorldRegistry.TryGetDoubleDoor(id, out d) || d == null) return;

            var net = LanNetworkManager.Instance;
            bool hostLocked = false;
            try { hostLocked = d.locked; } catch { }
            if (net != null && net.Role == NetworkRole.Host && hostLocked)
            {
                if (msg.Open)
                {
                    if (DoorNative.IsFlavorSeal(d.gameObject))
                    {
                        PlaytestLog.Event("Door", "ignore client open locked " + d.gameObject.name);
                        return;
                    }
                    try { d.locked = false; } catch { }
                    msg.Locked = false;
                    PlaytestLog.Event("Door", "honor client open " + d.gameObject.name);
                }
                else
                    msg.Locked = true;
            }

            DoorNative.ApplyDoubleDoor(d, msg.Open, msg.Locked);
            LastDoubleOpen[id] = msg.Open;
            LastDoubleLocked[id] = msg.Locked;
        }

        private static void ApplyConnectedDoors(ulong id, DoorStateMessage msg)
        {
            ConnectedDoors cd;
            if (!WorldRegistry.TryGetConnectedDoor(id, out cd) || cd == null) return;

            // Ignore InProgress/Forwards — those must stay local (room entry).
            DoorNative.ApplyConnectedDoors(cd, msg.Locked);
            LastCdLocked[id] = msg.Locked;
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
