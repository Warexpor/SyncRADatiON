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
            try { id = WorldId.FromGameObject(pad.gameObject); } catch { }
            bool solved = false;
            try { solved = pad.solved; } catch { }
            if (!solved && !_host.IsHeld(PuzzleType.PEN_Codepad, id))
            {
                try
                {
                    var cryo = PuzzleDomainUtil.FindInParents<PEN_Cryo>(pad.gameObject);
                    if (cryo != null && cryo.opened) solved = true;
                }
                catch { }
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
            try { id = WorldId.FromGameObject(pad.gameObject); } catch { }
            bool solved = false;
            try { solved = pad.solved; } catch { }
            if (!solved && !_host.IsHeld(PuzzleType.PatternLock, id))
            {
                try
                {
                    var cryo = PuzzleDomainUtil.FindInParents<PEN_Cryo>(pad.gameObject);
                    if (cryo != null && cryo.opened) solved = true;
                }
                catch { }
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
            try { go = it.gameObject; } catch { }
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
            catch { }
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
            catch { }
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
            catch { }
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
            catch { }
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
                    entry = PuzzleDomainUtil.Mk(type, wid, x.solved, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.PatternLock:
                {
                    var x = (LAB_PatternLock)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.solved, false, false, 0, 0, 0, 0, 0);
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
            try { was = x.solved; } catch { }
            x.solved = e.Bool0;
            if (!e.Bool0) return;
            DisablePatternLock(x);
            if (PuzzleSyncService.MutateWorld && !was)
            {
                NetGate.BeginApply();
                try
                {
                    if (x.onSolved != null)
                        x.onSolved.Invoke();
                }
                catch { }
                finally { NetGate.EndApply(); }
            }
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
            catch { }
        }

        public static void DisablePatternLock(LAB_PatternLock pad)
        {
            if (pad == null) return;
            try { pad.solved = true; } catch { }
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
                            try { ctrl._event.SetActive(false); } catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        public static void DisablePad(PEN_Codepad pad)
        {
            if (pad == null) return;
            try { pad.solved = true; } catch { }
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
            catch { }
            try
            {
                var counter = pad.counterButtons;
                if (counter != null)
                {
                    for (int i = 0; i < counter.Length; i++)
                        PuzzleSyncService.DisableOne(counter[i]);
                }
            }
            catch { }
        }
    }
}
