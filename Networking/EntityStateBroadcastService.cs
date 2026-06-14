using LiteNetLib;
using LiteNetLib.Utils;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    public static class EntityStateBroadcastService
    {
        private static NetPeer _peer;
        private static float _sendTimer;

        private static EntitySnapshotNet[] _buffer = new EntitySnapshotNet[128];

        public static void SetPeer(NetPeer peer) => _peer = peer;

        public static void Tick()
        {
            if (_peer == null || _peer.ConnectionState != ConnectionState.Connected)
                return;

            _sendTimer += Time.deltaTime;
            if (_sendTimer < PluginInfo.EntitySendInterval)
                return;

            _sendTimer = 0f;
            SendSnapshot();
        }

        private static void SendSnapshot()
        {
            int count = 0;

            count = CollectEnemies(count);
            count = CollectDoors(count);
            count = CollectItems(count);

            if (count == 0)
                return;

            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.EntityState);
            writer.Put(count);
            for (int i = 0; i < count; i++)
                _buffer[i].Serialize(writer);
            _peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        private static int CollectEnemies(int startIndex)
        {
            var enemies = Object.FindObjectsOfType<EnemyController>();
            int idx = startIndex;

            for (int i = 0; i < enemies.Length && idx < _buffer.Length; i++)
            {
                var e = enemies[i];
                if (e == null) continue;

                var go = e.gameObject;
                short id = GameObjectEntityTracker.GetOrAssignId(go);
                var pos = go.transform.position;
                var rot = go.transform.eulerAngles;

                string name = go.name;
                if (name.EndsWith("(Clone)"))
                    name = name.Substring(0, name.Length - 7);

                _buffer[idx] = new EntitySnapshotNet
                {
                    Index = id,
                    PosX = pos.x, PosY = pos.y, PosZ = pos.z,
                    RotY = rot.y,
                    Alive = e.enabled,
                    EntityName = name,
                    HealthPct = 100
                };
                idx++;
            }
            return idx;
        }

        private static int CollectDoors(int startIndex)
        {
            var swingDoors = Object.FindObjectsOfType<SwingDoor>();
            var doubleDoors = Object.FindObjectsOfType<Doorway_Double>();
            int idx = startIndex;

            for (int i = 0; i < swingDoors.Length && idx < _buffer.Length; i++)
            {
                var d = swingDoors[i];
                if (d == null) continue;
                var go = d.gameObject;
                short id = GameObjectEntityTracker.GetOrAssignId(go);
                var pos = go.transform.position;

                _buffer[idx] = new EntitySnapshotNet
                {
                    Index = id,
                    PosX = pos.x, PosY = pos.y, PosZ = pos.z,
                    RotY = 0f,
                    Alive = d.Open,
                    EntityName = "SwingDoor",
                    HealthPct = 100
                };
                idx++;
            }

            for (int i = 0; i < doubleDoors.Length && idx < _buffer.Length; i++)
            {
                var d = doubleDoors[i];
                if (d == null) continue;
                var go = d.gameObject;
                short id = GameObjectEntityTracker.GetOrAssignId(go);
                var pos = go.transform.position;

                _buffer[idx] = new EntitySnapshotNet
                {
                    Index = id,
                    PosX = pos.x, PosY = pos.y, PosZ = pos.z,
                    RotY = 0f,
                    Alive = d.open,
                    EntityName = "Doorway_Double",
                    HealthPct = 100
                };
                idx++;
            }
            return idx;
        }

        private static int CollectItems(int startIndex)
        {
            var items = Object.FindObjectsOfType<ItemPickup>();
            int idx = startIndex;

            for (int i = 0; i < items.Length && idx < _buffer.Length; i++)
            {
                var item = items[i];
                if (item == null || item.triggered) continue;

                var go = item.gameObject;
                short id = GameObjectEntityTracker.GetOrAssignId(go);
                var pos = go.transform.position;

                _buffer[idx] = new EntitySnapshotNet
                {
                    Index = id,
                    PosX = pos.x, PosY = pos.y, PosZ = pos.z,
                    Alive = !item.triggered,
                    EntityName = "ItemPickup",
                    HealthPct = 100
                };
                idx++;
            }
            return idx;
        }

        public static void Stop()
        {
            _peer = null;
            _sendTimer = 0f;
        }
    }
}
