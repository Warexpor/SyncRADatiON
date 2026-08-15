// Shared WorldId component lookup (include inactive — room chunks sleep).
using UnityEngine;

namespace SyncRADation.Sync
{
    public static class WorldLookup
    {
        public static T Find<T>(ulong worldId) where T : Component
        {
            if (worldId == 0) return null;
            try
            {
                T[] all = null;
                try { all = Object.FindObjectsOfType<T>(true); }
                catch { all = Object.FindObjectsOfType<T>(); }
                if (all == null) return null;
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] == null) continue;
                    if (WorldId.FromGameObject(all[i].gameObject) == worldId)
                        return all[i];
                }
            }
            catch { }
            return null;
        }
    }
}
