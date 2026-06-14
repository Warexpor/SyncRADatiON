// SyncRADation — door state tracking + broadcast: Doorway_Double, ConnectedDoors, EventSlidingDoor
using System;
using FMODUnity;
using UnityEngine;

namespace SyncRADation.Networking
{
    public static class DoorSyncService
    {
        private static Doorway_Double[] _doubleDoors;
        private static bool[] _lastDoubleOpen;
        private static bool[] _lastDoubleLocked;

        private static ConnectedDoors[] _connectedDoors;
        private static bool[] _lastCdInProgress;
        private static bool[] _lastCdForwards;
        private static bool[] _lastCdLocked;

        private static EventSlidingDoor[] _slidingDoors;
        private static bool[] _lastSdOpened;
        private static bool[] _lastSdMoving;

        private static float _scanTimer;
        private const float ScanInterval = 0.3f;

        public static void RefreshScene()
        {
            try
            {
                _doubleDoors = GameObject.FindObjectsOfType<Doorway_Double>();
                _lastDoubleOpen = new bool[_doubleDoors != null ? _doubleDoors.Length : 0];
                _lastDoubleLocked = new bool[_doubleDoors != null ? _doubleDoors.Length : 0];
                if (_doubleDoors != null)
                {
                    for (int i = 0; i < _doubleDoors.Length; i++)
                    {
                        if (_doubleDoors[i] != null)
                        {
                            _lastDoubleOpen[i] = _doubleDoors[i].open;
                            _lastDoubleLocked[i] = _doubleDoors[i].locked;
                        }
                    }
                }

                _connectedDoors = GameObject.FindObjectsOfType<ConnectedDoors>();
                _lastCdInProgress = new bool[_connectedDoors != null ? _connectedDoors.Length : 0];
                _lastCdForwards = new bool[_connectedDoors != null ? _connectedDoors.Length : 0];
                _lastCdLocked = new bool[_connectedDoors != null ? _connectedDoors.Length : 0];
                if (_connectedDoors != null)
                {
                    for (int i = 0; i < _connectedDoors.Length; i++)
                    {
                        if (_connectedDoors[i] != null)
                        {
                            _lastCdInProgress[i] = _connectedDoors[i].inProgress;
                            _lastCdForwards[i] = _connectedDoors[i].forwards;
                            _lastCdLocked[i] = _connectedDoors[i].locked;
                        }
                    }
                }

                _slidingDoors = GameObject.FindObjectsOfType<EventSlidingDoor>();
                _lastSdOpened = new bool[_slidingDoors != null ? _slidingDoors.Length : 0];
                _lastSdMoving = new bool[_slidingDoors != null ? _slidingDoors.Length : 0];
                if (_slidingDoors != null)
                {
                    for (int i = 0; i < _slidingDoors.Length; i++)
                    {
                        if (_slidingDoors[i] != null)
                        {
                            _lastSdOpened[i] = _slidingDoors[i].opened;
                            _lastSdMoving[i] = _slidingDoors[i].moving;
                        }
                    }
                }

                ModRuntime.Log?.Msg("[DoorSync] Scanned: " + (_doubleDoors != null ? _doubleDoors.Length : 0) + " Doorway_Double, "
                    + (_connectedDoors != null ? _connectedDoors.Length : 0) + " ConnectedDoors, "
                    + (_slidingDoors != null ? _slidingDoors.Length : 0) + " EventSlidingDoor");
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[DoorSync] Refresh failed: " + ex.Message);
            }
        }

        public static void Reset()
        {
            _doubleDoors = null;
            _lastDoubleOpen = null;
            _lastDoubleLocked = null;
            _connectedDoors = null;
            _lastCdInProgress = null;
            _lastCdForwards = null;
            _lastCdLocked = null;
            _slidingDoors = null;
            _lastSdOpened = null;
            _lastSdMoving = null;
            _scanTimer = 0f;
        }

        public static void Tick()
        {
            if (_doubleDoors == null) { RefreshScene(); return; }

            _scanTimer += Mathf.Min(Time.deltaTime, 0.1f);
            if (_scanTimer < ScanInterval) return;
            _scanTimer = 0f;

            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;

            // Doorway_Double
            for (int i = 0; i < _doubleDoors.Length; i++)
            {
                var d = _doubleDoors[i];
                if (d == null) continue;
                bool openNow = d.open;
                bool lockedNow = d.locked;
                if (openNow != _lastDoubleOpen[i] || lockedNow != _lastDoubleLocked[i])
                {
                    SendDoorChange(DoorType.DoorwayDouble, (short)i, openNow, lockedNow, false, false, false);
                    _lastDoubleOpen[i] = openNow;
                    _lastDoubleLocked[i] = lockedNow;
                }
            }

            // ConnectedDoors
            for (int i = 0; i < _connectedDoors.Length; i++)
            {
                var cd = _connectedDoors[i];
                if (cd == null) continue;
                bool ip = cd.inProgress;
                bool fw = cd.forwards;
                bool lk = cd.locked;
                if (ip != _lastCdInProgress[i] || fw != _lastCdForwards[i] || lk != _lastCdLocked[i])
                {
                    SendDoorChange(DoorType.ConnectedDoors, (short)i, false, lk, ip, fw, false);
                    _lastCdInProgress[i] = ip;
                    _lastCdForwards[i] = fw;
                    _lastCdLocked[i] = lk;
                }
            }

            // EventSlidingDoor
            for (int i = 0; i < _slidingDoors.Length; i++)
            {
                var sd = _slidingDoors[i];
                if (sd == null) continue;
                bool op = sd.opened;
                bool mv = sd.moving;
                if (op != _lastSdOpened[i] || mv != _lastSdMoving[i])
                {
                    SendDoorChange(DoorType.EventSlidingDoor, (short)i, op, false, false, false, mv);
                    _lastSdOpened[i] = op;
                    _lastSdMoving[i] = mv;
                }
            }
        }

        private static void SendDoorChange(DoorType type, short index, bool open, bool locked,
            bool inProgress, bool forwards, bool moving)
        {
            var msg = new DoorStateMessage
            {
                SenderPlayerId = LanNetworkManager.Instance.LocalPlayerId,
                Type = type,
                Index = index,
                Open = open,
                Locked = locked,
                InProgress = inProgress,
                Forwards = forwards,
                Moving = moving
            };
            LanNetworkManager.Instance.SendDoorState(msg);
        }

        public static void HandleMessage(DoorStateMessage msg)
        {
            switch (msg.Type)
            {
                case DoorType.DoorwayDouble: ApplyDoorwayDouble(msg); break;
                case DoorType.ConnectedDoors: ApplyConnectedDoors(msg); break;
                case DoorType.EventSlidingDoor: ApplySlidingDoor(msg); break;
            }
        }

        private static void ApplyDoorwayDouble(DoorStateMessage msg)
        {
            if (_doubleDoors == null) RefreshScene();
            if (_doubleDoors == null || msg.Index < 0 || msg.Index >= _doubleDoors.Length) return;
            var d = _doubleDoors[msg.Index];
            if (d == null) return;

            if (msg.Open != d.open)
            {
                d.open = msg.Open;
                d.locked = msg.Locked;
                if (d.enabled && d.gameObject.activeInHierarchy)
                {
                    try
                    {
                        if (msg.Open && d.OpenSFX != null) d.OpenSFX.Play();
                        else if (!msg.Open && d.CloseSFX != null) d.CloseSFX.Play();
                    }
                    catch { }
                }
            }
            else
            {
                d.locked = msg.Locked;
            }
            if (msg.Index < _lastDoubleOpen.Length)
            {
                _lastDoubleOpen[msg.Index] = msg.Open;
                _lastDoubleLocked[msg.Index] = msg.Locked;
            }
        }

        private static void ApplyConnectedDoors(DoorStateMessage msg)
        {
            if (_connectedDoors == null) RefreshScene();
            if (_connectedDoors == null || msg.Index < 0 || msg.Index >= _connectedDoors.Length) return;
            var cd = _connectedDoors[msg.Index];
            if (cd == null) return;

            cd.locked = msg.Locked;
            if (msg.InProgress != cd.inProgress)
            {
                cd.inProgress = msg.InProgress;
                cd.forwards = msg.Forwards;
            }
            if (msg.Index < _lastCdInProgress.Length)
            {
                _lastCdInProgress[msg.Index] = msg.InProgress;
                _lastCdForwards[msg.Index] = msg.Forwards;
                _lastCdLocked[msg.Index] = msg.Locked;
            }
        }

        private static void ApplySlidingDoor(DoorStateMessage msg)
        {
            if (_slidingDoors == null) RefreshScene();
            if (_slidingDoors == null || msg.Index < 0 || msg.Index >= _slidingDoors.Length) return;
            var sd = _slidingDoors[msg.Index];
            if (sd == null) return;

            sd.opened = msg.Open;
            sd.moving = msg.Moving;
            if (msg.Index < _lastSdOpened.Length)
            {
                _lastSdOpened[msg.Index] = msg.Open;
                _lastSdMoving[msg.Index] = msg.Moving;
            }
        }
    }
}
