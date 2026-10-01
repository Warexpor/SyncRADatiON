using System.Collections.Generic;

namespace SyncRADation.Networking
{
    /// <summary>
    /// Public one-line forwards onto domain NetHandlers.
    /// Keeps call sites on DoorSyncService / EnemySync / Story / Fmod / Pickups stable.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        public void SendDoorState(DoorStateMessage msg) => DoorHandlers.SendDoorState(msg);

        public void SendPuzzleState(IList<PuzzleStateEntry> entries, bool fullRefresh, int exceptPlayerId = -1) =>
            PuzzleHandlers.SendPuzzleState(entries, fullRefresh, exceptPlayerId);

        public void SendEnemyState(IList<EnemySnapshotNet> snaps) => EnemyHandlers.SendEnemyState(snaps);

        public void SendEnemySpawnRequest(string typeKey, UnityEngine.Vector3 pos, float rotY) =>
            EnemyHandlers.SendEnemySpawnRequest(typeKey, pos, rotY);

        public void BroadcastEnemySpawn(EnemySpawnMessage msg) => EnemyHandlers.BroadcastEnemySpawn(msg);

        public void SendEnemyDamage(int targetPlayerId, ulong enemyWorldId, float damage, bool stagger) =>
            EnemyHandlers.SendEnemyDamage(targetPlayerId, enemyWorldId, damage, stagger);

        public void SendNativeEnemyHit(ulong enemyWorldId, int damage, float fire, float crit, float hurt, bool noSneak) =>
            EnemyHandlers.SendNativeEnemyHit(enemyWorldId, damage, fire, crit, hurt, noSneak);

        public void SendBossState(IList<BossSnapshotNet> snaps) => BossHandlers.SendBossState(snaps);

        public void SendStoryCommit(StoryCommitMessage msg) => StoryHandlers.SendStoryCommit(msg);

        public void SendStoryPresentation(StoryPresentationMessage msg) =>
            StoryHandlers.SendStoryPresentation(msg);

        public void SendFmodEmitter(FmodEmitterMessage msg) => FmodHandlers.SendFmodEmitter(msg);

        public void SendInteractionRequest(ulong worldId, InteractionKind kind, int int0 = 0, int int1 = 0,
            float f0 = 0f, float f1 = 0f, float f2 = 0f, string text = "") =>
            InteractionHandlers.SendInteractionRequest(worldId, kind, int0, int1, f0, f1, f2, text);

        public void SendInteractionAck(int targetPlayerId, long worldId, InteractionKind kind, bool ok, string reason) =>
            InteractionHandlers.SendInteractionAck(targetPlayerId, worldId, kind, ok, reason);

        public void SendDropItem(DropItemSpawnMessage msg) => DroppedItemHandlers.SendDropItem(msg);

        public ushort AllocateItemIndex() => DroppedItemHandlers.AllocateItemIndex();

        public bool DropOverflow(Items.itemlist item, int count) => DroppedItemHandlers.DropOverflow(item, count);

        public bool TryDropItem(AnItem anItem) => DroppedItemHandlers.TryDropItem(anItem);

        public bool TryClaimDropped(int itemKey, int claimerId, out string reason, bool skipLocalGrant = false) =>
            DroppedItemHandlers.TryClaimDropped(itemKey, claimerId, out reason, skipLocalGrant);

        public void SendWorldPickupState(IList<WorldPickupEntry> entries, bool fullRefresh) =>
            WorldPickupHandlers.SendWorldPickupState(entries, fullRefresh);

        public void SendWorldPickupClaim(ulong worldId, Items.itemlist item = Items.itemlist.None, int count = 1) =>
            WorldPickupHandlers.SendWorldPickupClaim(worldId, item, count);

        public void SendFriendlyFire(int targetPlayerId, float damage, UnityEngine.Vector3 hitPos) =>
            CombatHandlers.SendFriendlyFire(targetPlayerId, damage, hitPos);

        public void SendDeathPolicy(DeathKind kind) => CombatHandlers.SendDeathPolicy(kind);

        public void BroadcastSceneHello() => SceneHandlers.BroadcastSceneHello();

        /// <summary>F2 line: "World: in sync" or "World: N ids differ".</summary>
        public string WorldSyncStatus => SceneHandlers.WorldSyncStatus();

        public void SendSceneFollow(string sceneName, bool isRequest) =>
            SceneHandlers.SendSceneFollow(sceneName, isRequest);

        public void SendStorageBoxBlob(StorageBoxItem[] items) =>
            InventoryHandlers.SendStorageBoxBlob(items);

        public void SendPartyKeyRing(ushort[] enums) => InventoryHandlers.SendPartyKeyRing(enums);

        public void SendFullWorldSnapshot(int targetPlayerId = -1) =>
            SessionHandlers.SendFullWorldSnapshot(targetPlayerId);

        public void RequestWorldSnapshot() => SessionHandlers.RequestWorldSnapshot();
    }
}
