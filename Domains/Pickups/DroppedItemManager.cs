// Forward kept only for Puzzles/Codepad (CodepadSyncService). Everything else calls DroppedItemRegistry /
// DroppedItemSpawner directly; delete this class once that caller has moved.
using UnityEngine;

namespace SyncRADation.ItemSystem
{
    public static class DroppedItemManager
    {
        public static bool IsDroppedGo(GameObject go) => DroppedItemRegistry.IsDroppedGo(go);
    }
}
