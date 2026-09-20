using LiteNetLib;
using LiteNetLib.Utils;

namespace SyncRADation.Networking
{
    /// <summary>Party key ring + storage box blob wire.</summary>
    internal sealed class InventoryNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal InventoryNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void SendStorageBoxBlob(StorageBoxItem[] items)
        {
            var msg = new StorageBoxBlobMessage { Items = items };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.StorageBoxBlob);
            msg.Serialize(writer);
            _net.BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        internal void HandleStorageBoxBlob(StorageBoxBlobMessage msg)
        {
            _net.StorageSync.Apply(msg);
        }

        internal void SendPartyKeyRing(ushort[] enums)
        {
            var msg = new PartyKeyRingMessage { ItemEnums = enums };
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.PartyKeyRing);
            msg.Serialize(writer);
            _net.BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        internal void HandlePartyKeyRing(PartyKeyRingMessage msg)
        {
            PartyKeyRing.ApplyMessage(msg);
        }
    }
}
