using LiteNetLib;
using LiteNetLib.Utils;
using SyncRADation.Players;
using SyncRADation.Sync;

namespace SyncRADation.Networking
{
    /// <summary>Party life (revive / wipe), party save token and room report wire.</summary>
    internal sealed class PartyNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal PartyNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void SendPartyLife(PartyLifeMessage msg)
        {
            if (_net.Role != NetworkRole.Host) return;
            var w = new NetDataWriter();
            w.Put((byte)NetMessageType.PartyLife);
            msg.Serialize(w);
            _net.BroadcastRaw(w, DeliveryMethod.ReliableOrdered);
        }

        internal void HandlePartyLife(PartyLifeMessage msg, int senderId)
        {
            // Only the host authors revive / wipe.
            if (_net.Role == NetworkRole.Host) return;
            if (senderId != 0) return;
            if (msg.Kind == PartyLifeKind.Revive)
                NetworkDamageSystem.ApplyRevive(msg);
            else if (msg.Kind == PartyLifeKind.Wipe)
                NetworkDamageSystem.ApplyWipeAsClient(msg);
        }

        /// <summary>Host → clients. targetPlayerId &gt;= 0 unicasts (join handshake).</summary>
        internal void SendPartySave(PartySaveToken token, byte flags, int targetPlayerId = -1)
        {
            if (_net.Role != NetworkRole.Host) return;
            var msg = new PartySaveMessage
            {
                Slot = token.Slot,
                Counter = token.Counter,
                Stamp = token.Stamp,
                Flags = flags
            };
            var w = new NetDataWriter();
            w.Put((byte)NetMessageType.PartySave);
            msg.Serialize(w);
            if (targetPlayerId >= 0)
                _net.SendToPlayer(targetPlayerId, w, DeliveryMethod.ReliableOrdered);
            else
                _net.BroadcastRaw(w, DeliveryMethod.ReliableOrdered);
        }

        internal void HandlePartySave(PartySaveMessage msg, int senderId)
        {
            if (_net.Role != NetworkRole.Client) return;
            if (senderId != 0) return;
            PartySaveService.OnHostAnnounced(msg);
        }

        internal void SendPartyRoom(string room)
        {
            if (_net.Role != NetworkRole.Client) return;
            var msg = new PartyRoomMessage { PlayerId = _net.LocalPlayerId, Room = room ?? "" };
            var w = new NetDataWriter();
            w.Put((byte)NetMessageType.PartyRoom);
            msg.Serialize(w);
            _net.BroadcastRaw(w, DeliveryMethod.ReliableOrdered);
        }

        internal void HandlePartyRoom(PartyRoomMessage msg, int senderId)
        {
            if (_net.Role != NetworkRole.Host) return;
            if (senderId < 1) return;
            PartyVitals.NoteRoom(senderId, msg.Room);
        }
    }
}
