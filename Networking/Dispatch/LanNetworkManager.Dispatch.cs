using System;
using LiteNetLib;

namespace SyncRADation.Networking
{
    public sealed partial class LanNetworkManager
    {
        void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            // Leftover events in a PollEvents batch after a stop request / teardown belong to a dead session.
            if (_stopPending || _net == null || _role == NetworkRole.Offline)
                return;
            if (!reader.TryGetByte(out byte messageType))
                return;

            var type = (NetMessageType)messageType;
            int senderId;
            if (!_peerToId.TryGetValue(peer, out senderId))
                return; // not a registered peer (rejected / torn down)

            // Pre-handshake gating: nothing but Handshake is processed until the peer is verified.
            if (type != NetMessageType.Handshake && !_readyPeers.Contains(senderId))
            {
                if (_loggedGateDrops.Add("pre:" + senderId))
                    ModRuntime.Log?.Warning("[Network] dropped " + type + " from player " + senderId + " before handshake");
                return;
            }

            // Trust boundary: a client may not author host-only world/story/session state.
            if (_role == NetworkRole.Host && IsHostAuthoredOnly(type))
            {
                if (_loggedGateDrops.Add("host-only:" + messageType))
                    ModRuntime.Log?.Warning("[Network] dropped host-only " + type + " sent by player " + senderId);
                return;
            }

            try
            {
                if (!(TryDispatchSession(type, reader, senderId)
                    || TryDispatchPlayers(type, reader, senderId)
                    || TryDispatchWorld(type, reader, senderId)
                    || TryDispatchStoryAudio(type, reader, senderId)))
                {
                    ModRuntime.Log?.Warning("[Network] Unhandled message type: " + type + " (" + messageType + ")");
                }
            }
            catch (Exception ex)
            {
                // One bad packet / handler must not abort the rest of the PollEvents batch.
                int n;
                _dispatchFailures.TryGetValue(messageType, out n);
                n++;
                _dispatchFailures[messageType] = n;
                if (n == 1 || n % 200 == 0)
                    ModRuntime.Log?.Error("[Network] " + type + " from player " + senderId + " threw (x" + n + "): " + ex);
            }
        }

        /// <summary>Message types only the host may originate; a client sending them is dropped on the host.</summary>
        static bool IsHostAuthoredOnly(NetMessageType type)
        {
            switch (type)
            {
            case NetMessageType.EnemyState:
            case NetMessageType.BossState:
            case NetMessageType.WorldPickupState:
            case NetMessageType.WorldPickupGrant:
            case NetMessageType.StoryCommit:
            case NetMessageType.StoryPresentation:
            case NetMessageType.StorageBoxBlob:
            case NetMessageType.FmodEmitter:
            case NetMessageType.InteractionAck:
            case NetMessageType.PlayerRoster:
            case NetMessageType.WorldPickupDeny:
            case NetMessageType.DropRekey:
            case NetMessageType.SceneDiff:
                return true;
            default:
                return false;
            }
        }
    }
}
