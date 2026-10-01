// Forward kept only for call sites outside Pickups/Inventory (ModRuntime, SessionResetRegistrations, LanNetworkManager,
// Combat/MenuHit, Players/NetworkDamageSystem, Puzzles/Codepad, Scene/SceneFollowService, Session/HostReload).
// Pickups/Inventory code calls DroppedItemRegistry / DroppedItemSpawner directly; new code should too.
using UnityEngine;

namespace SyncRADation.ItemSystem
{
    public static class DroppedItemManager
    {
        public static bool IsDroppedGo(GameObject go) => DroppedItemRegistry.IsDroppedGo(go);
        public static Vector3 FloorDropPos(Transform player) => DroppedItemRegistry.FloorDropPos(player);
        public static GameObject SpawnLocalItem(Items.itemlist item, int count, int netID, Vector3 pos)
            => DroppedItemSpawner.SpawnLocalItem(item, count, netID, pos);
        public static void RestorePlay() => DroppedItemRegistry.RestorePlay();
        public static void RestorePlayForLoad() => DroppedItemRegistry.RestorePlayForLoad();
        public static void TickDeferred() => DroppedItemRegistry.TickDeferred();
        public static void ClearVisuals() => DroppedItemRegistry.ClearVisuals();
        public static void RespawnCurrentScene() => DroppedItemRegistry.RespawnCurrentScene();
        public static void ClearAll() => DroppedItemRegistry.ClearAll();
    }
}
