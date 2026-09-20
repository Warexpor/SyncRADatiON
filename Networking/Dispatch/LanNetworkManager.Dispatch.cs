using LiteNetLib;

namespace SyncRADation.Networking
{
    public sealed partial class LanNetworkManager
    {
        void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            if (!reader.TryGetByte(out byte messageType))
                return;

            var type = (NetMessageType)messageType;
            int senderId = _peerToId.TryGetValue(peer, out int id) ? id : -1;

            if (!(TryDispatchSession(type, reader, senderId)
                || TryDispatchPlayers(type, reader, senderId)
                || TryDispatchWorld(type, reader, senderId)
                || TryDispatchStoryAudio(type, reader, senderId)))
            {
                ModRuntime.Log?.Warning("[Network] Unhandled message type: " + type + " (" + messageType + ")");
            }
        }
    }
}
