using System;
using FMODUnity;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace SyncRADation.Networking
{
    public static class DoorSyncService
    {
        private static Doorway_Double[] _doubleDoors;
        private static bool[] _lastDoubleOpen;
        private static bool[] _lastDoubleLocked;

        private static float _scanTimer;
        private const float ScanInterval = 0.5f;

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
                ModRuntime.Log?.Msg("[DoorSync] Scanned " + (_doubleDoors != null ? _doubleDoors.Length : 0) + " Doorway_Double");
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
            _scanTimer = 0f;
        }

        public static void Tick()
        {
            if (_doubleDoors == null)
            {
                RefreshScene();
                return;
            }

            _scanTimer += Mathf.Min(Time.deltaTime, 0.1f);
            if (_scanTimer < ScanInterval)
                return;
            _scanTimer = 0f;

            for (int i = 0; i < _doubleDoors.Length; i++)
            {
                var d = _doubleDoors[i];
                if (d == null) continue;

                bool openNow = d.open;
                bool lockedNow = d.locked;

                if (openNow != _lastDoubleOpen[i] || lockedNow != _lastDoubleLocked[i])
                {
                    var msg = new DoorStateMessage
                    {
                        Type = DoorType.DoorwayDouble,
                        Index = (short)i,
                        Open = openNow,
                        Locked = lockedNow
                    };

                    var net = LanNetworkManager.Instance;
                    if (net != null && net.IsConnected)
                    {
                        net.Send(NetMessageType.DoorState, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
                    }

                    _lastDoubleOpen[i] = openNow;
                    _lastDoubleLocked[i] = lockedNow;
                    ModRuntime.Log?.Msg("[DoorSync] Door " + i + " open=" + openNow + " locked=" + lockedNow);
                }
            }
        }

        public static void HandleMessage(DoorStateMessage msg)
        {
            switch (msg.Type)
            {
                case DoorType.DoorwayDouble:
                    ApplyDoorwayDouble(msg);
                    break;
            }
        }

        private static void ApplyDoorwayDouble(DoorStateMessage msg)
        {
            if (_doubleDoors == null)
                RefreshScene();

            if (_doubleDoors == null || msg.Index < 0 || msg.Index >= _doubleDoors.Length)
            {
                ModRuntime.Log?.Warning("[DoorSync] Invalid Doorway_Double index " + msg.Index);
                return;
            }

            var d = _doubleDoors[msg.Index];
            if (d == null) return;

            bool wasOpen = d.open;
            if (msg.Open != wasOpen)
            {
                d.open = msg.Open;
                d.locked = msg.Locked;

                if (d.enabled && d.gameObject.activeInHierarchy)
                {
                    try
                    {
                        if (msg.Open && d.OpenSFX != null)
                            d.OpenSFX.Play();
                        else if (!msg.Open && d.CloseSFX != null)
                            d.CloseSFX.Play();
                    }
                    catch (Exception ex)
                    {
                        ModRuntime.Log?.Warning("[DoorSync] FMOD Play failed: " + ex.Message);
                    }
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

            ModRuntime.Log?.Msg("[DoorSync] Applied Doorway_Double " + msg.Index + " open=" + msg.Open);
        }

    }
}
