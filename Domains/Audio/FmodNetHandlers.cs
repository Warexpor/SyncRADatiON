using LiteNetLib;
using LiteNetLib.Utils;

namespace SyncRADation.Networking
{
    /// <summary>World FMOD emitter Play/Stop send + apply.</summary>
    internal sealed class FmodNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal FmodNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void SendFmodEmitter(FmodEmitterMessage msg)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.FmodEmitter);
            msg.Serialize(writer);
            _net.BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        internal void HandleFmodEmitter(FmodEmitterMessage msg)
        {
            FmodEmitterSync.Handle(msg);
        }
    }
}
