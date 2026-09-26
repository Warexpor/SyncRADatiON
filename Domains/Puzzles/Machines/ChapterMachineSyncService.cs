using SyncRADation.Patches;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    /// <summary>
    /// Chapter machine snap/read/apply batch (card writer, shutters, magpie, reactor, rings, etc.).
    /// </summary>
    public sealed class ChapterMachineSyncService
    {
        public static bool TryRead(PuzzleType type, Component c, long wid, out PuzzleStateEntry entry)
        {
            entry = default;
            switch (type)
            {
                case PuzzleType.MED_CardWriter:
                {
                    var x = (MED_CardWriter)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.solved, x.hasCard, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.RES_Shutters:
                {
                    var x = (RES_Shutters)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.unlocked, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.ROT_Magpie:
                {
                    var x = (ROT_Magpie)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.opened, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.PEN_Reaktor:
                {
                    var x = (PEN_Reaktor)c;
                    // Mid-fidelity: current / Dvalue / Dtemp / total fit existing Int slots.
                    entry = PuzzleDomainUtil.Mk(type, wid, x.solved, x.valid, false, x.current, x.Dvalue, x.Dtemp, x.total, 0);
                    return true;
                }
                case PuzzleType.LAB_Rings:
                {
                    var x = (LAB_Rings)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.solved, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.ROT_MeatBlocker:
                {
                    var x = (ROT_MeatBlocker)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, !x.blocked, false, false, x.pickups, x.required, 0, 0, 0);
                    return true;
                }
                case PuzzleType.RES_Power:
                {
                    var x = (RES_Power)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.solved, x.powered, false, PackBoolBits(x.states), 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.FlipSwitch:
                {
                    var x = (FlipSwitch)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.flipped, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.EvidenceLockerPuzzle:
                {
                    var x = (EvidenceLockerLogicPuzzle)c;
                    int lo, hi;
                    PackBoolBits64(x.states, out lo, out hi);
                    entry = PuzzleDomainUtil.Mk(type, wid, x.solved, false, false, lo, hi, 0, 0, 0);
                    return true;
                }
                case PuzzleType.MED_VentPuzzle:
                {
                    var x = (MED_VentPuzzle)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.uncovered, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.ROT_Tarot:
                {
                    var x = (ROT_Tarot)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.darkmode, false, false, 0, 0, 0, 0, x.FlipSwitchPos);
                    return true;
                }
                case PuzzleType.ROT_Mural:
                {
                    var x = (ROT_Mural)c;
                    // Pack up to 8 moons into Int0–Int3 (2 moons / int: Pos8|State4|Desired4 each).
                    int m0 = 0, m1 = 0, m2 = 0, m3 = 0;
                    try { PackMuralMoons(x.moons, out m0, out m1, out m2, out m3); } catch { }
                    entry = PuzzleDomainUtil.Mk(type, wid, x.finished, x.busy, false, m0, m1, m2, m3, x.MoonTurnSpeed);
                    return true;
                }
                case PuzzleType.MED_Incinerator:
                {
                    var x = (MED_Incinerator)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.solved, false, false, x.A, x.B, x.C, 0, 0);
                    return true;
                }
                case PuzzleType.LAB_Waage:
                {
                    var x = (LAB_Waage)c;
                    // Weight only — content stays personal bag (Apply ignores Int0).
                    entry = PuzzleDomainUtil.Mk(type, wid, false, false, false, 0, 0, 0, 0, x.weight);
                    return true;
                }
                case PuzzleType.RES_Shrine:
                {
                    var x = (RES_Shrine)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.solved, x.busy, false, x.big, x.mid, x.small, 0, 0);
                    return true;
                }
                default:
                    return false;
            }
        }

        public static void ApplyPower(RES_Power x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.solved = e.Bool0;
            x.powered = e.Bool1;
            UnpackBoolBits(x.states, e.Int0);
        }

        public static void ApplyVent(MED_VentPuzzle x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.uncovered = e.Bool0;
            if (!e.Bool0) return;
            try { if (x.cover != null) x.cover.SetActive(false); } catch { }
            try { if (x.smallCover != null) x.smallCover.SetActive(false); } catch { }
            try { if (x.smallCoverDiscarded != null) x.smallCoverDiscarded.SetActive(true); } catch { }
            try
            {
                if (x.itemInter != null)
                {
                    try
                    {
                        if (x.itemInter.inter != null)
                            PuzzleSyncService.DisableOne(x.itemInter.inter);
                    }
                    catch { }
                    try { x.itemInter.enabled = false; } catch { }
                    try { x.itemInter.gameObject.SetActive(false); } catch { }
                }
            }
            catch { }
            try { PuzzleSyncService.DisableInteractions(x); } catch { }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        public static void ApplyEvidenceLocker(EvidenceLockerLogicPuzzle x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.solved = e.Bool0;
            UnpackBoolBits64(x.states, e.Int0, e.Int1);
            if (!e.Bool0) return;
            SnapEvidenceLockerDoors(x.gameObject);
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        static void SnapEvidenceLockerDoors(GameObject root)
        {
            if (root == null) return;
            try
            {
                var doors = root.GetComponentsInChildren<EvidenceLockerDoor>(true);
                if (doors == null) return;
                for (int i = 0; i < doors.Length; i++)
                {
                    var d = doors[i];
                    if (d == null) continue;
                    try { d.done = true; } catch { }
                    try
                    {
                        if (d.Door != null)
                        {
                            d.Door.SetActive(true);
                            PuzzleSyncService.UnlockDoorObject(d.Door);
                        }
                    }
                    catch { }
                    try
                    {
                        if (d.inter != null)
                            PuzzleSyncService.DisableOne(d.inter);
                    }
                    catch { }
                    try { d.LoadState(); } catch { }
                }
            }
            catch { }
            // Sibling doors under the same room chunk.
            try
            {
                var parent = root.transform.parent;
                if (parent == null) return;
                var siblings = parent.GetComponentsInChildren<EvidenceLockerDoor>(true);
                if (siblings == null) return;
                for (int i = 0; i < siblings.Length; i++)
                {
                    var d = siblings[i];
                    if (d == null) continue;
                    try { d.done = true; } catch { }
                    try
                    {
                        if (d.Door != null)
                            PuzzleSyncService.UnlockDoorObject(d.Door);
                    }
                    catch { }
                    try
                    {
                        if (d.inter != null)
                            PuzzleSyncService.DisableOne(d.inter);
                    }
                    catch { }
                }
            }
            catch { }
        }

        public static void ApplyTarot(ROT_Tarot x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.darkmode = e.Bool0;
            x.FlipSwitchPos = e.Float0;
        }

        public static void ApplyCardWriter(MED_CardWriter x, PuzzleStateEntry e)
        {
            if (x != null)
                SnapCardWriter(x, e.Bool0, e.Bool1);
        }

        public static void ApplyShutters(RES_Shutters x, PuzzleStateEntry e)
        {
            if (x != null && e.Bool0)
                SnapShutters(x);
        }

        public static void ApplyMagpie(ROT_Magpie x, PuzzleStateEntry e)
        {
            if (x != null && e.Bool0)
                SnapMagpie(x);
        }

        public static void ApplyReaktor(PEN_Reaktor x, PuzzleStateEntry e)
        {
            if (x == null) return;
            try { x.valid = e.Bool1; } catch { }
            try { x.current = e.Int0; } catch { }
            try { x.Dvalue = e.Int1; } catch { }
            try { x.Dtemp = e.Int2; } catch { }
            try { x.total = e.Int3; } catch { }
            if (e.Bool0)
                SnapReaktor(x);
        }

        public static void ApplyLabRings(LAB_Rings x, PuzzleStateEntry e)
        {
            if (x != null && e.Bool0)
                SnapLabRings(x);
        }

        public static void ApplyMeatBlocker(ROT_MeatBlocker x, PuzzleStateEntry e)
        {
            if (x == null) return;
            // MeatBlocker ID "Death" seals the bookstore wing that holds NG+ KeyOfSacrifice.
            // Hold party seal apply while that key is still obtainable (picker is gated too).
            if (!e.Bool0 && IsDeathMeatBlocker(x) && SacrificeKeyStillAvailable())
            {
                try { x.pickups = e.Int0; } catch { }
                PlaytestLog.Event("Puzzle", "hold Death MeatBlocker seal until KeyOfSacrifice");
                return;
            }
            SnapMeatBlocker(x, e.Bool0, e.Int0);
        }

        static bool IsDeathMeatBlocker(ROT_MeatBlocker x)
        {
            if (x == null) return false;
            try
            {
                var id = x.ID;
                return id != null && string.Equals(id, "Death", System.StringComparison.Ordinal);
            }
            catch { return false; }
        }

        static bool SacrificeKeyStillAvailable()
        {
            try
            {
                var net = LanNetworkManager.Instance;
                if (net == null || !net.IsConnected) return false;
                return net.PickupSync.KeyOfSacrificeAvailableUnclaimed();
            }
            catch { return false; }
        }

        public static void SnapCardWriter(MED_CardWriter x, bool solved, bool hasCard)
        {
            if (x == null) return;
            try { x.solved = solved; } catch { }
            try { x.hasCard = hasCard || solved; } catch { }
            if (!solved) return;
            try { if (x.insertCard != null) x.insertCard.SetActive(false); } catch { }
            try { if (x.insertCardPrompt != null) x.insertCardPrompt.SetActive(false); } catch { }
            try { if (x.pickUpBlank != null) x.pickUpBlank.SetActive(true); } catch { }
            try { if (x.tinyCard != null) x.tinyCard.SetActive(true); } catch { }
            try { PuzzleSyncService.RevealPickups(x.pickUpBlank); } catch { }
            try { PuzzleSyncService.RevealPickups(x.gameObject); } catch { }
        }

        public static void SnapShutters(RES_Shutters x)
        {
            if (x == null) return;
            try { x.unlocked = true; } catch { }
            try { if (x.Shutter != null) x.Shutter.SetActive(false); } catch { }
            try { if (x.Handle != null) x.Handle.SetActive(false); } catch { }
            try
            {
                if (x._lock != null)
                    DoorNative.ApplyConnectedDoors(x._lock, false);
            }
            catch { }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        public static void SnapMagpie(ROT_Magpie x)
        {
            if (x == null) return;
            try { x.opened = true; } catch { }
            try { if (x.CardPickup != null) x.CardPickup.SetActive(true); } catch { }
            try { if (x.BoxObs != null) x.BoxObs.SetActive(false); } catch { }
            try { PuzzleSyncService.RevealPickups(x.CardPickup); } catch { }
            try { PuzzleSyncService.RevealPickups(x.gameObject); } catch { }
        }

        public static void SnapReaktor(PEN_Reaktor x)
        {
            if (x == null) return;
            try { x.solved = true; } catch { }
            try
            {
                if (x.doorLock != null)
                    DoorNative.ApplyConnectedDoors(x.doorLock, false);
            }
            catch { }
            try
            {
                var singles = x.GetComponentsInChildren<InteractiveLockSingle>(true);
                if (singles != null)
                {
                    for (int i = 0; i < singles.Length; i++)
                    {
                        if (singles[i] == null) continue;
                        try { if (singles[i].door != null) singles[i].door.locked = false; } catch { }
                        DoorNative.ApplyLockPlate(singles[i], false);
                    }
                }
            }
            catch { }
            try
            {
                if (x._event != null)
                {
                    PuzzleSyncService.DisableInteractions(x._event);
                    try { x._event.enabled = false; } catch { }
                }
            }
            catch { }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        public static void SnapLabRings(LAB_Rings x)
        {
            if (x == null) return;
            try { x.solved = true; } catch { }
            try { if (x.solvedState != null) x.solvedState.SetActive(true); } catch { }
            try { if (x.FakePlate != null) x.FakePlate.SetActive(false); } catch { }
            try { if (x.PlatePickup != null) x.PlatePickup.SetActive(true); } catch { }
            try { PuzzleSyncService.RevealPickups(x.PlatePickup); } catch { }
            try { PuzzleSyncService.RevealPickups(x.gameObject); } catch { }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        public static void SnapMeatBlocker(ROT_MeatBlocker x, bool unblocked, int pickups)
        {
            if (x == null) return;
            try { x.pickups = pickups; } catch { }
            try { x.blocked = !unblocked; } catch { }
            try
            {
                var blockers = x.Blockers;
                if (blockers != null)
                {
                    for (int i = 0; i < blockers.Length; i++)
                    {
                        try
                        {
                            if (blockers[i] != null)
                                blockers[i].SetActive(!unblocked);
                        }
                        catch { }
                    }
                }
            }
            catch { }
            try
            {
                var open = x.UnBlockers;
                if (open != null)
                {
                    for (int i = 0; i < open.Length; i++)
                    {
                        try
                        {
                            if (open[i] != null)
                                open[i].SetActive(unblocked);
                        }
                        catch { }
                    }
                }
            }
            catch { }
            try
            {
                var locks = x.Locks;
                if (locks != null)
                {
                    for (int i = 0; i < locks.Length; i++)
                    {
                        try
                        {
                            if (locks[i] != null)
                                DoorNative.ApplyConnectedDoors(locks[i], locked: !unblocked);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        public static void ApplyFlipSwitch(FlipSwitch x, PuzzleStateEntry e, bool mutateWorld)
        {
            if (x == null) return;
            if (x.flipped != e.Bool0)
            {
                if (mutateWorld)
                {
                    try { x.Flip(); }
                    catch { x.flipped = e.Bool0; }
                }
                else
                    x.flipped = e.Bool0;
            }
            if (e.Bool0) PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        public static void ApplyMural(ROT_Mural x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.finished = e.Bool0; x.busy = e.Bool1;
            try { x.MoonTurnSpeed = e.Float0; } catch { }
            try { UnpackMuralMoons(x.moons, e.Int0, e.Int1, e.Int2, e.Int3); } catch { }
            if (!e.Bool0) return;
            // useRing is presentation — skip on FullRefresh (_mutateWorld=false).
            if (PuzzleSyncService.MutateWorld)
            {
                try { x.useRing(); } catch { }
            }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        static int PackMoonPair(ROT_Mural.Moon a, ROT_Mural.Moon b)
        {
            return PackOneMoon(a) | (PackOneMoon(b) << 16);
        }

        static int PackOneMoon(ROT_Mural.Moon m)
        {
            if (m == null) return 0;
            int pos = 0, state = 0, desired = 0;
            try { pos = m.Pos & 0xFF; } catch { }
            try { state = m.State & 0xF; } catch { }
            try { desired = m.DesiredPos & 0xF; } catch { }
            return pos | (state << 8) | (desired << 12);
        }

        static void UnpackOneMoon(ROT_Mural.Moon m, int packed)
        {
            if (m == null) return;
            try { m.Pos = packed & 0xFF; } catch { }
            try { m.State = (packed >> 8) & 0xF; } catch { }
            try { m.DesiredPos = (packed >> 12) & 0xF; } catch { }
        }

        static void PackMuralMoons(UnhollowerBaseLib.Il2CppReferenceArray<ROT_Mural.Moon> moons,
            out int i0, out int i1, out int i2, out int i3)
        {
            i0 = i1 = i2 = i3 = 0;
            if (moons == null) return;
            ROT_Mural.Moon At(int i)
            {
                try { return i < moons.Length ? moons[i] : null; }
                catch { return null; }
            }
            i0 = PackMoonPair(At(0), At(1));
            i1 = PackMoonPair(At(2), At(3));
            i2 = PackMoonPair(At(4), At(5));
            i3 = PackMoonPair(At(6), At(7));
        }

        static void UnpackMuralMoons(UnhollowerBaseLib.Il2CppReferenceArray<ROT_Mural.Moon> moons,
            int i0, int i1, int i2, int i3)
        {
            if (moons == null) return;
            void Slot(int i, int packed)
            {
                try
                {
                    if (i < moons.Length && moons[i] != null)
                        UnpackOneMoon(moons[i], packed);
                }
                catch { }
            }
            Slot(0, i0 & 0xFFFF); Slot(1, (i0 >> 16) & 0xFFFF);
            Slot(2, i1 & 0xFFFF); Slot(3, (i1 >> 16) & 0xFFFF);
            Slot(4, i2 & 0xFFFF); Slot(5, (i2 >> 16) & 0xFFFF);
            Slot(6, i3 & 0xFFFF); Slot(7, (i3 >> 16) & 0xFFFF);
        }

        static int PackBoolBits(UnhollowerBaseLib.Il2CppStructArray<bool> arr)
        {
            return ResidencyPuzzleSyncService.PackBoolArray(arr);
        }

        static void UnpackBoolBits(UnhollowerBaseLib.Il2CppStructArray<bool> arr, int bits)
        {
            ResidencyPuzzleSyncService.UnpackBoolArray(arr, bits);
        }

        static void PackBoolBits64(UnhollowerBaseLib.Il2CppStructArray<bool> arr, out int lo, out int hi)
        {
            lo = 0;
            hi = 0;
            if (arr == null) return;
            try
            {
                int n = arr.Length;
                for (int i = 0; i < n && i < 32; i++)
                {
                    try { if (arr[i]) lo |= 1 << i; } catch { }
                }
                for (int i = 32; i < n && i < 64; i++)
                {
                    try { if (arr[i]) hi |= 1 << (i - 32); } catch { }
                }
            }
            catch { }
        }

        static void UnpackBoolBits64(UnhollowerBaseLib.Il2CppStructArray<bool> arr, int lo, int hi)
        {
            if (arr == null) return;
            try
            {
                int n = arr.Length;
                for (int i = 0; i < n && i < 32; i++)
                {
                    try { arr[i] = (lo & (1 << i)) != 0; } catch { }
                }
                for (int i = 32; i < n && i < 64; i++)
                {
                    try { arr[i] = (hi & (1 << (i - 32))) != 0; } catch { }
                }
            }
            catch { }
        }

        public static void ApplyIncinerator(MED_Incinerator x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.solved = e.Bool0; x.A = e.Int0; x.B = e.Int1; x.C = e.Int2;
            if (!e.Bool0) return;
            try { x.StartShutdown(); } catch { }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        public static void ApplyWaage(LAB_Waage x, PuzzleStateEntry e)
        {
            // Decompile LAB_Waage.weight/content — sync weight only.
            // Writing InventoryManager.getItem onto the peer stomps independent bags (AGENTS).
            if (x == null) return;
            x.weight = e.Float0;
        }

        public static void ApplyShrine(RES_Shrine x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.solved = e.Bool0; x.busy = e.Bool1;
            x.big = e.Int0; x.mid = e.Int1; x.small = e.Int2;
            if (!e.Bool0) return;
            try { x.CheckSolve(); } catch { }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        // Radio peel — façade for any leftover callers.
        public static void ApplyRadioAlignment(ROT_RadioAlignment x, PuzzleStateEntry e)
            => RadioPuzzleSyncService.ApplyAlignment(x, e);

        public static void ApplyRadioCode(DET_RadioCodeLock x, PuzzleStateEntry e)
            => RadioPuzzleSyncService.ApplyCode(x, e);
    }
}
