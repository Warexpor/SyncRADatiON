using SyncRADation.Patches;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    /// <summary>
    /// InteractiveLock*, Keypad3D/ROT_Keypad, Dial, Number, Multi, DoorLock* read/apply.
    /// </summary>
    public sealed class LockSyncService
    {
        public static bool TryRead(PuzzleType type, Component c, long wid, out PuzzleStateEntry entry)
        {
            entry = default;
            switch (type)
            {
                case PuzzleType.InteractiveLock:
                {
                    var x = (InteractiveLock)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.locked, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.InteractiveLockSingle:
                {
                    var x = (InteractiveLockSingle)c;
                    bool locked = x.door != null && x.door.locked;
                    bool plate = DoorNative.TraversePlateActive(x);
                    try
                    {
                        if (!plate && x.key == null && (x.door == null || !x.door.open))
                            plate = DoorNative.IsFlavorSeal(x.gameObject)
                                || (x.door != null && DoorNative.IsFlavorSeal(x.door.gameObject));
                    }
                    catch { }
                    entry = PuzzleDomainUtil.Mk(type, wid, locked, plate, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.Keypad3D:
                {
                    var x = (Keypad3D)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.solved || x.opening, x.opening, x.blocked, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.ROT_Keypad:
                {
                    var x = (ROT_Keypad)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.solved || x.opening, x.opening, x.blocked, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.DialLock:
                {
                    var x = (ROT_DialLock)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.solved, false, false, x.A, x.B, x.C, x.D, 0);
                    return true;
                }
                case PuzzleType.NumberLockNew:
                {
                    var x = (NumberLockNew)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.locked, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.DoorLockPuzzle:
                {
                    var x = (DoorLockPuzzle)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.locked, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.MultiLock:
                {
                    // Decompile MED/LAB_MultiLock element bools → Int0 bits (protocol 8 reuses Int0).
                    var med = c as MED_MultiLock;
                    if (med != null)
                    {
                        entry = PuzzleDomainUtil.Mk(type, wid, med.unlocked, false, false, PackMedBits(med), 0, 0, 0, 0);
                        return true;
                    }
                    var lab = c as LAB_MultiLock;
                    if (lab != null)
                    {
                        entry = PuzzleDomainUtil.Mk(type, wid, lab.unlocked, false, false, PackLabBits(lab), 0, 0, 0, 0);
                        return true;
                    }
                    return false;
                }
                case PuzzleType.DoorLockControl:
                    return PuzzleDoorFlagsSyncService.TryRead(type, c, wid, out entry);
                case PuzzleType.DoorLockEventInteraction:
                {
                    var x = (DoorLockEventInteraction)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.done, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.BiodomeDoorLock:
                {
                    var x = (BiodomeDoorLock)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, !x.hasLock, false, false, x.KeyLevel, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.DET_ServiceLock:
                {
                    var x = (DET_ServiceLock)c;
                    bool ok = x.solved != null && x.solved.solved;
                    entry = PuzzleDomainUtil.Mk(type, wid, ok, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                default:
                    return false;
            }
        }

        public static void ApplyInteractive(InteractiveLock x, PuzzleStateEntry e, bool mutateWorld)
        {
            if (x == null) return;
            bool was = x.locked;
            x.locked = e.Bool0;
            if (was && !e.Bool0)
            {
                if (mutateWorld)
                {
                    try { x.delayedOpen(); } catch { }
                }
                PuzzleSyncService.TryUnlockDoors(x.gameObject);
            }
        }

        public static void ApplyInteractiveSingle(InteractiveLockSingle x, PuzzleStateEntry e, bool mutateWorld)
        {
            // Flags + plate always snap (incl. FullRefresh); unlock side-effects stay mutate-gated.
            if (x == null) return;
            try
            {
                if (DoorNative.IsFlavorSeal(x.gameObject)
                    || (x.door != null && DoorNative.IsFlavorSeal(x.door.gameObject)))
                {
                    if (x.door != null) x.door.locked = true;
                    return;
                }
            }
            catch { }
            if (e.Bool0)
            {
                if (x.door != null) x.door.locked = true;
                if (e.Bool1)
                    DoorNative.ApplyLockPlate(x, true);
            }
            else
            {
                if (x.door != null)
                {
                    try { x.door.locked = false; } catch { }
                    PuzzleSyncService.UnlockDoorObject(x.door.gameObject);
                }
                PuzzleSyncService.TryUnlockDoors(x.gameObject);
                DoorNative.ApplyLockPlate(x, false);
            }
        }

        public static void ApplyKeypad3D(Keypad3D x, PuzzleStateEntry e, bool mutateWorld)
        {
            if (x == null) return;
            bool was = x.solved;
            x.solved = e.Bool0; x.opening = e.Bool1; x.blocked = e.Bool2;
            if (!e.Bool0) return;
            if (mutateWorld && !was) { try { x.openDoor(); } catch { } }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        public static void ApplyRotKeypad(ROT_Keypad x, PuzzleStateEntry e, bool mutateWorld)
        {
            if (x == null) return;
            bool was = x.solved;
            x.solved = e.Bool0; x.opening = e.Bool1; x.blocked = e.Bool2;
            if (!e.Bool0) return;
            // Mirror Keypad3D openDoor rising-edge + host ApplyKeypad: onSuccess peels
            // to ConnectedDoors.Unlock on Door Connection (35) under DoorConnections
            // (separate tree from KeypadLogic) + Event.SetActive + exitEvent + dimPOI.
            // TryUnlockDoors = FindInParents ConnectedDoors only — misses that peel.
            if (mutateWorld && !was)
            {
                NetGate.BeginApply();
                try
                {
                    if (x.onSuccess != null)
                        x.onSuccess.Invoke();
                }
                catch { }
                finally { NetGate.EndApply(); }
            }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        public static void ApplyDial(ROT_DialLock x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.solved = e.Bool0;
            x.A = e.Int0; x.B = e.Int1; x.C = e.Int2; x.D = e.Int3;
            if (x.a != null) x.a.localEulerAngles = new Vector3(0, e.Int0 * 36, 0);
            if (x.b != null) x.b.localEulerAngles = new Vector3(0, e.Int1 * 36, 0);
            if (x.c != null) x.c.localEulerAngles = new Vector3(0, e.Int2 * 36, 0);
            if (x.d != null) x.d.localEulerAngles = new Vector3(0, e.Int3 * 36, 0);
            if (!e.Bool0) return;
            try
            {
                if (x.lockObject != null)
                    x.lockObject.SetActive(false);
            }
            catch { }
            try
            {
                if (x.Door != null)
                    PuzzleSyncService.UnlockDoorObject(x.Door.gameObject);
            }
            catch { }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
            try { PuzzleSyncService.DisableInteractions(x); } catch { }
        }

        public static void ApplyNumber(NumberLockNew x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.locked = e.Bool0;
            if (e.Bool0) return;
            try
            {
                if (x.door != null)
                {
                    x.door.locked = false;
                    PuzzleSyncService.UnlockDoorObject(x.door.gameObject);
                }
            }
            catch { }
            try { if (x.doorInt != null) PuzzleSyncService.DisableOne(x.doorInt); } catch { }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        public static void ApplyDoorLockPuzzle(DoorLockPuzzle x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.locked = e.Bool0;
            if (e.Bool0) return;
            try
            {
                if (x.door != null)
                {
                    x.door.locked = false;
                    PuzzleSyncService.UnlockDoorObject(x.door.gameObject);
                }
            }
            catch { }
            try { if (x.doorInt != null) PuzzleSyncService.DisableOne(x.doorInt); } catch { }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        public static void ApplyMulti(Component c, PuzzleStateEntry e)
        {
            if (c == null) return;
            var med = c as MED_MultiLock;
            if (med != null)
            {
                med.unlocked = e.Bool0;
                UnpackMedBits(med, e.Int0);
                if (e.Bool0) PuzzleSyncService.TryUnlockDoors(med.gameObject);
                return;
            }
            var lab = c as LAB_MultiLock;
            if (lab != null)
            {
                lab.unlocked = e.Bool0;
                UnpackLabBits(lab, e.Int0);
                if (e.Bool0) PuzzleSyncService.TryUnlockDoors(lab.gameObject);
            }
        }

        // Decompile MED/LAB_MultiLock: Fire=1 Earth=2 Water=4 Air=8 Gold=16 Star=32 (LAB).
        static int PackMedBits(MED_MultiLock x)
        {
            int bits = 0;
            if (x == null) return bits;
            try { if (x.Fire) bits |= 1; } catch { }
            try { if (x.Earth) bits |= 2; } catch { }
            try { if (x.Water) bits |= 4; } catch { }
            try { if (x.Air) bits |= 8; } catch { }
            try { if (x.Gold) bits |= 16; } catch { }
            return bits;
        }

        static int PackLabBits(LAB_MultiLock x)
        {
            int bits = 0;
            if (x == null) return bits;
            try { if (x.Fire) bits |= 1; } catch { }
            try { if (x.Earth) bits |= 2; } catch { }
            try { if (x.Water) bits |= 4; } catch { }
            try { if (x.Air) bits |= 8; } catch { }
            try { if (x.Gold) bits |= 16; } catch { }
            try { if (x.Star) bits |= 32; } catch { }
            return bits;
        }

        static void UnpackMedBits(MED_MultiLock x, int bits)
        {
            if (x == null) return;
            try { x.Fire = (bits & 1) != 0; } catch { }
            try { x.Earth = (bits & 2) != 0; } catch { }
            try { x.Water = (bits & 4) != 0; } catch { }
            try { x.Air = (bits & 8) != 0; } catch { }
            try { x.Gold = (bits & 16) != 0; } catch { }
        }

        static void UnpackLabBits(LAB_MultiLock x, int bits)
        {
            if (x == null) return;
            try { x.Fire = (bits & 1) != 0; } catch { }
            try { x.Earth = (bits & 2) != 0; } catch { }
            try { x.Water = (bits & 4) != 0; } catch { }
            try { x.Air = (bits & 8) != 0; } catch { }
            try { x.Gold = (bits & 16) != 0; } catch { }
            try { x.Star = (bits & 32) != 0; } catch { }
        }

        public static void ApplyDoorLockControl(DoorLockControl x, PuzzleStateEntry e, bool mutateWorld)
            => PuzzleDoorFlagsSyncService.ApplyDoorLockControl(x, e, mutateWorld);

        public static void ApplyDoorLockEvent(DoorLockEventInteraction x, PuzzleStateEntry e)
        {
            if (x != null) x.done = e.Bool0;
        }

        public static void ApplyBiodome(BiodomeDoorLock x, PuzzleStateEntry e)
        {
            if (x != null)
                SnapBiodomeLock(x, e.Bool0, e.Int0);
        }

        public static void ApplyServiceLock(DET_ServiceLock x, PuzzleStateEntry e)
        {
            if (x != null && e.Bool0)
                SnapServiceLock(x);
        }

        public static void SnapBiodomeLock(BiodomeDoorLock x, bool unlocked, int keyLevel)
        {
            if (x == null) return;
            try { x.KeyLevel = keyLevel; } catch { }
            try { x.hasLock = !unlocked; } catch { }
            try { x.setSprites(); } catch { }
            if (!unlocked) return;
            try
            {
                if (x.door != null)
                    x.door.locked = false;
            }
            catch { }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        public static void SnapServiceLock(DET_ServiceLock x)
        {
            if (x == null) return;
            try
            {
                if (x.solved != null)
                    x.solved.solved = true;
            }
            catch { }
            PuzzleSyncService.DisableInteractions(x);
            try
            {
                var buttons = x.Buttons;
                if (buttons != null)
                {
                    for (int i = 0; i < buttons.Length; i++)
                        PuzzleSyncService.DisableOne(buttons[i]);
                }
            }
            catch { }
            try
            {
                var counter = x.CounterButtons;
                if (counter != null)
                {
                    for (int i = 0; i < counter.Length; i++)
                        PuzzleSyncService.DisableOne(counter[i]);
                }
            }
            catch { }
            try { PuzzleSyncService.DisableOne(x.TestButton); } catch { }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }
    }
}
