using SyncRADation.Patches;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    /// <summary>
    /// PuzzleStatus / DoorwaySimple / SwingDoor / DoorLockControl / FoldingShutterDoor
    /// flag polls plus TryUnlockDoors / UnlockDoorObject / RevealPickups helpers.
    /// Visual traverse sync stays on DoorSyncService.
    /// </summary>
    public sealed class PuzzleDoorFlagsSyncService
    {
        public static bool TryRead(PuzzleType type, Component c, long wid, out PuzzleStateEntry entry)
        {
            entry = default;
            switch (type)
            {
                case PuzzleType.PuzzleStatus:
                {
                    var x = (PuzzleStatus)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.solved, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.DoorwaySimple:
                {
                    var x = (Doorway_simple)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.locked, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.SwingDoor:
                {
                    var x = (SwingDoor)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.Open, x.locked, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.DoorLockControl:
                {
                    var x = (DoorLockControl)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.locked, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.FoldingShutterDoor:
                {
                    var x = (FoldingShutterDoor)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, false, false, false, 0, 0, 0, 0, x.open);
                    return true;
                }
                default:
                    return false;
            }
        }

        public static void ApplyPuzzleStatus(PuzzleStatus x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.solved = e.Bool0;
            if (e.Bool0) TryUnlockDoors(x.gameObject);
        }

        public static void ApplyDoorway(Doorway_simple x, PuzzleStateEntry e, bool mutateWorld)
        {
            // Decompile Doorway_simple.locked — join/live must unseal as well as seal.
            // Flavor seals stay locked (DoorNative.IsFlavorSeal).
            // Flags always snap (incl. FullRefresh); mutateWorld reserved for side-effects only.
            if (x == null) return;
            if (DoorNative.IsFlavorSeal(x.gameObject))
            {
                x.locked = true;
                return;
            }
            x.locked = e.Bool0;
        }

        public static void ApplySwing(SwingDoor x, PuzzleStateEntry e, bool mutateWorld)
        {
            if (x == null) return;
            x.Open = e.Bool0;
            x.locked = e.Bool1;
        }

        public static void ApplyDoorLockControl(DoorLockControl x, PuzzleStateEntry e, bool mutateWorld)
        {
            // Decompile DoorLockControl.locked + setLock — Bool0 true seals, false unseals.
            // setLock is the flag snap (needed on join FullRefresh too).
            if (x == null) return;
            if (e.Bool0)
                DoorNative.ApplyDoorLockControl(x, true);
            else
                DoorNative.UnsealDoorLockControl(x);
        }

        public static void ApplyFoldingShutter(FoldingShutterDoor x, PuzzleStateEntry e)
        {
            if (x != null) x.open = e.Float0;
        }

        internal static void RevealPickups(GameObject root)
        {
            if (root == null || !PuzzleSyncService.MutateWorld) return;
            try
            {
                var picks = root.GetComponentsInChildren<ItemPickup>(true);
                if (picks != null)
                {
                    for (int i = 0; i < picks.Length; i++)
                    {
                        var p = picks[i];
                        if (p == null) continue;
                        ulong pid = 0;
                        try { pid = WorldId.FromGameObject(p.gameObject); } catch (System.Exception e) { Guard.Swallow(e); }
                        try
                        {
                            var netClaim = LanNetworkManager.Instance;
                            if (netClaim != null && pid != 0 && (netClaim.PickupSync.IsClaimed(pid) || netClaim.PickupSync.IsClaimedPickup(p)))
                            {
                                netClaim.PickupSync.HidePickup(p);
                                continue;
                            }
                        }
                        catch (System.Exception e) { Guard.Swallow(e); }
                        try { p.triggered = false; } catch (System.Exception e) { Guard.Swallow(e); }
                        try { p.gameObject.SetActive(true); } catch (System.Exception e) { Guard.Swallow(e); }
                        try { p.enabled = true; } catch (System.Exception e) { Guard.Swallow(e); }
                        try
                        {
                            var it = p.GetComponent<Interaction>();
                            if (it != null)
                            {
                                it.enabled = true;
                                it.triggered = false;
                            }
                        }
                        catch (System.Exception e) { Guard.Swallow(e); }
                    }
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                var net = LanNetworkManager.Instance;
                if (net != null)
                    net.PickupSync.NotifyRevealed();
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        internal static void UnlockDoorObject(GameObject door)
        {
            // Flag snap — safe on join FullRefresh. Flavor seals skipped.
            if (door == null) return;
            if (DoorNative.IsFlavorSeal(door))
            {
                PlaytestLog.Verbose("Puzzle", "skip unlock flavor seal " + door.name);
                return;
            }
            TryUnlockDoors(door);
            try
            {
                var dlc = door.GetComponent<DoorLockControl>();
                if (dlc != null)
                    DoorNative.UnsealDoorLockControl(dlc);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                var simple = door.GetComponent<Doorway_simple>();
                if (simple != null) simple.locked = false;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                var dbl = door.GetComponent<Doorway_Double>();
                if (dbl != null) dbl.locked = false;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                var cd = door.GetComponent<ConnectedDoors>();
                if (cd != null && DoorNative.AllowUnlock(cd))
                    DoorNative.ApplyConnectedDoors(cd, false);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        internal static void TryUnlockDoors(GameObject go)
        {
            // Flag snap — safe on join FullRefresh. Flavor seals stay gated by AllowUnlock.
            if (go == null) return;
            try
            {
                var cd = PuzzleDomainUtil.FindInParents<ConnectedDoors>(go);
                if (cd != null && cd.locked && DoorNative.AllowUnlock(cd))
                    DoorNative.ApplyConnectedDoors(cd, false);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }
    }
}
