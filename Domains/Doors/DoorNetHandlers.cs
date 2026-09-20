using LiteNetLib;
using LiteNetLib.Utils;

namespace SyncRADation.Networking
{
    /// <summary>Door open/close send + apply (thin wrapper over DoorSyncService).</summary>
    internal sealed class DoorNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal DoorNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void SendDoorState(DoorStateMessage msg)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.DoorState);
            msg.Serialize(writer);
            _net.BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        internal void HandleDoorState(DoorStateMessage doorMsg, int senderId)
        {
            DoorSyncService.HandleMessage(doorMsg);
            if (_net.Role == NetworkRole.Host)
            {
                var w = new NetDataWriter();
                w.Put((byte)NetMessageType.DoorState);
                doorMsg.Serialize(w);
                _net.RelayRaw(w, DeliveryMethod.ReliableOrdered, senderId);
            }
        }
    }
}
