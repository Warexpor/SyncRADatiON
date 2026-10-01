using System;
using SyncRADation.ItemSystem;
using SyncRADation.Sync;
using UnityEngine;
using static SyncRADation.Networking.PuzzleDomainUtil;

namespace SyncRADation.Networking
{
    /// <summary>PEN_Codepad + LAB_PatternLock read/apply, OnEnable rematch and the solved-overlay kill.</summary>
    public sealed class CodepadSyncService
    {
        readonly IPuzzleDomainHost _host;

        internal CodepadSyncService(IPuzzleDomainHost host)
        {
            _host = host;
        }

        /// <summary>Native OnEnable re-enables the pad: shut it in the same callback when it is (held) solved.</summary>
        public void HandleCodepadEnabled(PEN_Codepad pad)
        {
            if (pad == null) return;
            try
            {
                if (!SolvedOrHeld(PuzzleType.PEN_Codepad, pad, pad.solved)) return;
                NetGate.BeginApply();
                try { DisablePad(pad); }
                finally { NetGate.EndApply(); }
            }
            catch (Exception e) { Guard.Swallow(e); }
        }

        public void HandlePatternLockEnabled(LAB_PatternLock pad)
        {
            if (pad == null) return;
            try
            {
                if (!SolvedOrHeld(PuzzleType.PatternLock, pad, pad.solved)) return;
                NetGate.BeginApply();
                try { DisablePatternLock(pad); }
                finally { NetGate.EndApply(); }
            }
            catch (Exception e) { Guard.Swallow(e); }
        }

        /// <summary>
        /// Solved natively, under an opened PEN_Cryo, held, or the local copy of a held entry under another WorldId
        /// (then the held entry is remapped to this pad).
        /// </summary>
        bool SolvedOrHeld(PuzzleType type, Component pad, bool solved)
        {
            ulong id = WorldId.FromGameObject(pad.gameObject);
            bool held = _host.IsHeld(type, id);
            if (!solved && !held)
            {
                var cryo = FindInParents<PEN_Cryo>(pad.gameObject);
                if (cryo != null && cryo.opened) solved = true;
            }
            if (!solved && !held && !_host.HeldUnmatched(type, id))
                return false;
            if (!held)
                _host.RemapHeld(type, id);
            return true;
        }

        public bool ShouldKillOverlay(Interaction it)
        {
            if (it == null) return false;
            try
            {
                GameObject go = it.gameObject;
                if (go == null || DroppedItemRegistry.IsDroppedGo(go)) return false;
                var pad = FindInParents<LAB_PatternLock>(go);
                if (pad != null && (pad.solved || _host.IsHeld(PuzzleType.PatternLock, WorldId.FromGameObject(pad.gameObject))))
                    return true;
                var cryo = FindInParents<PEN_Cryo>(go);
                if (cryo != null && (cryo.opened || _host.IsHeld(PuzzleType.PEN_Cryo, WorldId.FromGameObject(cryo.gameObject))))
                    return true;
                var doorLock = FindInParents<CryoDoorLock>(go);
                if (doorLock != null && (doorLock.done || _host.IsHeld(PuzzleType.CryoDoorLock, WorldId.FromGameObject(doorLock.gameObject))))
                    return true;
                var code = FindInParents<PEN_Codepad>(go);
                return code != null && (code.solved || _host.IsHeld(PuzzleType.PEN_Codepad, WorldId.FromGameObject(code.gameObject)));
            }
            catch (Exception e) { Guard.Swallow(e); return false; }
        }

        internal static PuzzleStateEntry ReadCodepad(PEN_Codepad x, long wid)
        {
            int i3;
            int wheels = KeypadLive.PackWheels(x, unchecked((ulong)wid), out i3);
            return Mk(PuzzleType.PEN_Codepad, wid, x.solved, false, false, wheels, 0, 0, i3, 0);
        }

        /// <summary>
        /// states is bool[,] (Il2Cpp rank 2): up to 8×8 packed into Int0/Int1. Int2 = rows &lt;&lt; 8 | cols (dim0 = column),
        /// Int3 = cell count so an all-off grid still holds across a remount. Bool0 = solved.
        /// </summary>
        internal static PuzzleStateEntry ReadPatternLock(LAB_PatternLock x, long wid)
        {
            int bits0, bits1, dims, count;
            PackPatternStates(x, out bits0, out bits1, out dims, out count);
            return Mk(PuzzleType.PatternLock, wid, x.solved, false, false, bits0, bits1, dims, count, 0);
        }

        internal static void ApplyCodepad(PEN_Codepad x, PuzzleStateEntry e)
        {
            if (x == null) return;
            if (x.solved && !e.Bool0) return;
            KeypadLive.ApplyCodepad(x, e);
            x.solved = e.Bool0 || x.solved;
            if (e.Bool0)
                ApplyCodepadConsequences(x, PuzzleSyncService.LiveEdge);
        }

        /// <summary>
        /// Native delayed() latches solved in checkSolution and invokes onSolved (Ghidra LAB_PatternLock.c). Scene data
        /// PEN_Wreck PEN_CryoOverride.onSolved: Cryo/Inter on, unlock SFX, exitEvent on Pivot, Pivot/Interaction off;
        /// the LAB locks: exitEvent + SFX. Live in-room edge = onSolved; everything else (join dump, held re-snap,
        /// another room) = pad off + the SetActive part of onSolved, so a late joiner gets the cryo interaction too.
        /// </summary>
        internal static void ApplyPatternLock(LAB_PatternLock x, PuzzleStateEntry e)
        {
            if (x == null) return;
            bool was = x.solved;
            x.solved = e.Bool0;
            // Lights first, solved or not: the solving press is the last light. A peer's press clicks here like the
            // native delayed() press (zoomed in: the same 2D click; nearby: 3D at the panel).
            if (ApplyPatternButtons(x, e) > 0 && PuzzleFx.LiveApply)
                PuzzleFx.Press(x, x.clickSFX);
            if (!e.Bool0) return;
            PuzzleEdge.Solved("PatternLock", was, true,
                durable: () =>
                {
                    DisablePatternLock(x);
                    if (!was) PuzzleEdge.ReplayDurable(x.onSolved);
                },
                onLive: () =>
                {
                    DisablePatternLock(x);
                    PuzzleEdge.Invoke(x.onSolved);
                }, at: x);
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        static void ApplyCodepadConsequences(PEN_Codepad pad, bool playAnim)
        {
            pad.solved = true;
            DisablePad(pad);
            PuzzleSyncService.TryUnlockDoors(pad.gameObject);
            PlaytestLog.Event("Puzzle", "codepad solved " + pad.gameObject.name);
            var locks = WorldLookup.All<CryoDoorLock>();
            if (locks == null) return;
            for (int i = 0; i < locks.Length; i++)
            {
                var c = locks[i];
                if (c == null || (c.puzzle != null && c.puzzle != pad)) continue;
                CryoSyncService.SnapCryoLock(c, playAnim);
            }
        }

        /// <summary>
        /// Mid-pattern lights through setButtonState (the native writer: column, row, on) so materials match. Only
        /// cells that differ are written. No-op when Int3 is 0 (unreadable array).
        /// </summary>
        static int ApplyPatternButtons(LAB_PatternLock x, PuzzleStateEntry e)
        {
            if (e.Int3 <= 0 || x.states == null) return 0;
            int cols = e.Int2 & 0xFF;
            int rows = (e.Int2 >> 8) & 0xFF;
            if (rows <= 0 || cols <= 0 || rows > 8 || cols > 8) return 0;
            var arr = new Il2CppSystem.Array(x.states.Pointer);
            int n = rows * cols;
            int changed = 0;
            for (int i = 0; i < n; i++)
            {
                int row = i / cols;
                int col = i % cols;
                bool on = i < 32 ? (e.Int0 & (1 << i)) != 0 : (e.Int1 & (1 << (i - 32))) != 0;
                if (ReadArrayBool(arr, col, row) == on) continue;
                x.setButtonState(col, row, on);
                changed++;
            }
            return changed;
        }

        static void PackPatternStates(LAB_PatternLock x, out int bits0, out int bits1, out int dims, out int count)
        {
            bits0 = bits1 = dims = count = 0;
            if (x.states == null) return;
            var arr = new Il2CppSystem.Array(x.states.Pointer);
            if (arr.Rank != 2) return;
            // dim0 is setButtonState's column; dim1 is the row.
            int cols = Mathf.Min(arr.GetLength(0), 8);
            int rows = Mathf.Min(arr.GetLength(1), 8);
            if (rows <= 0 || cols <= 0) return;
            count = rows * cols;
            dims = (rows << 8) | cols;
            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < cols; col++)
                {
                    if (!ReadArrayBool(arr, col, row)) continue;
                    int i = row * cols + col;
                    if (i < 32) bits0 |= 1 << i;
                    else bits1 |= 1 << (i - 32);
                }
            }
        }

        static bool ReadArrayBool(Il2CppSystem.Array arr, int i0, int i1)
        {
            var boxed = arr.GetValue(i0, i1);
            return boxed != null && boxed.Unbox<bool>();
        }

        internal static void DisablePatternLock(LAB_PatternLock pad)
        {
            if (pad == null) return;
            pad.solved = true;
            PuzzleSyncService.DisableInteractions(pad);
            var ctrl = pad.GetComponent<Lab_PatternLockControl>()
                ?? pad.GetComponentInChildren<Lab_PatternLockControl>(true)
                ?? FindInParents<Lab_PatternLockControl>(pad.gameObject);
            if (ctrl == null) return;
            PuzzleSyncService.DisableInteractions(ctrl);
            if (ctrl._event != null)
            {
                PuzzleSyncService.DisableInteractions(ctrl._event.transform);
                ctrl._event.SetActive(false);
            }
            SnapPatternDoor(ctrl);
        }

        /// <summary>
        /// Decompile Lab_PatternLockControl.CheckState (solved branch): _event off, _door on, DoorL z=120, DoorR z=60.
        /// Applied directly so a late joiner / remounted room is open without waiting for the control's Update.
        /// </summary>
        static void SnapPatternDoor(Lab_PatternLockControl ctrl)
        {
            if (ctrl._door != null) ctrl._door.SetActive(true);
            var l = ctrl.L;
            l.z = 120f;
            ctrl.L = l;
            if (ctrl.DoorL != null) ctrl.DoorL.localRotation = Quaternion.Euler(l);
            var r = ctrl.R;
            r.z = 60f;
            ctrl.R = r;
            if (ctrl.DoorR != null) ctrl.DoorR.localRotation = Quaternion.Euler(r);
        }

        internal static void DisablePad(PEN_Codepad pad)
        {
            if (pad == null) return;
            pad.solved = true;
            PuzzleSyncService.DisableInteractions(pad);
            LockSyncService.DisableAll(pad.buttons);
            LockSyncService.DisableAll(pad.counterButtons);
        }
    }
}
