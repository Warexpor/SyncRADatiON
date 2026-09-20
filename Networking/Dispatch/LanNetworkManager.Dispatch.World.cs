using LiteNetLib;
using LiteNetLib.Utils;
using SyncRADation.Config;

namespace SyncRADation.Networking
{
    public sealed partial class LanNetworkManager
    {
        bool TryDispatchWorld(NetMessageType type, NetPacketReader reader, int senderId)
        {
            switch (type)
            {
            case NetMessageType.DoorState:
                DoorHandlers.HandleDoorState(DoorStateMessage.Deserialize(reader), senderId);
                return true;
            case NetMessageType.EnemyState:
                EnemyHandlers.HandleEnemyState(EnemyStateMessage.Deserialize(reader));
                return true;
            case NetMessageType.EnemySpawn:
                EnemyHandlers.HandleEnemySpawn(EnemySpawnMessage.Deserialize(reader));
                return true;
            case NetMessageType.EnemyDamage:
                EnemyHandlers.HandleEnemyDamage(EnemyDamageMessage.Deserialize(reader));
                return true;
            case NetMessageType.PuzzleState:
                if (ModConfig.PuzzlesEnabled)
                    PuzzleHandlers.HandlePuzzleState(PuzzleStateMessage.Deserialize(reader));
                return true;
            case NetMessageType.BossState:
                BossHandlers.HandleBossState(BossStateMessage.Deserialize(reader));
                return true;
            case NetMessageType.WorldPickupState:
                WorldPickupHandlers.HandleWorldPickupState(WorldPickupStateMessage.Deserialize(reader));
                return true;
            case NetMessageType.WorldPickupClaim:
                WorldPickupHandlers.HandleWorldPickupClaim(WorldPickupClaimMessage.Deserialize(reader));
                return true;
            case NetMessageType.WorldPickupGrant:
                WorldPickupHandlers.HandleWorldPickupGrant(WorldPickupGrantMessage.Deserialize(reader));
                return true;
            case NetMessageType.StorageBoxBlob:
                InventoryHandlers.HandleStorageBoxBlob(StorageBoxBlobMessage.Deserialize(reader));
                return true;
            case NetMessageType.PartyKeyRing:
                InventoryHandlers.HandlePartyKeyRing(PartyKeyRingMessage.Deserialize(reader));
                return true;
            default:
                return false;
            }
        }
    }
}
