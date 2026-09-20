using SyncRADation.Patches;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    /// <summary>
    /// UseItemInteraction / UseItemMulti read/apply + SnapUseItemWorld / PerPlayerUse.
    /// </summary>
    public sealed class UseItemWorldSyncService
    {
        public static bool TryRead(PuzzleType type, Component c, long wid, out PuzzleStateEntry entry)
        {
            entry = default;
            switch (type)
            {
                case PuzzleType.UseItemInteraction:
                {
                    var x = (UseItemInteraction)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.unlocked, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.UseItemMulti:
                {
                    entry = PuzzleDomainUtil.Mk(type, wid, UseItemMultiInteraction.blocked, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                default:
                    return false;
            }
        }

        public static void ApplyUseItem(UseItemInteraction x, PuzzleStateEntry e)
        {
            if (x == null) return;
            if (e.Bool0)
                SnapUseItemWorld(x);
            else if (!PerPlayerUse(x))
                x.unlocked = false;
        }

        public static void ApplyMulti(PuzzleStateEntry e)
        {
            UseItemMultiInteraction.blocked = e.Bool0;
        }

        internal static bool PerPlayerUse(UseItemInteraction x)
        {
            if (x == null) return false;
            try
            {
                if (LocalInspect.AirlockCinematic(x.gameObject)) return true;
            }
            catch { }
            try { return AirlockCinematic.IsPenTitlesCard(x); } catch { return false; }
        }

        public static void SnapUseItemWorld(UseItemInteraction x)
        {
            if (x == null) return;
            bool localUse = PerPlayerUse(x);
            if (!localUse)
            {
                try { x.unlocked = true; } catch { }
            }
            else
            {
                try { AirlockCinematic.NoteRemoteUnlock(x); } catch { }
            }
            try
            {
                if (x.inter != null)
                {
                    if (localUse)
                    {
                        x.inter.triggered = false;
                        x.inter.enabled = true;
                    }
                    else
                    {
                        x.inter.triggered = true;
                        x.inter.enabled = false;
                    }
                }
            }
            catch { }
            if (!PuzzleSyncService.MutateWorld)
            {
                PlaytestLog.Verbose("Puzzle", "snap UseItem flags only " + x.gameObject.name);
                return;
            }
            try
            {
                if (x.slaveInteraction != null)
                {
                    x.slaveInteraction.enabled = true;
                    x.slaveInteraction.triggered = false;
                }
            }
            catch { }
            try
            {
                var lockComp = x.GetComponent<InteractiveLock>();
                if (lockComp != null) lockComp.locked = false;
            }
            catch { }
            PuzzleDoorFlagsSyncService.TryUnlockDoors(x.gameObject);
            UnlockMatchingKeyLocks(x);
        }

        static bool SameKey(AnItem a, AnItem b)
        {
            if (a == null || b == null) return false;
            try { if (a == b) return true; } catch { }
            try { return a._item == b._item; } catch { return false; }
        }

        static void UnlockMatchingKeyLocks(UseItemInteraction x)
        {
            AnItem key = null;
            try { key = x.key; } catch { }
            if (key == null) return;
            GameObject root = x.gameObject;
            try
            {
                var room = PuzzleDomainUtil.FindInParents<Room>(x.gameObject);
                if (room != null) root = room.gameObject;
            }
            catch { }
            try
            {
                var singles = root.GetComponentsInChildren<InteractiveLockSingle>(true);
                if (singles != null)
                {
                    for (int i = 0; i < singles.Length; i++)
                    {
                        var s = singles[i];
                        if (s == null || !SameKey(s.key, key)) continue;
                        try
                        {
                            if (s.door != null && DoorNative.IsFlavorSeal(s.door.gameObject))
                                continue;
                        }
                        catch { }
                        try
                        {
                            if (s.door != null) s.door.locked = false;
                        }
                        catch { }
                        DoorNative.ApplyLockPlate(s, false);
                    }
                }
            }
            catch { }
            try
            {
                var locks = root.GetComponentsInChildren<InteractiveLock>(true);
                if (locks != null)
                {
                    for (int i = 0; i < locks.Length; i++)
                    {
                        var l = locks[i];
                        if (l == null) continue;
                        try
                        {
                            if (l.key == null || DoorNative.IsFlavorSeal(l.gameObject)) continue;
                            if (SameKey(l.key, key)) l.locked = false;
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }
    }
}
