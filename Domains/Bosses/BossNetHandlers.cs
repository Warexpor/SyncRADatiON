using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;

namespace SyncRADation.Networking
{
    /// <summary>Boss snapshot send + apply (thin over BossSyncService).</summary>
    internal sealed class BossNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal BossNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        // Persistent on purpose: serialized size of an empty snapshot struct, a pure cache that never depends on session state.
        private static int _snapBytes;

        static int SnapshotBytes()
        {
            if (_snapBytes == 0)
            {
                var probe = new NetDataWriter();
                new BossSnapshotNet().Serialize(probe);
                _snapBytes = probe.Length;
            }
            return _snapBytes;
        }

        /// <summary>Continuous snapshot: Sequenced on the State channel (see EnemyNetHandlers.SendEnemyState).</summary>
        internal void SendBossState(IList<BossSnapshotNet> snaps)
        {
            if (snaps == null || snaps.Count == 0) return;
            int perPacket = (_net.StatePacketBudget() - 1 - 4) / SnapshotBytes();
            if (perPacket < 1) perPacket = 1;
            if (perPacket > NetWire.MaxBosses) perPacket = NetWire.MaxBosses;
            int total = NetWire.ClampCount(snaps.Count, 1024, "BossState send");
            for (int start = 0; start < total; start += perPacket)
            {
                int n = total - start;
                if (n > perPacket) n = perPacket;
                var writer = new NetDataWriter();
                writer.Put((byte)NetMessageType.BossState);
                writer.Put(n);
                for (int i = 0; i < n; i++)
                    snaps[start + i].Serialize(writer);
                _net.BroadcastState(writer);
            }
        }

        internal void HandleBossState(BossStateMessage msg)
        {
            _net.BossSync.OnBossStateReceived(msg);
        }

        /// <summary>Client → host: boss hitbox damage / Falke stab / spear take.</summary>
        internal void SendBossHitToHost(long worldId, BossHitKind kind, int amount)
        {
            if (_net.Role != NetworkRole.Client) return;
            var msg = new BossHitMessage
            {
                SenderPlayerId = _net.LocalPlayerId,
                WorldId = worldId,
                Kind = kind,
                Amount = amount
            };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.BossHit);
            msg.Serialize(writer);
            if (_net.TryGetPeer(0, out var peer) && peer.ConnectionState == ConnectionState.Connected)
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Host → every client: boss presentation event (spear taken, Chimera rifle shot).</summary>
        internal void BroadcastBossEvent(long worldId, BossHitKind kind, int amount)
        {
            if (_net.Role != NetworkRole.Host || !_net.IsConnected) return;
            var msg = new BossHitMessage
            {
                SenderPlayerId = _net.LocalPlayerId,
                WorldId = worldId,
                Kind = kind,
                Amount = amount
            };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.BossHit);
            msg.Serialize(writer);
            _net.BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        internal void HandleBossHit(BossHitMessage msg, int senderId)
        {
            if (_net.Role == NetworkRole.Host)
                _net.BossSync.ApplyHitOnHost(msg, senderId);
            else
                _net.BossSync.ApplyBossEventOnClient(msg);
        }
    }
}
