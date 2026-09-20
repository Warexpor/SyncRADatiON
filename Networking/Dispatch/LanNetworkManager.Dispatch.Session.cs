using LiteNetLib;

namespace SyncRADation.Networking
{
    public sealed partial class LanNetworkManager
    {
        bool TryDispatchSession(NetMessageType type, NetPacketReader reader, int senderId)
        {
            switch (type)
            {
            case NetMessageType.Handshake:
                HandleHandshake(HandshakeMessage.Deserialize(reader), senderId);
                return true;
            case NetMessageType.SceneHello:
                SceneHandlers.HandleSceneHello(SceneHelloMessage.Deserialize(reader));
                return true;
            case NetMessageType.SceneFollow:
                SceneHandlers.HandleSceneFollow(SceneFollowMessage.Deserialize(reader));
                return true;
            case NetMessageType.SnapshotRequest:
                SessionHandlers.HandleSnapshotRequest(SnapshotRequestMessage.Deserialize(reader), senderId);
                return true;
            case NetMessageType.PlayerRoster:
                HandlePlayerRoster(PlayerRosterMessage.Deserialize(reader));
                return true;
            default:
                return false;
            }
        }
    }
}
