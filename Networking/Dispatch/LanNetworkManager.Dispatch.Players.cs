using LiteNetLib;
using LiteNetLib.Utils;

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
            case NetMessageType.PlayerVital:
                AvatarHandlers.HandlePlayerVital(PlayerVitalMessage.Deserialize(reader), senderId);
                return true;
            case NetMessageType.DropItemSpawn:
            {
                var dropMsg = DropItemSpawnMessage.Deserialize(reader);
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
            case NetMessageType.FriendlyFire:
                CombatHandlers.HandleFriendlyFire(FriendlyFireMessage.Deserialize(reader), senderId);
                return true;
            case NetMessageType.DeathPolicy:
                CombatHandlers.HandleDeathPolicy(DeathPolicyMessage.Deserialize(reader));
                return true;
            default:
                return false;
            }
        }
    }
}
