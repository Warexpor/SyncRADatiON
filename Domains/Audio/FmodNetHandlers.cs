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

        /// <summary>Client → host: world emitter the client triggered locally.</summary>
        internal void SendFmodEmitterRequest(FmodEmitterRequestMessage msg)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.FmodEmitterRequest);
            msg.Serialize(writer);
            _net.BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Host → every client except the one that originated the sound.</summary>
        internal void SendFmodEmitterExcept(FmodEmitterMessage msg, int exceptPlayerId)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.FmodEmitter);
            msg.Serialize(writer);
            _net.BroadcastRawExcept(writer, DeliveryMethod.ReliableOrdered, exceptPlayerId);
        }

        internal void HandleFmodEmitter(FmodEmitterMessage msg)
        {
            FmodEmitterSync.Handle(msg);
        }

        internal void HandleFmodEmitterRequest(FmodEmitterRequestMessage msg, int senderId)
        {
            if (_net.Role != NetworkRole.Host || senderId < 1) return;
            FmodEmitterSync.HandleRequest(msg, senderId);
        }
    }
}
