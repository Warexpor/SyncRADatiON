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

        internal void SendBossState(IList<BossSnapshotNet> snaps)
        {
            if (snaps == null || snaps.Count == 0) return;
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.BossState);
            writer.Put(snaps.Count);
            for (int i = 0; i < snaps.Count; i++)
                snaps[i].Serialize(writer);
            _net.BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        internal void HandleBossState(BossStateMessage msg)
        {
            _net.BossSync.OnBossStateReceived(msg);
        }
    }
}
