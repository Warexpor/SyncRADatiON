using LiteNetLib;
using SyncRADation.Sync;

namespace SyncRADation.Networking
{
    public sealed partial class LanNetworkManager
    {
        bool TryDispatchSession(NetMessageType type, LiteNetLib.Utils.NetDataReader reader, int senderId)
        {
            switch (type)
            {
            case NetMessageType.Handshake:
                HandleHandshake(HandshakeMessage.Deserialize(reader), senderId);
                return true;
            case NetMessageType.SceneHello:
            {
                var hello = SceneHelloMessage.Deserialize(reader);
                if (StampsSender) hello.SenderPlayerId = senderId;
                SceneHandlers.HandleSceneHello(hello);
                return true;
            }
            case NetMessageType.SceneFollow:
            {
                var follow = SceneFollowMessage.Deserialize(reader);
                if (StampsSender) follow.SenderPlayerId = senderId;
                SceneHandlers.HandleSceneFollow(follow);
                return true;
            }
            case NetMessageType.SceneDiff:
            {
                var diff = SceneDiffMessage.Deserialize(reader);
                if (_role == NetworkRole.Client && senderId == 0) SceneHandlers.HandleSceneDiff(diff); // host-authored only
                return true;
            }
            case NetMessageType.WorldDigest:
            {
                // Host digest lines to clients, a client's findings to the host (DesyncCheck checks kind and sender).
                DesyncCheck.Handle(this, WorldDigestMessage.Deserialize(reader), senderId);
                return true;
            }
            case NetMessageType.SnapshotRequest:
            {
                var req = SnapshotRequestMessage.Deserialize(reader);
                if (StampsSender) req.SenderPlayerId = senderId;
                SessionHandlers.HandleSnapshotRequest(req, senderId);
                return true;
            }
            case NetMessageType.PeerScenes:
                SceneOwners.HandlePeerScenes(this, PeerScenesMessage.Deserialize(reader), senderId);
                return true;
            case NetMessageType.WorldRelay:
                HandleWorldRelay(WorldRelayMessage.Deserialize(reader), senderId);
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
