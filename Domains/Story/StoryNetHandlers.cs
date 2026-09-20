using LiteNetLib;
using LiteNetLib.Utils;

namespace SyncRADation.Networking
{
    /// <summary>Story commit / presentation send + apply (thin over StorySyncService).</summary>
    internal sealed class StoryNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal StoryNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void SendStoryCommit(StoryCommitMessage msg)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.StoryCommit);
            msg.Serialize(writer);
            _net.BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        internal void SendStoryPresentation(StoryPresentationMessage msg)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.StoryPresentation);
            msg.Serialize(writer);
            _net.BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        internal void HandleStoryCommit(StoryCommitMessage msg)
        {
            _net.StorySync.ApplyCommit(msg);
        }

        internal void HandleStoryPresentation(StoryPresentationMessage msg)
        {
            _net.StorySync.ApplyPresentation(msg);
        }
    }
}
