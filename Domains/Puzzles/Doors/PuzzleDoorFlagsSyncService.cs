using SyncRADation.Sync;
using UnityEngine;
using static SyncRADation.Networking.PuzzleDomainUtil;

namespace SyncRADation.Networking
{
    /// <summary>
    /// PuzzleStatus / DoorwaySimple / SwingDoor / DoorLockControl / FoldingShutterDoor flag polls plus the
    /// TryUnlockDoors / UnlockDoorObject / RevealPickups helpers. Visual traverse sync stays on DoorSyncService.
    /// </summary>
    public sealed class PuzzleDoorFlagsSyncService
    {
        internal static PuzzleStateEntry ReadPuzzleStatus(PuzzleStatus x, long wid)
            => Mk(PuzzleType.PuzzleStatus, wid, x.solved, false, false, 0, 0, 0, 0, 0);

        internal static PuzzleStateEntry ReadDoorway(Doorway_simple x, long wid)
            => Mk(PuzzleType.DoorwaySimple, wid, x.locked, false, false, 0, 0, 0, 0, 0);

        internal static PuzzleStateEntry ReadSwing(SwingDoor x, long wid)
            => Mk(PuzzleType.SwingDoor, wid, x.Open, x.locked, false, 0, 0, 0, 0, 0);

        internal static PuzzleStateEntry ReadDoorLockControl(DoorLockControl x, long wid)
            => Mk(PuzzleType.DoorLockControl, wid, x.locked, false, false, 0, 0, 0, 0, 0);

        internal static PuzzleStateEntry ReadFoldingShutter(FoldingShutterDoor x, long wid)
            => Mk(PuzzleType.FoldingShutterDoor, wid, false, false, false, 0, 0, 0, 0, x.open);

        internal static void ApplyPuzzleStatus(PuzzleStatus x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.solved = e.Bool0;
            if (e.Bool0) TryUnlockDoors(x.gameObject);
        }

        /// <summary>Decompile Doorway_simple.locked: the dump must unseal as well as seal. Flavor seals stay locked.</summary>
        internal static void ApplyDoorway(Doorway_simple x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.locked = DoorNative.IsFlavorSeal(x.gameObject) || e.Bool0;
        }

        internal static void ApplySwing(SwingDoor x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.Open = e.Bool0;
            x.locked = e.Bool1;
        }

        /// <summary>Decompile DoorLockControl.locked + setLock: Bool0 true seals, false unseals (a flag snap, dump too).</summary>
        internal static void ApplyDoorLockControl(DoorLockControl x, PuzzleStateEntry e)
        {
            if (x == null) return;
            if (e.Bool0)
                DoorNative.ApplyDoorLockControl(x, true);
            else
                DoorNative.UnsealDoorLockControl(x);
        }

        internal static void ApplyFoldingShutter(FoldingShutterDoor x, PuzzleStateEntry e)
        {
            if (x != null) x.open = e.Float0;
        }

        /// <summary>Show the pickups a solve revealed (claimed ones stay hidden). Live / held re-snap only.</summary>
        internal static void RevealPickups(GameObject root)
        {
            if (root == null || !PuzzleSyncService.MutateWorld) return;
            var net = LanNetworkManager.Instance;
            var picks = root.GetComponentsInChildren<ItemPickup>(true);
            if (picks != null)
            {
                for (int i = 0; i < picks.Length; i++)
                {
                    var p = picks[i];
                    if (p == null) continue;
                    ulong pid = WorldId.FromGameObject(p.gameObject);
                    if (net != null && pid != 0 && (net.PickupSync.IsClaimed(pid) || net.PickupSync.IsClaimedPickup(p)))
                    {
                        net.PickupSync.HidePickup(p);
                        continue;
                    }
                    p.triggered = false;
                    p.gameObject.SetActive(true);
                    p.enabled = true;
                    var it = p.GetComponent<Interaction>();
                    if (it != null)
                    {
                        it.enabled = true;
                        it.triggered = false;
                    }
                }
            }
            if (net != null)
                net.PickupSync.NotifyRevealed();
        }

        /// <summary>Unlock flags on a door object (a flag snap, safe on the join dump). Flavor seals are skipped.</summary>
        internal static void UnlockDoorObject(GameObject door)
        {
            if (door == null) return;
            if (DoorNative.IsFlavorSeal(door))
            {
                PlaytestLog.Verbose("Puzzle", "skip unlock flavor seal " + door.name);
                return;
            }
            TryUnlockDoors(door);
            var dlc = door.GetComponent<DoorLockControl>();
            if (dlc != null)
                DoorNative.UnsealDoorLockControl(dlc);
            var simple = door.GetComponent<Doorway_simple>();
            if (simple != null) simple.locked = false;
            var dbl = door.GetComponent<Doorway_Double>();
            if (dbl != null) dbl.locked = false;
            var cd = door.GetComponent<ConnectedDoors>();
            if (cd != null && DoorNative.HasUnlocker(cd))
                DoorNative.ApplyConnectedDoors(cd, false);
        }

        /// <summary>Unlock the ConnectedDoors link this object sits under (flavor seals stay gated by HasUnlocker).</summary>
        internal static void TryUnlockDoors(GameObject go)
        {
            if (go == null) return;
            var cd = FindInParents<ConnectedDoors>(go);
            if (cd != null && cd.locked && DoorNative.HasUnlocker(cd))
                DoorNative.ApplyConnectedDoors(cd, false);
        }
    }
}
