using LiteNetLib;

namespace SyncRADation.Networking
{
    public sealed partial class LanNetworkManager
    {
        bool TryDispatchStoryAudio(NetMessageType type, LiteNetLib.Utils.NetDataReader reader, int senderId)
        {
            switch (type)
            {
            case NetMessageType.InteractionRequest:
            {
                var req = InteractionRequestMessage.Deserialize(reader);
                if (StampsSender) req.SenderPlayerId = senderId;
                InteractionHandlers.HandleInteractionRequest(req);
                return true;
            }
            case NetMessageType.InteractionAck:
                InteractionHandlers.HandleInteractionAck(InteractionAckMessage.Deserialize(reader));
                return true;
            case NetMessageType.StoryCommit:
                StoryHandlers.HandleStoryCommit(StoryCommitMessage.Deserialize(reader));
                return true;
            case NetMessageType.StoryPresentation:
                StoryHandlers.HandleStoryPresentation(StoryPresentationMessage.Deserialize(reader));
                return true;
            case NetMessageType.FmodEmitter:
                FmodHandlers.HandleFmodEmitter(FmodEmitterMessage.Deserialize(reader));
                return true;
            case NetMessageType.FmodEmitterRequest:
                FmodHandlers.HandleFmodEmitterRequest(FmodEmitterRequestMessage.Deserialize(reader), senderId);
                return true;
            default:
                return false;
            }
        }
    }
}
