using LiteNetLib;
using LiteNetLib.Utils;
using SyncRADation.Config;

namespace SyncRADation.Networking
{
    public sealed partial class LanNetworkManager
    {
        bool TryDispatchPlayers(NetMessageType type, NetPacketReader reader, int senderId)
        {
            switch (type)
            {
            case NetMessageType.PlayerState:
                AvatarHandlers.HandlePlayerState(PlayerStateMessage.Deserialize(reader), senderId);
                return true;
            case NetMessageType.BonePose:
                AvatarHandlers.HandleBonePose(BonePoseMessage.Deserialize(reader), senderId);
                return true;
            case NetMessageType.AvatarOneShot:
                AvatarHandlers.HandleAvatarOneShot(AvatarOneShotMessage.Deserialize(reader), senderId);
                return true;
            case NetMessageType.PlayerVital:
            {
                var vital = PlayerVitalMessage.Deserialize(reader);
                if (_role == NetworkRole.Host) vital.SenderPlayerId = senderId; // identity = peer map, not wire
                AvatarHandlers.HandlePlayerVital(vital, senderId);
                return true;
            }
            case NetMessageType.DropItemSpawn:
            {
                var dropMsg = DropItemSpawnMessage.Deserialize(reader);
                if (_role == NetworkRole.Host) dropMsg.SenderID = (byte)senderId; // owner namespace of the item key
                DroppedItemHandlers.HandleDropItemSpawn(dropMsg);
                if (_role == NetworkRole.Host)
                {
                    var w = new NetDataWriter();
                    w.Put((byte)NetMessageType.DropItemSpawn);
                    dropMsg.Serialize(w);
                    RelayRaw(w, DeliveryMethod.ReliableOrdered, senderId);
                }
                return true;
            }
            case NetMessageType.ItemPickedUp:
            {
                var pickMsg = ItemPickedUpMessage.Deserialize(reader);
                // Host is the only author of a floor-item claim; a client may only retire its own drop.
                if (_role == NetworkRole.Host && !DroppedItemHandlers.AcceptClientPickedUp(ref pickMsg, senderId))
                    return true;
                DroppedItemHandlers.HandleItemPickedUp(pickMsg);
                if (_role == NetworkRole.Host)
                {
                    var w = new NetDataWriter();
                    w.Put((byte)NetMessageType.ItemPickedUp);
                    pickMsg.Serialize(w);
                    RelayRaw(w, DeliveryMethod.ReliableOrdered, senderId);
                }
                return true;
            }
            case NetMessageType.DropRekey:
            {
                var rekey = DropRekeyMessage.Deserialize(reader);
                if (_role != NetworkRole.Host) DroppedItemHandlers.HandleDropRekey(rekey); // host-authored only
                return true;
            }
            case NetMessageType.FriendlyFire:
            {
                var ff = FriendlyFireMessage.Deserialize(reader);
                if (_role == NetworkRole.Host)
                {
                    // Host forwards FF to the target peer: refuse when FF is off or the claim is self/forged.
                    if (ModConfig.FriendlyFire?.Value != true || ff.TargetPlayerId == senderId)
                        return true;
                    ff.AttackerPlayerId = senderId;
                }
                CombatHandlers.HandleFriendlyFire(ff, senderId);
                return true;
            }
            case NetMessageType.DeathPolicy:
            {
                var dp = DeathPolicyMessage.Deserialize(reader);
                if (_role == NetworkRole.Host) dp.SenderPlayerId = senderId;
                CombatHandlers.HandleDeathPolicy(dp);
                if (_role == NetworkRole.Host && dp.Kind == DeathKind.ClientDowned)
                {
                    // Relay (3+ players): every other client learns at once that this one is down, instead of waiting
                    // for its next 5 Hz vital. Reliable + ordered like the original. Stamped with the real sender id.
                    var w = new NetDataWriter();
                    w.Put((byte)NetMessageType.DeathPolicy);
                    dp.Serialize(w);
                    RelayRaw(w, DeliveryMethod.ReliableOrdered, senderId);
                }
                return true;
            }
            case NetMessageType.PartyLife:
                PartyHandlers.HandlePartyLife(PartyLifeMessage.Deserialize(reader), senderId);
                return true;
            case NetMessageType.PartySave:
                PartyHandlers.HandlePartySave(PartySaveMessage.Deserialize(reader), senderId);
                return true;
            case NetMessageType.PartyRoom:
                PartyHandlers.HandlePartyRoom(PartyRoomMessage.Deserialize(reader), senderId);
                return true;
            default:
                return false;
            }
        }
    }
}
