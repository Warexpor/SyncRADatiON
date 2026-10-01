using UnhollowerBaseLib;
using SyncRADation.ItemSystem;
using SyncRADation.Patches;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    /// <summary>
    /// PEN_Codepad + LAB_PatternLock snap/disable + OnEnable rematch + overlay kill.
    /// </summary>
    public sealed class CodepadSyncService
    {
        readonly IPuzzleDomainHost _host;

        internal CodepadSyncService(IPuzzleDomainHost host)
        {
            _host = host;
        }

        public void HandleCodepadEnabled(PEN_Codepad pad)
        {
            if (pad == null) return;
            ulong id = 0;
            try { id = WorldId.FromGameObject(pad.gameObject); } catch (System.Exception e) { Guard.Swallow(e); }
            bool solved = false;
            try { solved = pad.solved; } catch (System.Exception e) { Guard.Swallow(e); }
            if (!solved && !_host.IsHeld(PuzzleType.PEN_Codepad, id))
            {
                try
                {
                    var cryo = PuzzleDomainUtil.FindInParents<PEN_Cryo>(pad.gameObject);
                    if (cryo != null && cryo.opened) solved = true;
                }
                catch (System.Exception e) { Guard.Swallow(e); }
            }
            if (!solved && !_host.IsHeld(PuzzleType.PEN_Codepad, id) && !_host.CryoFamilyHeldUnmatched())
                return;
            if (!_host.IsHeld(PuzzleType.PEN_Codepad, id))
                _host.RemapHeld(PuzzleType.PEN_Codepad, id);
            NetGate.BeginApply();
            try { DisablePad(pad); }
            finally { NetGate.EndApply(); }
        }

        public void HandlePatternLockEnabled(LAB_PatternLock pad)
        {
            if (pad == null) return;
            ulong id = 0;
            try { id = WorldId.FromGameObject(pad.gameObject); } catch (System.Exception e) { Guard.Swallow(e); }
            bool solved = false;
            try { solved = pad.solved; } catch (System.Exception e) { Guard.Swallow(e); }
            if (!solved && !_host.IsHeld(PuzzleType.PatternLock, id))
            {
                try
                {
                    var cryo = PuzzleDomainUtil.FindInParents<PEN_Cryo>(pad.gameObject);
                    if (cryo != null && cryo.opened) solved = true;
                }
                catch (System.Exception e) { Guard.Swallow(e); }
            }
            if (!solved && !_host.IsHeld(PuzzleType.PatternLock, id) && !_host.CryoFamilyHeldUnmatched())
                return;
            if (!_host.IsHeld(PuzzleType.PatternLock, id))
                _host.RemapHeld(PuzzleType.PatternLock, id);
            NetGate.BeginApply();
            try { DisablePatternLock(pad); }
            finally { NetGate.EndApply(); }
        }

        public bool ShouldKillOverlay(Interaction it)
        {
            if (it == null) return false;
            GameObject go = null;
            try { go = it.gameObject; } catch (System.Exception e) { Guard.Swallow(e); }
            if (go == null) return false;
            if (DroppedItemManager.IsDroppedGo(go)) return false;
            try
            {
                var pad = PuzzleDomainUtil.FindInParents<LAB_PatternLock>(go);
                if (pad != null)
                {
                    ulong id = WorldId.FromGameObject(pad.gameObject);
                    if (pad.solved || _host.IsHeld(PuzzleType.PatternLock, id))
                        return true;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                var cryo = PuzzleDomainUtil.FindInParents<PEN_Cryo>(go);
                if (cryo != null)
                {
                    ulong id = WorldId.FromGameObject(cryo.gameObject);
                    if (cryo.opened || _host.IsHeld(PuzzleType.PEN_Cryo, id))
                        return true;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                var doorLock = PuzzleDomainUtil.FindInParents<CryoDoorLock>(go);
                if (doorLock != null)
                {
                    ulong id = WorldId.FromGameObject(doorLock.gameObject);
                    if (doorLock.done || _host.IsHeld(PuzzleType.CryoDoorLock, id))
                        return true;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                var code = PuzzleDomainUtil.FindInParents<PEN_Codepad>(go);
                if (code != null)
                {
                    ulong id = WorldId.FromGameObject(code.gameObject);
                    if (code.solved || _host.IsHeld(PuzzleType.PEN_Codepad, id))
                        return true;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return false;
        }

        public static bool TryRead(PuzzleType type, Component c, long wid, out PuzzleStateEntry entry)
        {
            entry = default;
            switch (type)
            {
                case PuzzleType.PEN_Codepad:
                {
                    var x = (PEN_Codepad)c;
                    int i3;
                    int wheels = KeypadLive.PackWheels(x, unchecked((ulong)wid), out i3);
                    entry = PuzzleDomainUtil.Mk(type, wid, x.solved, false, false, wheels, 0, 0, i3, 0);
                    return true;
                }
                case PuzzleType.PatternLock:
                {
                    var x = (LAB_PatternLock)c;
                    // states is bool[,] (Il2Cpp rank-2). Pack up to 8×8 into Int0/Int1.
                    // Int2 = rows<<8 | cols (dim0 = column). Int3 = cell count so an all-off grid still
                    // holds across remount (Dig AI). Solved stays Bool0.
                    int bits0 = 0, bits1 = 0, dims = 0, count = 0;
                    try { PackPatternStates(x, out bits0, out bits1, out dims, out count); } catch (System.Exception e) { Guard.Swallow(e); }
                    entry = PuzzleDomainUtil.Mk(type, wid, x.solved, false, false, bits0, bits1, dims, count, 0);
                    return true;
                }
                default:
                    return false;
            }
        }

        public static void ApplyCodepad(PEN_Codepad x, PuzzleStateEntry e, bool cinematic)
        {
            if (x == null)
            {
                PlaytestLog.Miss("Puzzle", "PEN_Codepad", unchecked((ulong)e.WorldId));
                return;
            }
            if (x.solved && !e.Bool0) return;
            KeypadLive.ApplyCodepad(x, e);
            x.solved = e.Bool0 || x.solved;
            if (e.Bool0)
                ApplyCodepadConsequences(x, cinematic);
        }

        public static void ApplyPatternLock(LAB_PatternLock x, PuzzleStateEntry e)
        {
            if (x == null) return;
            // Rising-edge onSolved: native delayed.MoveNext (after checkSolution latches
            // solved @ +0xA0) loads onSolved @ +0xB0 and calls UnityEvent$$Invoke
            // (RVA 0xDD7BF0). AssetStudio LAB_PatternLock.onSolved → exitEvent (EventScreen
            // DoorLockEvent) + Play + SetActive (PEN_CryoOverride / LAB ponds). Prior
            // ApplyPatternLock (~159–166) only latched solved + DisablePatternLock +
            // TryUnlockDoors — never Invoked onSolved → peer EventScreen softlock
            // (Dig K #2 / Dig J #5). Melon fields solved / onSolved verified (camelCase);
            // no onLoad UnityEvent — FullRefresh keep skip (mirror ApplyMural 0.5.26 /
            // ApplyPower 0.5.27). Mirror ApplyMulti 0.5.29 live path: MutateWorld&&!was
            // → BeginApply + onSolved.Invoke(); keep DisablePatternLock + TryUnlockDoors.
            bool was = false;
            try { was = x.solved; } catch (System.Exception ex) { Guard.Swallow(ex); }
            x.solved = e.Bool0;
            // Lights first, solved or not: the solving press is the last light. A peer's press clicks here like the
            // native delayed() press (zoomed in: the same 2D click; nearby: 3D at the panel).
            int changed = ApplyPatternButtons(x, e);
            if (changed > 0 && PuzzleFx.LiveApply)
            {
                string click = null;
                try { click = x.clickSFX; } catch (System.Exception ex) { Guard.Swallow(ex); }
                PuzzleFx.Press(x, click);
            }
            if (!e.Bool0) return;
            // Shared edge rule (PuzzleEdge): the latch above used to make every later apply (join dump,
            // held re-snap) look "already solved" and skip the consequence. Live rising edge = native
            // onSolved; everything else = the idempotent durable form (pad off, _event off, _door on).
            PuzzleEdge.Solved("PatternLock", was, true,
                durable: () => DisablePatternLock(x),
                onLive: () =>
                {
                    DisablePatternLock(x);
                    NetGate.BeginApply();
                    try
                    {
                        if (x.onSolved != null)
                            x.onSolved.Invoke();
                    }
                    finally { NetGate.EndApply(); }
                });
            PuzzleDoorFlagsSyncService.TryUnlockDoors(x.gameObject);
        }

        public static void ApplyCodepadConsequences(PEN_Codepad pad)
            => ApplyCodepadConsequences(pad, playAnim: true);

        public static void ApplyCodepadConsequences(PEN_Codepad pad, bool playAnim)
        {
            if (pad == null) return;
            pad.solved = true;
            DisablePad(pad);
            PuzzleSyncService.TryUnlockDoors(pad.gameObject);
            PlaytestLog.Event("Puzzle", "codepad solved " + pad.gameObject.name);
            try
            {
                var locks = WorldLookup.All<CryoDoorLock>();
                if (locks == null) return;
                for (int i = 0; i < locks.Length; i++)
                {
                    var c = locks[i];
                    if (c == null) continue;
                    try
                    {
                        if (c.puzzle != null && c.puzzle != pad) continue;
                    }
                    catch { continue; }
                    CryoSyncService.SnapCryoLock(c, playAnim);
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        /// <summary>
        /// Mid-pattern lights. setButtonState is the native writer (column, row, on)
        /// so materials match. Only cells that differ are written. No-op when Int3
        /// is 0 (older payload / unreadable array).
        /// </summary>
        static int ApplyPatternButtons(LAB_PatternLock x, PuzzleStateEntry e)
        {
            if (x == null || e.Int3 <= 0) return 0;
            // Int2 low = dim0 (setButtonState column), high = dim1 (row).
            int cols = e.Int2 & 0xFF;
            int rows = (e.Int2 >> 8) & 0xFF;
            if (rows <= 0 || cols <= 0 || rows > 8 || cols > 8) return 0;
            int n = rows * cols;
            if (n > 64) n = 64;
            int changed = 0;
            for (int i = 0; i < n; i++)
            {
                int row = i / cols;
                int col = i % cols;
                bool on = i < 32
                    ? (e.Int0 & (1 << i)) != 0
                    : (e.Int1 & (1 << (i - 32))) != 0;
                bool cur = false;
                try { cur = ReadPatternCell(x, col, row); } catch (System.Exception ex) { Guard.Swallow(ex); }
                if (cur == on) continue;
                try { x.setButtonState(col, row, on); changed++; } catch (System.Exception ex) { Guard.Swallow(ex); }
            }
            return changed;
        }

        static void PackPatternStates(LAB_PatternLock x, out int bits0, out int bits1, out int dims, out int count)
        {
            bits0 = 0;
            bits1 = 0;
            dims = 0;
            count = 0;
            if (x == null || x.states == null) return;
            var arr = new Il2CppSystem.Array(x.states.Pointer);
            if (arr == null || arr.Rank != 2) return;
            // dim0 is setButtonState's column; dim1 is the row.
            int cols = arr.GetLength(0);
            int rows = arr.GetLength(1);
            if (rows <= 0 || cols <= 0) return;
            if (rows > 8) rows = 8;
            if (cols > 8) cols = 8;
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

        static bool ReadPatternCell(LAB_PatternLock x, int col, int row)
        {
            if (x == null || x.states == null) return false;
            var arr = new Il2CppSystem.Array(x.states.Pointer);
            return ReadArrayBool(arr, col, row);
        }

        static bool ReadArrayBool(Il2CppSystem.Array arr, int i0, int i1)
        {
            if (arr == null) return false;
            var boxed = arr.GetValue(i0, i1);
            if (boxed == null) return false;
            return boxed.Unbox<bool>();
        }

        public static void DisablePatternLock(LAB_PatternLock pad)
        {
            if (pad == null) return;
            try { pad.solved = true; } catch (System.Exception e) { Guard.Swallow(e); }
            PuzzleSyncService.DisableInteractions(pad);
            try
            {
                var ctrl = pad.GetComponent<Lab_PatternLockControl>()
                    ?? pad.GetComponentInChildren<Lab_PatternLockControl>(true);
                if (ctrl == null)
                    ctrl = PuzzleDomainUtil.FindInParents<Lab_PatternLockControl>(pad.gameObject);
                if (ctrl != null)
                {
                    PuzzleSyncService.DisableInteractions(ctrl);
                    try
                    {
                        if (ctrl._event != null)
                        {
                            PuzzleSyncService.DisableInteractions(ctrl._event.transform);
                            try { ctrl._event.SetActive(false); } catch (System.Exception e) { Guard.Swallow(e); }
                        }
                    }
                    catch (System.Exception e) { Guard.Swallow(e); }
                    SnapPatternDoor(ctrl);
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        /// <summary>
        /// Decompile Lab_PatternLockControl.CheckState (solved branch): _event off, _door on, DoorL z=120, DoorR z=60.
        /// Applied directly so a late joiner / remounted room is open without waiting for the control's Update.
        /// </summary>
        static void SnapPatternDoor(Lab_PatternLockControl ctrl)
        {
            if (ctrl == null) return;
            try
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
            catch (System.Exception ex)
            {
                PuzzleSyncService.WarnOnce("pattern-door", ex.Message);
            }
        }

        public static void DisablePad(PEN_Codepad pad)
        {
            if (pad == null) return;
            try { pad.solved = true; } catch (System.Exception e) { Guard.Swallow(e); }
            PuzzleSyncService.DisableInteractions(pad);
            try
            {
                var buttons = pad.buttons;
                if (buttons != null)
                {
                    for (int i = 0; i < buttons.Length; i++)
                        PuzzleSyncService.DisableOne(buttons[i]);
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                var counter = pad.counterButtons;
                if (counter != null)
                {
                    for (int i = 0; i < counter.Length; i++)
                        PuzzleSyncService.DisableOne(counter[i]);
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }
    }
}
