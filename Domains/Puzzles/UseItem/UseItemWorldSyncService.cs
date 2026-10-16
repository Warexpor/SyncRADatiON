using SyncRADation.Patches;
using SyncRADation.Sync;
using UnityEngine;
using static SyncRADation.Networking.PuzzleDomainUtil;

namespace SyncRADation.Networking
{
    /// <summary>UseItemInteraction / UseItemMulti read/apply + SnapUseItemWorld / PerPlayerUse.</summary>
    public sealed class UseItemWorldSyncService
    {
        internal static PuzzleStateEntry ReadUseItem(UseItemInteraction x, long wid)
            => Mk(PuzzleType.UseItemInteraction, wid, x.unlocked, false, false, 0, 0, 0, 0, 0);

        /// <summary>UseItemMultiInteraction.blocked is static (read through the first instance).</summary>
        internal static PuzzleStateEntry ReadMulti(UseItemMultiInteraction x, long wid)
            => Mk(PuzzleType.UseItemMulti, wid, UseItemMultiInteraction.blocked, false, false, 0, 0, 0, 0, 0);

        internal static void ApplyUseItem(UseItemInteraction x, PuzzleStateEntry e)
        {
            if (x == null) return;
            if (e.Bool0)
                SnapUseItemWorld(x);
            else if (!PerPlayerUse(x))
                x.unlocked = false;
        }

        internal static void ApplyMulti(UseItemMultiInteraction x, PuzzleStateEntry e)
        {
            UseItemMultiInteraction.blocked = e.Bool0;
        }

        /// <summary>The airlock key card (PEN_Titles) is used by each player for themselves.</summary>
        internal static bool PerPlayerUse(UseItemInteraction x)
        {
            if (x == null) return false;
            return LocalInspect.AirlockCinematic(x.gameObject) || AirlockCinematic.IsPenTitlesCard(x);
        }

        /// <summary>
        /// The interaction snaps on every apply (dump included) so joiners cannot re-use a party-wide UseItem. A
        /// party-wide use latches unlocked (an unlatched flag would be polled back to the host as a relock) and its
        /// durable consequence (door unlock, follow-up interaction: flag / enable state, flavor seals filtered) runs on
        /// every apply. A per-player use only touches the world on a live apply.
        /// </summary>
        internal static void SnapUseItemWorld(UseItemInteraction x)
        {
            if (x == null) return;
            bool wasUnlocked = x.unlocked;
            bool localUse = PerPlayerUse(x);
            if (x.inter != null)
            {
                x.inter.triggered = !localUse;
                x.inter.enabled = localUse;
            }
            if (localUse && !PuzzleSyncService.MutateWorld)
            {
                PlaytestLog.Verbose("Puzzle", "snap UseItem inter only (no unlock latch) " + x.gameObject.name);
                return;
            }
            if (!localUse)
                x.unlocked = true;
            if (x.slaveInteraction != null)
            {
                x.slaveInteraction.enabled = true;
                x.slaveInteraction.triggered = false;
            }
            var lockComp = x.GetComponent<InteractiveLock>();
            if (lockComp != null && lockComp.key != null) lockComp.locked = false;
            PuzzleDoorFlagsSyncService.TryUnlockDoors(x.gameObject);
            UnlockMatchingKeyLocks(x);
            // onSuccessful (scene data: InsertDisk*, PlaceCard*, UnlockKey, TurnValve, PowerUp, END_Graves.Unlock,
            // painting Dissolve, SetActive …) runs once on the false → true edge of a live party-wide use, for every
            // peer whatever room it is in: several targets are world state with no puzzle entry of their own. Not
            // PuzzleEdge.Solved: this snap is also the host's InteractionSync relay of a client use, which is live but
            // not inside a PuzzleState apply (LiveEdge false), so the gate is ReplayWorld. A dump / held re-snap never
            // replays it (the puzzle consequences ride their own entries).
            if (!wasUnlocked && !localUse && PuzzleSyncService.ReplayWorld)
                Native("useitem-success", () => PuzzleEdge.Invoke(x.onSuccessful));
        }

        static bool SameKey(AnItem a, AnItem b)
            => a != null && b != null && (a == b || a._item == b._item);

        /// <summary>Locks in the same room keyed to this item open with it.</summary>
        static void UnlockMatchingKeyLocks(UseItemInteraction x)
        {
            AnItem key = x.key;
            if (key == null) return;
            var room = FindInParents<Room>(x.gameObject);
            GameObject root = room != null ? room.gameObject : x.gameObject;
            var singles = root.GetComponentsInChildren<InteractiveLockSingle>(true);
            if (singles != null)
            {
                for (int i = 0; i < singles.Length; i++)
                {
                    var s = singles[i];
                    if (s == null || !SameKey(s.key, key)) continue;
                    if (s.door != null && DoorNative.IsFlavorSeal(s.door.gameObject)) continue;
                    if (s.door != null) s.door.locked = false;
                    DoorNative.ApplyLockPlate(s, false);
                }
            }
            var locks = root.GetComponentsInChildren<InteractiveLock>(true);
            if (locks == null) return;
            for (int i = 0; i < locks.Length; i++)
            {
                var l = locks[i];
                if (l == null || l.key == null || DoorNative.IsFlavorSeal(l.gameObject)) continue;
                if (SameKey(l.key, key)) l.locked = false;
            }
        }
    }
}
