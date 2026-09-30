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
            {
                var hello = SceneHelloMessage.Deserialize(reader);
                if (_role == NetworkRole.Host) hello.SenderPlayerId = senderId;
                SceneHandlers.HandleSceneHello(hello);
                return true;
            }
            case NetMessageType.SceneFollow:
            {
                var follow = SceneFollowMessage.Deserialize(reader);
                if (_role == NetworkRole.Host) follow.SenderPlayerId = senderId;
                SceneHandlers.HandleSceneFollow(follow);
                return true;
            }
            case NetMessageType.SceneDiff:
            {
                var diff = SceneDiffMessage.Deserialize(reader);
                if (_role == NetworkRole.Client && senderId == 0) SceneHandlers.HandleSceneDiff(diff); // host-authored only
                return true;
            }
            case NetMessageType.SnapshotRequest:
            {
                var req = SnapshotRequestMessage.Deserialize(reader);
                if (_role == NetworkRole.Host) req.SenderPlayerId = senderId;
                SessionHandlers.HandleSnapshotRequest(req, senderId);
                return true;
            }
            case NetMessageType.PlayerRoster:
                HandlePlayerRoster(PlayerRosterMessage.Deserialize(reader));
                return true;
            default:
                return false;
            }
        }
    }
}
