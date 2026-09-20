using UnhollowerBaseLib;
using UnityEngine;

namespace SyncRADation.Networking
{
    /// <summary>
    /// Protocol-9 residency / key / drawer puzzles (RES_MusicBox..OpenableDrawer).
    /// Magpie-style final-pose snaps; MED_KeyGrid / ArianePhotoCode are static globals.
    /// </summary>
    public sealed class ResidencyPuzzleSyncService
    {
        public static bool TryRead(PuzzleType type, Component c, long wid, out PuzzleStateEntry entry)
        {
            entry = default;
            switch (type)
            {
                case PuzzleType.RES_MusicBox:
                {
                    var x = (RES_MusicBox)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.opened, x.hasCassette, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.RES_LibraryPC:
                {
                    var x = (RES_LibraryPC)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.solved, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.RES_Paternoster:
                {
                    var x = (RES_Paternoster)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.powered, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.DET_ServiceLock_Key:
                {
                    var x = (DET_ServiceLock_Key)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.Open, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.SafeDoorSmall:
                {
                    var x = (SafeDoorSmall)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.open, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.MultiKeyLock:
                {
                    var x = (MultiKeyLock)c;
                    // Derive unlocked from keys[] — never call checkLock() on the poll path
                    // (Apply calls it when unlocking).
                    bool unlocked = false;
                    try
                    {
                        var keys = x.keys;
                        if (keys != null && keys.Length > 0)
                        {
                            unlocked = true;
                            for (int i = 0; i < keys.Length; i++)
                            {
                                if (!keys[i]) { unlocked = false; break; }
                            }
                        }
                    }
                    catch { }
                    entry = PuzzleDomainUtil.Mk(type, wid, unlocked, false, false, PackBoolArray(x.keys), 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.OpenableDrawer:
                {
                    var x = (OpenableDrawer)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.open, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.MED_KeyGrid:
                {
                    // Instance registered for scan; static solved is the authority.
                    entry = PuzzleDomainUtil.Mk(type, wid, MED_KeyGrid.solved, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.ArianePhotoCode:
                {
                    entry = PuzzleDomainUtil.Mk(type, wid, ArianePhotoCode.code != 0, false, false, ArianePhotoCode.code, 0, 0, 0, 0);
                    return true;
                }
                default:
                    return false;
            }
        }

        /// <summary>Host-only global (WorldId 0) — mirrors RadioManagerState.</summary>
        public static PuzzleStateEntry ReadKeyGridGlobal()
        {
            bool solved = false;
            try { solved = MED_KeyGrid.solved; } catch { }
            return PuzzleDomainUtil.Mk(PuzzleType.MED_KeyGrid, 0, solved, false, false, 0, 0, 0, 0, 0f);
        }

        /// <summary>Host-only global (WorldId 0) — static ArianePhotoCode.code.</summary>
        public static PuzzleStateEntry ReadArianePhotoCodeGlobal()
        {
            int code = 0;
            try { code = ArianePhotoCode.code; } catch { }
            return PuzzleDomainUtil.Mk(PuzzleType.ArianePhotoCode, 0, code != 0, false, false, code, 0, 0, 0, 0f);
        }

        public static void ApplyMusicBox(RES_MusicBox x, PuzzleStateEntry e)
        {
            if (x == null) return;
            try { x.hasCassette = e.Bool1; } catch { }
            if (e.Bool0)
                SnapMusicBox(x);
        }

        public static void ApplyLibraryPc(RES_LibraryPC x, PuzzleStateEntry e)
        {
            if (x != null && e.Bool0)
                SnapLibraryPc(x);
        }

        public static void ApplyPaternoster(RES_Paternoster x, PuzzleStateEntry e)
        {
            if (x == null) return;
            try { x.setPower(e.Bool0); }
            catch
            {
                try { x.powered = e.Bool0; } catch { }
            }
        }

        public static void ApplyKeyGrid(PuzzleStateEntry e)
        {
            try { MED_KeyGrid.solved = e.Bool0; } catch { }
        }

        public static void ApplyArianePhotoCode(PuzzleStateEntry e)
        {
            try { ArianePhotoCode.code = e.Int0; } catch { }
            try { ArianePhotoCode.LoadState(); } catch { }
            // Reassert after LoadState in case it reloads from local save.
            try { ArianePhotoCode.code = e.Int0; } catch { }
        }

        public static void ApplyServiceLockKey(DET_ServiceLock_Key x, PuzzleStateEntry e)
        {
            if (x != null && e.Bool0)
                SnapServiceLockKey(x);
        }

        public static void ApplySafeDoorSmall(SafeDoorSmall x, PuzzleStateEntry e)
        {
            if (x != null && e.Bool0)
                SnapSafeDoorSmall(x);
        }

        public static void ApplyMultiKeyLock(MultiKeyLock x, PuzzleStateEntry e)
        {
            if (x == null) return;
            UnpackBoolArray(x.keys, e.Int0);
            if (!e.Bool0) return;
            try { x.checkLock(); } catch { }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
            try { PuzzleSyncService.DisableInteractions(x); } catch { }
        }

        public static void ApplyOpenableDrawer(OpenableDrawer x, PuzzleStateEntry e)
        {
            if (x == null) return;
            if (x.open == e.Bool0) return;
            try { x.open = e.Bool0; } catch { }
            PoseDrawer(x, e.Bool0);
        }

        public static void SnapMusicBox(RES_MusicBox x)
        {
            if (x == null) return;
            try { x.opened = true; } catch { }
            try { x.hasCassette = true; } catch { }
            try { if (x.CardPickup != null) x.CardPickup.SetActive(true); } catch { }
            try { if (x.BoxObs != null) x.BoxObs.SetActive(false); } catch { }
            try
            {
                if (x.euleLid != null)
                {
                    var e = x.euleLid.localEulerAngles;
                    e.z = x.lidOpen;
                    x.euleLid.localEulerAngles = e;
                }
            }
            catch { }
            try { PuzzleSyncService.RevealPickups(x.CardPickup); } catch { }
            try { PuzzleSyncService.RevealPickups(x.gameObject); } catch { }
            try { PuzzleSyncService.DisableInteractions(x); } catch { }
            try { if (x.tapeInteraction != null) x.tapeInteraction.SetActive(false); } catch { }
        }

        public static void SnapLibraryPc(RES_LibraryPC x)
        {
            if (x == null) return;
            try { x.solved = true; } catch { }
            try { if (x.Pickup != null) x.Pickup.SetActive(true); } catch { }
            try { if (x.Tome != null) x.Tome.gameObject.SetActive(true); } catch { }
            try { PuzzleSyncService.RevealPickups(x.Pickup); } catch { }
            try { PuzzleSyncService.RevealPickups(x.gameObject); } catch { }
            try { PuzzleSyncService.DisableInteractions(x); } catch { }
        }

        public static void SnapServiceLockKey(DET_ServiceLock_Key x)
        {
            if (x == null) return;
            try { x.Open = true; } catch { }
            try { if (x.KeyObj != null) x.KeyObj.SetActive(true); } catch { }
            try
            {
                if (x.Key != null)
                {
                    var e = x.Key.localEulerAngles;
                    e.z = x.keyTargetRot;
                    x.Key.localEulerAngles = e;
                }
            }
            catch { }
            try
            {
                if (x.Door != null)
                {
                    var e = x.Door.localEulerAngles;
                    e.y = x.doorTargetRot;
                    x.Door.localEulerAngles = e;
                }
            }
            catch { }
            try
            {
                if (x.lockInteraction != null)
                    x.lockInteraction.SetActive(false);
            }
            catch { }
            try { PuzzleSyncService.DisableInteractions(x); } catch { }
            try { if (x.inter != null) PuzzleSyncService.DisableOne(x.inter); } catch { }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        public static void SnapSafeDoorSmall(SafeDoorSmall x)
        {
            if (x == null) return;
            try { x.open = true; } catch { }
            try { if (x.key != null) x.key.SetActive(true); } catch { }
            try { if (x.smallKey != null) x.smallKey.SetActive(true); } catch { }
            try { PuzzleSyncService.RevealPickups(x.key); } catch { }
            try { PuzzleSyncService.RevealPickups(x.smallKey); } catch { }
            try { PuzzleSyncService.RevealPickups(x.gameObject); } catch { }
            try
            {
                if (x.keypad != null)
                {
                    x.keypad.solved = true;
                    PuzzleSyncService.DisableInteractions(x.keypad);
                }
            }
            catch { }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        static void PoseDrawer(OpenableDrawer x, bool open)
        {
            try
            {
                if (x.drawer != null && open)
                    x.drawer.localPosition = x.openPos;
            }
            catch { }
            try
            {
                var content = x.Content;
                if (content != null)
                {
                    for (int i = 0; i < content.Count; i++)
                    {
                        try
                        {
                            if (content[i] != null)
                                content[i].SetActive(open);
                        }
                        catch { }
                    }
                }
            }
            catch { }
            if (open)
            {
                try { PuzzleSyncService.RevealPickups(x.gameObject); } catch { }
            }
        }

        internal static int PackBoolArray(Il2CppStructArray<bool> arr)
        {
            int bits = 0;
            if (arr == null) return bits;
            try
            {
                int n = arr.Length;
                if (n > 32) n = 32;
                for (int i = 0; i < n; i++)
                {
                    try { if (arr[i]) bits |= 1 << i; } catch { }
                }
            }
            catch { }
            return bits;
        }

        internal static void UnpackBoolArray(Il2CppStructArray<bool> arr, int bits)
        {
            if (arr == null) return;
            try
            {
                int n = arr.Length;
                if (n > 32) n = 32;
                for (int i = 0; i < n; i++)
                {
                    try { arr[i] = (bits & (1 << i)) != 0; } catch { }
                }
            }
            catch { }
        }
    }
}
