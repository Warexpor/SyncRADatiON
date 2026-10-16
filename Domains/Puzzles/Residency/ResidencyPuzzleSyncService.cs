using System;
using SyncRADation.Sync;
using UnhollowerBaseLib;
using UnityEngine;
using static SyncRADation.Networking.PuzzleDomainUtil;

namespace SyncRADation.Networking
{
    /// <summary>
    /// Residency / key / drawer puzzles (RES_MusicBox..OpenableDrawer): final-pose snaps. MED_KeyGrid and
    /// ArianePhotoCode are static globals (WorldId 0).
    /// </summary>
    public sealed class ResidencyPuzzleSyncService
    {
        internal static PuzzleStateEntry ReadMusicBox(RES_MusicBox x, long wid)
            => Mk(PuzzleType.RES_MusicBox, wid, x.opened, x.hasCassette, false, 0, 0, 0, 0, 0);

        /// <summary>Float0/Float1 = robotPos (spawn 9,5; victory 10,1). Bool1 = pack valid, so the spawn cell still holds.</summary>
        internal static PuzzleStateEntry ReadLibraryPc(RES_LibraryPC x, long wid)
        {
            var p = x.robotPos;
            return Mk(PuzzleType.RES_LibraryPC, wid, x.solved, true, false, 0, 0, 0, 0, p.x, p.y);
        }

        internal static PuzzleStateEntry ReadPaternoster(RES_Paternoster x, long wid)
            => Mk(PuzzleType.RES_Paternoster, wid, x.powered, false, false, 0, 0, 0, 0, 0);

        internal static PuzzleStateEntry ReadServiceLockKey(DET_ServiceLock_Key x, long wid)
            => Mk(PuzzleType.DET_ServiceLock_Key, wid, x.Open, false, false, 0, 0, 0, 0, 0);

        internal static PuzzleStateEntry ReadSafeDoor(SafeDoorSmall x, long wid)
            => Mk(PuzzleType.SafeDoorSmall, wid, x.open, false, false, 0, 0, 0, 0, 0);

        /// <summary>Unlocked = every keys[] slot set (what native checkLock() computes); Int0 = keys pack.</summary>
        internal static PuzzleStateEntry ReadMultiKeyLock(MultiKeyLock x, long wid)
        {
            var keys = x.keys;
            bool unlocked = keys != null && keys.Length > 0;
            if (unlocked)
                for (int i = 0; i < keys.Length; i++)
                    if (!keys[i]) { unlocked = false; break; }
            return Mk(PuzzleType.MultiKeyLock, wid, unlocked, false, false, PackBoolArray(keys), 0, 0, 0, 0);
        }

        internal static PuzzleStateEntry ReadDrawer(OpenableDrawer x, long wid)
            => Mk(PuzzleType.OpenableDrawer, wid, x.open, false, false, 0, 0, 0, 0, 0);

        /// <summary>Bool0 = static MED_KeyGrid.solved; Int0 = nodes connected (first grid with nodes), Int1 = node count.</summary>
        internal static PuzzleStateEntry ReadKeyGridGlobal()
        {
            int bits = 0, count = 0;
            var grids = WorldLookup.All<MED_KeyGrid>();
            if (grids != null)
            {
                for (int g = 0; g < grids.Length; g++)
                {
                    if (grids[g] == null) continue;
                    ChapterMachineSyncService.PackNodes(grids[g].nodes, out bits, out count);
                    if (count > 0) break;
                }
            }
            return Mk(PuzzleType.MED_KeyGrid, 0, MED_KeyGrid.solved, false, false, bits, count, 0, 0, 0f);
        }

        internal static PuzzleStateEntry ReadArianePhotoCodeGlobal()
        {
            int code = ArianePhotoCode.code;
            return Mk(PuzzleType.ArianePhotoCode, 0, code != 0, false, false, code, 0, 0, 0, 0f);
        }

        /// <summary>
        /// Native Update fires onSuccess only on opened false → true, so a remote latch would skip it forever. Scene
        /// data onSuccess = dimPOI (the durable form replays it). hasCassette is the wire's Bool1, never forced.
        /// </summary>
        internal static void ApplyMusicBox(RES_MusicBox x, PuzzleStateEntry e)
        {
            if (x == null) return;
            bool was = x.opened;
            x.hasCassette = e.Bool1;
            if (!e.Bool0) return;
            PuzzleEdge.Solved("RES_MusicBox", was, true,
                durable: () =>
                {
                    if (was) return;
                    PuzzleEdge.ReplayDurable(x.onSuccess);
                    SnapMusicBox(x);
                },
                onLive: () =>
                {
                    PuzzleEdge.Invoke(x.onSuccess);
                    SnapMusicBox(x);
                }, at: x);
        }

        static void SnapMusicBox(RES_MusicBox x)
        {
            x.opened = true;
            if (x.CardPickup != null) x.CardPickup.SetActive(true);
            if (x.BoxObs != null) x.BoxObs.SetActive(false);
            if (x.euleLid != null)
            {
                var r = x.euleLid.localEulerAngles;
                r.z = x.lidOpen;
                x.euleLid.localEulerAngles = r;
            }
            PuzzleSyncService.RevealPickups(x.CardPickup);
            PuzzleSyncService.RevealPickups(x.gameObject);
            PuzzleSyncService.DisableInteractions(x);
            if (x.tapeInteraction != null) x.tapeInteraction.SetActive(false);
        }

        /// <summary>
        /// robotPos applies always (live + dump). A peer's live move glides at the native movementSpeed (the native
        /// Move* coroutines set gameState 4/7, so they cannot be replayed on a player outside the screen); native Update
        /// draws RobotX/RobotY from robotPos. onSuccess (scene data: Play only) is the live in-room edge.
        /// </summary>
        internal static void ApplyLibraryPc(RES_LibraryPC x, PuzzleStateEntry e)
        {
            if (x == null) return;
            if (e.Bool1)
            {
                var to = new Vector2(e.Float0, e.Float1);
                if (PuzzleFx.LiveApply && !e.Bool0) LibraryRobotGlide.To(x, to);
                else
                {
                    LibraryRobotGlide.Stop(x);
                    x.robotPos = to;
                }
            }
            if (!e.Bool0) return;
            bool was = x.solved;
            x.solved = true;
            if (x.Pickup != null) x.Pickup.SetActive(true);
            if (x.Tome != null) x.Tome.gameObject.SetActive(true);
            PuzzleSyncService.RevealPickups(x.Pickup);
            PuzzleSyncService.RevealPickups(x.gameObject);
            PuzzleSyncService.DisableInteractions(x);
            PuzzleEdge.Solved("RES_LibraryPC", was, true, durable: null,
                onLive: () => PuzzleEdge.Invoke(x.onSuccess), at: x);
        }

        internal static void ApplyPaternoster(RES_Paternoster x, PuzzleStateEntry e)
        {
            if (x == null) return;
            try { x.setPower(e.Bool0); }
            catch (Exception ex)
            {
                Guard.Swallow("Puzzle.paternoster-power", ex);
                x.powered = e.Bool0;
            }
        }

        /// <summary>Bool0 → static solved; Int0 → nodes connected + nodesObjects active (what native Update mirrors).</summary>
        internal static void ApplyKeyGrid(PuzzleStateEntry e)
        {
            MED_KeyGrid.solved = e.Bool0;
            if (e.Int1 <= 0) return;
            var grids = WorldLookup.All<MED_KeyGrid>();
            if (grids == null) return;
            for (int g = 0; g < grids.Length; g++)
            {
                var x = grids[g];
                if (x == null) continue;
                ChapterMachineSyncService.UnpackNodes(x.nodes, e.Int0, e.Int1);
                var objs = x.nodesObjects;
                if (objs == null) continue;
                int n = objs.Count;
                if (e.Int1 < n) n = e.Int1;
                n = Mathf.Min(n, 32);
                for (int i = 0; i < n; i++)
                    if (objs[i] != null) objs[i].SetActive((e.Int0 & (1 << i)) != 0);
            }
        }

        /// <summary>LoadState may reload the code from the local save: the wire's code is asserted again after it.</summary>
        internal static void ApplyArianePhotoCode(PuzzleStateEntry e)
        {
            ArianePhotoCode.code = e.Int0;
            Native("ariane-load", ArianePhotoCode.LoadState);
            ArianePhotoCode.code = e.Int0;
        }

        internal static void ApplyServiceLockKey(DET_ServiceLock_Key x, PuzzleStateEntry e)
        {
            if (x == null || !e.Bool0) return;
            x.Open = true;
            if (x.KeyObj != null) x.KeyObj.SetActive(true);
            if (x.Key != null)
            {
                var r = x.Key.localEulerAngles;
                r.z = x.keyTargetRot;
                x.Key.localEulerAngles = r;
            }
            if (x.Door != null)
            {
                var r = x.Door.localEulerAngles;
                r.y = x.doorTargetRot;
                x.Door.localEulerAngles = r;
            }
            if (x.lockInteraction != null) x.lockInteraction.SetActive(false);
            PuzzleSyncService.DisableInteractions(x);
            PuzzleSyncService.DisableOne(x.inter);
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        /// <summary>onSolved (scene data: dimPOI) only on the opening edge; the open pose on every apply.</summary>
        internal static void ApplySafeDoor(SafeDoorSmall x, PuzzleStateEntry e)
        {
            if (x == null || !e.Bool0) return;
            bool was = x.open;
            x.open = true;
            if (x.key != null) x.key.SetActive(true);
            if (x.smallKey != null) x.smallKey.SetActive(true);
            PuzzleSyncService.RevealPickups(x.key);
            PuzzleSyncService.RevealPickups(x.smallKey);
            PuzzleSyncService.RevealPickups(x.gameObject);
            if (x.keypad != null)
            {
                x.keypad.solved = true;
                PuzzleSyncService.DisableInteractions(x.keypad);
            }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
            PuzzleEdge.Solved("SafeDoorSmall", was, true,
                durable: () => { if (!was) PuzzleEdge.ReplayDurable(x.onSolved); },
                onLive: () => PuzzleEdge.Invoke(x.onSolved), at: x);
        }

        /// <summary>
        /// keys[] is the whole lock state: checkLock() is a pure predicate over it (Ghidra MultiKeyLock.c, no writes),
        /// so unpacking keys is the unlock; the door / interaction consequences follow.
        /// </summary>
        internal static void ApplyMultiKeyLock(MultiKeyLock x, PuzzleStateEntry e)
        {
            if (x == null) return;
            UnpackBoolArray(x.keys, e.Int0);
            if (!e.Bool0) return;
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
            PuzzleSyncService.DisableInteractions(x);
        }

        internal static void ApplyDrawer(OpenableDrawer x, PuzzleStateEntry e)
        {
            if (x == null || x.open == e.Bool0) return;
            x.open = e.Bool0;
            if (x.drawer != null && e.Bool0)
                x.drawer.localPosition = x.openPos;
            var content = x.Content;
            if (content != null)
                for (int i = 0; i < content.Count; i++)
                    if (content[i] != null) content[i].SetActive(e.Bool0);
            if (e.Bool0)
                PuzzleSyncService.RevealPickups(x.gameObject);
        }

        internal static int PackBoolArray(Il2CppStructArray<bool> arr)
        {
            int bits = 0;
            if (arr == null) return bits;
            int n = Mathf.Min(arr.Length, 32);
            for (int i = 0; i < n; i++)
                if (arr[i]) bits |= 1 << i;
            return bits;
        }

        internal static void UnpackBoolArray(Il2CppStructArray<bool> arr, int bits)
        {
            if (arr == null) return;
            int n = Mathf.Min(arr.Length, 32);
            for (int i = 0; i < n; i++)
                arr[i] = (bits & (1 << i)) != 0;
        }
    }
}
