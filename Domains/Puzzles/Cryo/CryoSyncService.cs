using SyncRADation.Patches;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    /// <summary>
    /// CryoDoorLock / PEN_Cryo snap/pose + OnEnable rematch. World result only — never EventScreen.
    /// </summary>
    public sealed class CryoSyncService
    {
        readonly IPuzzleDomainHost _host;

        internal CryoSyncService(IPuzzleDomainHost host)
        {
            _host = host;
        }

        /// <summary>Native OnEnable re-enables pad/open. Shut them in the same callback if already solved.</summary>
        public void HandlePenCryoEnabled(PEN_Cryo x)
        {
            if (x == null) return;
            ulong id = 0;
            try { id = WorldId.FromGameObject(x.gameObject); } catch (System.Exception e) { Guard.Swallow(e); }
            bool open = false;
            try { open = x.opened; } catch (System.Exception e) { Guard.Swallow(e); }
            if (!open && !_host.IsHeld(PuzzleType.PEN_Cryo, id) && !_host.HeldUnmatched(PuzzleType.PEN_Cryo, id))
                return;
            if (!_host.IsHeld(PuzzleType.PEN_Cryo, id))
                _host.RemapHeld(PuzzleType.PEN_Cryo, id);
            NetGate.BeginApply();
            try { SnapPenCryo(x, playOpen: false); }
            finally { NetGate.EndApply(); }
        }

        public void HandleCryoLockEnabled(CryoDoorLock c)
        {
            if (c == null) return;
            ulong id = 0;
            try { id = WorldId.FromGameObject(c.gameObject); } catch (System.Exception e) { Guard.Swallow(e); }
            bool done = false;
            try { done = c.done; } catch (System.Exception e) { Guard.Swallow(e); }
            if (!done && !_host.IsHeld(PuzzleType.CryoDoorLock, id))
            {
                try
                {
                    if (c.puzzle != null && c.puzzle.solved)
                        done = true;
                }
                catch (System.Exception e) { Guard.Swallow(e); }
                try
                {
                    var pen = PuzzleDomainUtil.FindInParents<PEN_Cryo>(c.gameObject)
                        ?? (c.Door != null ? PuzzleDomainUtil.FindInParents<PEN_Cryo>(c.Door) : null);
                    if (pen != null && pen.opened) done = true;
                }
                catch (System.Exception e) { Guard.Swallow(e); }
            }
            if (!done && !_host.IsHeld(PuzzleType.CryoDoorLock, id) && !_host.HeldUnmatched(PuzzleType.CryoDoorLock, id))
                return;
            if (!_host.IsHeld(PuzzleType.CryoDoorLock, id))
                _host.RemapHeld(PuzzleType.CryoDoorLock, id);
            NetGate.BeginApply();
            try { SnapCryoLock(c, playAnim: false); }
            finally { NetGate.EndApply(); }
        }

        public static bool TryRead(PuzzleType type, Component c, long wid, out PuzzleStateEntry entry)
        {
            entry = default;
            switch (type)
            {
                case PuzzleType.CryoDoorController:
                {
                    var x = (CryoDoorController)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.open, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.CryoDoorLock:
                {
                    var x = (CryoDoorLock)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.done, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.PEN_Cryo:
                {
                    var x = (PEN_Cryo)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.opened, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                default:
                    return false;
            }
        }

        public static void ApplyCryoDoorController(CryoDoorController x, PuzzleStateEntry e)
        {
            if (x == null || e.Bool0 == x.open) return;
            try { x.toggleDoors(); }
            catch { x.open = e.Bool0; }
        }

        public static void ApplyCryoDoorLock(CryoDoorLock x, PuzzleStateEntry e, bool cinematic)
        {
            if (x != null && e.Bool0)
                SnapCryoLock(x, cinematic);
            else if (x == null)
                PlaytestLog.Miss("Puzzle", "CryoDoorLock", unchecked((ulong)e.WorldId));
        }

        public static void ApplyPenCryo(PEN_Cryo x, PuzzleStateEntry e, bool cinematic)
        {
            if (x != null && e.Bool0)
                SnapPenCryo(x, playOpen: cinematic);
            else if (x == null)
                PlaytestLog.Miss("Puzzle", "PEN_Cryo", unchecked((ulong)e.WorldId));
        }

        // World result only. Never EventScreen — that camera-locks the remote Elster.
        public static void SnapCryoLock(CryoDoorLock c, bool playAnim = false)
        {
            if (c == null) return;

            PlaytestLog.Event("Puzzle", "snap CryoDoorLock " + c.gameObject.name
                + (playAnim ? " anim" : " pose"));
            try { c.done = true; } catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                if (c.puzzle != null)
                    c.puzzle.solved = true;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                if (c.inter != null)
                {
                    c.inter.triggered = true;
                    c.inter.enabled = false;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                if (c.Event != null)
                {
                    PuzzleSyncService.DisableInteractions(c.Event);
                    try { c.Event.enabled = false; } catch (System.Exception e) { Guard.Swallow(e); }
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            if (c.puzzle != null)
                CodepadSyncService.DisablePad(c.puzzle);
            try
            {
                if (c.Door != null)
                {
                    // Decompile CryoDoorLock.LoadState (done): Door.SetActive(true). Durable object state, so it
                    // runs on the join dump too (was gated on MutateWorld and left late joiners without the door).
                    c.Door.SetActive(true);
                    PuzzleSyncService.UnlockDoorObject(c.Door);
                    TryOpenCryoController(c.Door, animate: playAnim);
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            PuzzleSyncService.TryUnlockDoors(c.gameObject);
            try
            {
                var pen = c.GetComponent<PEN_Cryo>()
                    ?? PuzzleDomainUtil.FindInParents<PEN_Cryo>(c.gameObject)
                    ?? c.GetComponentInChildren<PEN_Cryo>(true);
                if (pen != null)
                    SnapPenCryo(pen, playAnim);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        static void TryOpenCryoController(GameObject door, bool animate)
        {
            if (door == null) return;
            CryoDoorController ctrl = null;
            try { ctrl = door.GetComponent<CryoDoorController>(); } catch (System.Exception e) { Guard.Swallow(e); }
            if (ctrl == null)
            {
                try { ctrl = door.GetComponentInChildren<CryoDoorController>(true); } catch (System.Exception e) { Guard.Swallow(e); }
            }
            if (ctrl == null) return;
            try
            {
                if (ctrl.open) return;
                if (animate)
                    ctrl.toggleDoors();
                else
                    ctrl.open = true;
            }
            catch { ctrl.open = true; }
        }

        public static void SnapPenCryo(PEN_Cryo x, bool playOpen)
        {
            if (x == null) return;

            bool active = false;
            try { active = x.gameObject.activeInHierarchy; } catch { active = true; }
            ulong id = WorldId.FromGameObject(x.gameObject);
            bool animating = PuzzleSyncService.AnimStarted(PuzzleType.PEN_Cryo, id);

            if (!active || !playOpen)
                StampPenCryoOpen(x);

            if (!active)
            {
                PlaytestLog.Event("Puzzle", "snap PEN_Cryo " + x.gameObject.name + " wait inactive");
                DisableCryoAccess(x);
                return;
            }

            try { PuzzleSyncService.DisableOne(x.interaction); } catch (System.Exception e) { Guard.Swallow(e); }
            try { PuzzleSyncService.DisableInteractions(x); } catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                if (x.RoomDoor != null)
                    PuzzleSyncService.UnlockDoorObject(x.RoomDoor);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);

            if (animating)
            {
                PlaytestLog.Event("Puzzle", "snap PEN_Cryo " + x.gameObject.name + " skip");
                DisableCryoAccess(x);
                HideClaimedAround(x.gameObject);
                return;
            }

            if (playOpen)
            {
                if (id != 0)
                    PuzzleSyncService.NoteAnimStarted(PuzzleType.PEN_Cryo, id);
                PlaytestLog.Event("Puzzle", "snap PEN_Cryo " + x.gameObject.name + " open");
                try { x.Open(); }
                catch
                {
                    PosePenCryoOpen(x);
                    ActivateCryoContent(x);
                }
            }
            else
            {
                PlaytestLog.Event("Puzzle", "snap PEN_Cryo " + x.gameObject.name + " pose");
                PosePenCryoOpen(x);
                ActivateCryoContent(x);
            }

            DisableCryoAccess(x);
        }

        static void StampPenCryoOpen(PEN_Cryo x)
        {
            if (x == null) return;
            try { x.opened = true; } catch (System.Exception e) { Guard.Swallow(e); }
            try { x.doorPos = 1f; } catch (System.Exception e) { Guard.Swallow(e); }
            try { x.coverPos = 1f; } catch (System.Exception e) { Guard.Swallow(e); }
            try { x.moverPos = 1f; } catch (System.Exception e) { Guard.Swallow(e); }
            try { x.openerPos = 1f; } catch (System.Exception e) { Guard.Swallow(e); }
            try { x.fluidPos = 1f; } catch (System.Exception e) { Guard.Swallow(e); }
            try { x.brightness = 0f; } catch (System.Exception e) { Guard.Swallow(e); }
        }

        static void ActivateCryoContent(PEN_Cryo x)
        {
            if (x == null) return;
            if (x.contentLateActivated != null)
            {
                try { x.contentLateActivated.SetActive(true); } catch (System.Exception e) { Guard.Swallow(e); }
                try { PuzzleSyncService.RevealPickups(x.contentLateActivated); } catch (System.Exception e) { Guard.Swallow(e); }
            }
            HideClaimedAround(x.gameObject);
        }

        static void HideClaimedAround(GameObject root)
        {
            try
            {
                var net = LanNetworkManager.Instance;
                if (net != null)
                    net.PickupSync.HideClaimed(root);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        public static void DisableCryoAccess(PEN_Cryo x)
        {
            if (x == null) return;
            // Native PEN_Cryo.Open hides the "open" prompt by disabling the interaction's BoxCollider (Ghidra
            // PEN_Cryo.c Open); the Interactor finds prompts by collider, so a disabled Interaction alone kept
            // offering "open" to the peer who did not open it.
            try
            {
                var it = x.interaction;
                if (it != null)
                {
                    // The MethodInfo is GetComponent<BoxCollider2D>; disable a 3D box too in case of folding.
                    var box2 = it.GetComponent<BoxCollider2D>();
                    if (box2 != null) box2.enabled = false;
                    var box = it.GetComponent<BoxCollider>();
                    if (box != null) box.enabled = false;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                var lockGo = PuzzleDomainUtil.FindInParents<CryoDoorLock>(x.gameObject);
                if (lockGo == null)
                    lockGo = x.GetComponentInChildren<CryoDoorLock>(true);
                if (lockGo == null)
                {
                    var locks = WorldLookup.All<CryoDoorLock>();
                    if (locks != null)
                    {
                        for (int i = 0; i < locks.Length; i++)
                        {
                            var c = locks[i];
                            if (c == null || c.Door == null) continue;
                            PEN_Cryo linked = null;
                            try { linked = c.Door.GetComponent<PEN_Cryo>(); } catch (System.Exception e) { Guard.Swallow(e); }
                            if (linked == null)
                            {
                                try { linked = c.Door.GetComponentInChildren<PEN_Cryo>(true); } catch (System.Exception e) { Guard.Swallow(e); }
                            }
                            if (linked == null)
                                linked = PuzzleDomainUtil.FindInParents<PEN_Cryo>(c.Door);
                            if (linked == x)
                            {
                                lockGo = c;
                                break;
                            }
                        }
                    }
                }
                if (lockGo != null)
                {
                    try { lockGo.done = true; } catch (System.Exception e) { Guard.Swallow(e); }
                    try { PuzzleSyncService.DisableOne(lockGo.inter); } catch (System.Exception e) { Guard.Swallow(e); }
                    try { PuzzleSyncService.DisableInteractions(lockGo); } catch (System.Exception e) { Guard.Swallow(e); }
                    try
                    {
                        if (lockGo.Event != null)
                        {
                            PuzzleSyncService.DisableInteractions(lockGo.Event);
                            try { lockGo.Event.enabled = false; } catch (System.Exception e) { Guard.Swallow(e); }
                        }
                    }
                    catch (System.Exception e) { Guard.Swallow(e); }
                    if (lockGo.puzzle != null)
                        CodepadSyncService.DisablePad(lockGo.puzzle);
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                var pads = x.GetComponentsInChildren<PEN_Codepad>(true);
                if (pads != null)
                {
                    for (int i = 0; i < pads.Length; i++)
                        CodepadSyncService.DisablePad(pads[i]);
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                var parentPad = PuzzleDomainUtil.FindInParents<PEN_Codepad>(x.gameObject);
                if (parentPad != null)
                    CodepadSyncService.DisablePad(parentPad);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                var patterns = x.GetComponentsInChildren<LAB_PatternLock>(true);
                if (patterns != null)
                {
                    for (int i = 0; i < patterns.Length; i++)
                        CodepadSyncService.DisablePatternLock(patterns[i]);
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                var parentPat = PuzzleDomainUtil.FindInParents<LAB_PatternLock>(x.gameObject);
                if (parentPat != null)
                    CodepadSyncService.DisablePatternLock(parentPat);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            // Hierarchy-only: pads under this cryo, plus siblings that share cryo's parent.
            // (Replaces the old sqrMagnitude < 64f radius heuristic.)
            try
            {
                Transform cryoParent = null;
                try { cryoParent = x.transform.parent; } catch (System.Exception e) { Guard.Swallow(e); }
                var all = WorldLookup.All<LAB_PatternLock>();
                if (all != null)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        var p = all[i];
                        if (p == null) continue;
                        if (PuzzleDomainUtil.FindInParents<PEN_Cryo>(p.gameObject) == x)
                        {
                            CodepadSyncService.DisablePatternLock(p);
                            continue;
                        }
                        try
                        {
                            if (cryoParent != null && p.transform.parent == cryoParent)
                                CodepadSyncService.DisablePatternLock(p);
                        }
                        catch (System.Exception e) { Guard.Swallow(e); }
                    }
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        public static void PosePenCryoOpen(PEN_Cryo x)
        {
            if (x == null) return;
            StampPenCryoOpen(x);
            try
            {
                if (x.Door != null)
                {
                    var e = x.Door.localEulerAngles;
                    e.y = x.doorOpenAngle;
                    x.Door.localEulerAngles = e;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                if (x.cover != null)
                {
                    var e = x.cover.localEulerAngles;
                    e.y = x.coverOpenAngle;
                    x.cover.localEulerAngles = e;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                if (x.coverMover != null)
                {
                    var p = x.coverMover.localPosition;
                    p.y = x.moverYPos;
                    x.coverMover.localPosition = p;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                if (x.Opener != null)
                {
                    var p = x.Opener.localPosition;
                    p.y = x.openerPos;
                    x.Opener.localPosition = p;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try { if (x.scanner != null) x.scanner.enabled = false; } catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                if (x.fluid != null)
                {
                    var p = x.fluid.localPosition;
                    p.y = x.fluidLevel;
                    x.fluid.localPosition = p;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                if (x.Fog != null)
                {
                    for (int i = 0; i < x.Fog.Length; i++)
                    {
                        try { if (x.Fog[i] != null) x.Fog[i].Stop(true); } catch (System.Exception e) { Guard.Swallow(e); }
                    }
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                if (x.Steam != null)
                {
                    for (int i = 0; i < x.Steam.Length; i++)
                    {
                        try { if (x.Steam[i] != null) x.Steam[i].Stop(true); } catch (System.Exception e) { Guard.Swallow(e); }
                    }
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }
    }
}
