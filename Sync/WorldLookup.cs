// Shared WorldId component lookup (include inactive — room chunks sleep).
using UnityEngine;

namespace SyncRADation.Sync
{
    public static class WorldLookup
    {
        public static T[] All<T>() where T : Object
        {
            try { return Object.FindObjectsOfType<T>(true); }
            catch
            {
                try { return Object.FindObjectsOfType<T>(); }
                catch { return null; }
            }
        }

        public static T Find<T>(ulong worldId, string missTag) where T : Component
        {
            var found = Find<T>(worldId);
            if (found == null && worldId != 0)
                PlaytestLog.Miss(missTag, typeof(T).Name, worldId);
            return found;
        }

        public static T Find<T>(ulong worldId) where T : Component
        {
            if (worldId == 0) return null;
            try
            {
                var all = All<T>();
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
