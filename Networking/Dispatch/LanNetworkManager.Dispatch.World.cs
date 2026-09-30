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
            {
                var door = DoorStateMessage.Deserialize(reader);
                if (_role == NetworkRole.Host) door.SenderPlayerId = senderId;
                DoorHandlers.HandleDoorState(door, senderId);
                return true;
            }
            case NetMessageType.EnemyState:
                EnemyHandlers.HandleEnemyState(EnemyStateMessage.Deserialize(reader));
                return true;
            case NetMessageType.EnemySpawn:
                EnemyHandlers.HandleEnemySpawn(EnemySpawnMessage.Deserialize(reader));
                return true;
            case NetMessageType.EnemyDamage:
            {
                var dmg = EnemyDamageMessage.Deserialize(reader);
                if (_role == NetworkRole.Host) dmg.AttackerPlayerId = senderId;
                EnemyHandlers.HandleEnemyDamage(dmg);
                return true;
            }
            case NetMessageType.PuzzleState:
            {
                var puzzle = PuzzleStateMessage.Deserialize(reader);
                if (ModConfig.PuzzlesEnabled)
                {
                    if (_role == NetworkRole.Host) puzzle.SenderPlayerId = senderId;
                    PuzzleHandlers.HandlePuzzleState(puzzle);
                }
                return true;
            }
            case NetMessageType.BossState:
                BossHandlers.HandleBossState(BossStateMessage.Deserialize(reader));
                return true;
            case NetMessageType.WorldPickupState:
                WorldPickupHandlers.HandleWorldPickupState(WorldPickupStateMessage.Deserialize(reader));
                return true;
            case NetMessageType.WorldPickupClaim:
            {
                var claim = WorldPickupClaimMessage.Deserialize(reader);
                if (_role == NetworkRole.Host) claim.ClaimerPlayerId = senderId;
                WorldPickupHandlers.HandleWorldPickupClaim(claim);
                return true;
            }
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
