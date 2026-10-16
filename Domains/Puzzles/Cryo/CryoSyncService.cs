using System;
using SyncRADation.Sync;
using UnityEngine;
using static SyncRADation.Networking.PuzzleDomainUtil;

namespace SyncRADation.Networking
{
    /// <summary>
    /// CryoDoorController / CryoDoorLock / PEN_Cryo snap/pose + OnEnable rematch. World result only: never EventScreen,
    /// which camera-locks the remote Elster.
    /// </summary>
    public sealed class CryoSyncService
    {
        readonly IPuzzleDomainHost _host;

        internal CryoSyncService(IPuzzleDomainHost host)
        {
            _host = host;
        }

        /// <summary>Native OnEnable re-enables pad / open prompt: shut them in the same callback if already solved.</summary>
        public void HandlePenCryoEnabled(PEN_Cryo x)
        {
            if (x == null) return;
            try
            {
                ulong id = WorldId.FromGameObject(x.gameObject);
                bool held = _host.IsHeld(PuzzleType.PEN_Cryo, id);
                if (!x.opened && !held && !_host.HeldUnmatched(PuzzleType.PEN_Cryo, id))
                    return;
                if (!held)
                    _host.RemapHeld(PuzzleType.PEN_Cryo, id);
                NetGate.BeginApply();
                try { SnapPenCryo(x, playOpen: false); }
                finally { NetGate.EndApply(); }
            }
            catch (Exception e) { Guard.Swallow(e); }
        }

        public void HandleCryoLockEnabled(CryoDoorLock c)
        {
            if (c == null) return;
            try
            {
                ulong id = WorldId.FromGameObject(c.gameObject);
                bool held = _host.IsHeld(PuzzleType.CryoDoorLock, id);
                bool done = c.done;
                if (!done && !held)
                {
                    if (c.puzzle != null && c.puzzle.solved)
                        done = true;
                    var pen = FindInParents<PEN_Cryo>(c.gameObject)
                        ?? (c.Door != null ? FindInParents<PEN_Cryo>(c.Door) : null);
                    if (pen != null && pen.opened) done = true;
                }
                if (!done && !held && !_host.HeldUnmatched(PuzzleType.CryoDoorLock, id))
                    return;
                if (!held)
                    _host.RemapHeld(PuzzleType.CryoDoorLock, id);
                NetGate.BeginApply();
                try { SnapCryoLock(c, playAnim: false); }
                finally { NetGate.EndApply(); }
            }
            catch (Exception e) { Guard.Swallow(e); }
        }

        internal static PuzzleStateEntry ReadController(CryoDoorController x, long wid)
            => Mk(PuzzleType.CryoDoorController, wid, x.open, false, false, 0, 0, 0, 0, 0);

        internal static PuzzleStateEntry ReadLock(CryoDoorLock x, long wid)
            => Mk(PuzzleType.CryoDoorLock, wid, x.done, false, false, 0, 0, 0, 0, 0);

        internal static PuzzleStateEntry ReadPenCryo(PEN_Cryo x, long wid)
            => Mk(PuzzleType.PEN_Cryo, wid, x.opened, false, false, 0, 0, 0, 0, 0);

        internal static void ApplyController(CryoDoorController x, PuzzleStateEntry e)
        {
            if (x == null || e.Bool0 == x.open) return;
            ToggleOrSet(x, e.Bool0);
        }

        internal static void ApplyLock(CryoDoorLock x, PuzzleStateEntry e)
        {
            if (x != null && e.Bool0)
                SnapCryoLock(x, PuzzleSyncService.LiveEdge);
        }

        internal static void ApplyPenCryo(PEN_Cryo x, PuzzleStateEntry e)
        {
            if (x != null && e.Bool0)
                SnapPenCryo(x, playOpen: PuzzleSyncService.LiveEdge);
        }

        /// <summary>toggleDoors animates the doors; when it throws, the flag still lands.</summary>
        static void ToggleOrSet(CryoDoorController ctrl, bool open)
        {
            try { ctrl.toggleDoors(); }
            catch (Exception e)
            {
                Guard.Swallow("Puzzle.cryo-toggle", e);
                ctrl.open = open;
            }
        }

        internal static void SnapCryoLock(CryoDoorLock c, bool playAnim = false)
        {
            if (c == null) return;
            PlaytestLog.Event("Puzzle", "snap CryoDoorLock " + c.gameObject.name + (playAnim ? " anim" : " pose"));
            c.done = true;
            if (c.puzzle != null)
                c.puzzle.solved = true;
            if (c.inter != null)
            {
                c.inter.triggered = true;
                c.inter.enabled = false;
            }
            if (c.Event != null)
            {
                PuzzleSyncService.DisableInteractions(c.Event);
                c.Event.enabled = false;
            }
            CodepadSyncService.DisablePad(c.puzzle);
            if (c.Door != null)
            {
                // Decompile CryoDoorLock.LoadState (done): Door.SetActive(true). Durable object state, so it runs on
                // the join dump too.
                c.Door.SetActive(true);
                PuzzleSyncService.UnlockDoorObject(c.Door);
                var ctrl = c.Door.GetComponent<CryoDoorController>() ?? c.Door.GetComponentInChildren<CryoDoorController>(true);
                if (ctrl != null && !ctrl.open)
                {
                    if (playAnim) ToggleOrSet(ctrl, true);
                    else ctrl.open = true;
                }
            }
            PuzzleSyncService.TryUnlockDoors(c.gameObject);
            var pen = c.GetComponent<PEN_Cryo>()
                ?? FindInParents<PEN_Cryo>(c.gameObject)
                ?? c.GetComponentInChildren<PEN_Cryo>(true);
            if (pen != null)
                SnapPenCryo(pen, playAnim);
        }

        internal static void SnapPenCryo(PEN_Cryo x, bool playOpen)
        {
            if (x == null) return;
            bool active = x.gameObject.activeInHierarchy;
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

            PuzzleSyncService.DisableOne(x.interaction);
            PuzzleSyncService.DisableInteractions(x);
            if (x.RoomDoor != null)
                PuzzleSyncService.UnlockDoorObject(x.RoomDoor);
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
                catch (Exception e)
                {
                    // The native open animation failed: land on its end pose instead.
                    Guard.Swallow("Puzzle.cryo-open", e);
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
            x.opened = true;
            x.doorPos = 1f;
            x.coverPos = 1f;
            x.moverPos = 1f;
            x.openerPos = 1f;
            x.fluidPos = 1f;
            x.brightness = 0f;
        }

        static void ActivateCryoContent(PEN_Cryo x)
        {
            if (x.contentLateActivated != null)
            {
                x.contentLateActivated.SetActive(true);
                PuzzleSyncService.RevealPickups(x.contentLateActivated);
            }
            HideClaimedAround(x.gameObject);
        }

        static void HideClaimedAround(GameObject root)
        {
            var net = LanNetworkManager.Instance;
            if (net != null)
                net.PickupSync.HideClaimed(root);
        }

        static void DisableCryoAccess(PEN_Cryo x)
        {
            // Native PEN_Cryo.Open hides the "open" prompt by disabling the interaction's BoxCollider (Ghidra
            // PEN_Cryo.c Open); the Interactor finds prompts by collider, so a disabled Interaction alone kept
            // offering "open" to the peer who did not open it.
            var it = x.interaction;
            if (it != null)
            {
                // The MethodInfo is GetComponent<BoxCollider2D>; disable a 3D box too in case of folding.
                var box2 = it.GetComponent<BoxCollider2D>();
                if (box2 != null) box2.enabled = false;
                var box = it.GetComponent<BoxCollider>();
                if (box != null) box.enabled = false;
            }
            var lockGo = FindInParents<CryoDoorLock>(x.gameObject) ?? x.GetComponentInChildren<CryoDoorLock>(true) ?? LockOpening(x);
            if (lockGo != null)
            {
                lockGo.done = true;
                PuzzleSyncService.DisableOne(lockGo.inter);
                PuzzleSyncService.DisableInteractions(lockGo);
                if (lockGo.Event != null)
                {
                    PuzzleSyncService.DisableInteractions(lockGo.Event);
                    lockGo.Event.enabled = false;
                }
                CodepadSyncService.DisablePad(lockGo.puzzle);
            }
            var pads = x.GetComponentsInChildren<PEN_Codepad>(true);
            if (pads != null)
                for (int i = 0; i < pads.Length; i++)
                    CodepadSyncService.DisablePad(pads[i]);
            CodepadSyncService.DisablePad(FindInParents<PEN_Codepad>(x.gameObject));
            var patterns = x.GetComponentsInChildren<LAB_PatternLock>(true);
            if (patterns != null)
                for (int i = 0; i < patterns.Length; i++)
                    CodepadSyncService.DisablePatternLock(patterns[i]);
            CodepadSyncService.DisablePatternLock(FindInParents<LAB_PatternLock>(x.gameObject));
            // Hierarchy only: pattern locks under this cryo, plus siblings that share the cryo's parent.
            Transform cryoParent = x.transform.parent;
            var all = WorldLookup.All<LAB_PatternLock>();
            if (all == null) return;
            for (int i = 0; i < all.Length; i++)
            {
                var p = all[i];
                if (p == null) continue;
                if (FindInParents<PEN_Cryo>(p.gameObject) == x || (cryoParent != null && p.transform.parent == cryoParent))
                    CodepadSyncService.DisablePatternLock(p);
            }
        }

        /// <summary>The CryoDoorLock whose Door is (or holds) this cryo.</summary>
        static CryoDoorLock LockOpening(PEN_Cryo x)
        {
            var locks = WorldLookup.All<CryoDoorLock>();
            if (locks == null) return null;
            for (int i = 0; i < locks.Length; i++)
            {
                var c = locks[i];
                if (c == null || c.Door == null) continue;
                var linked = c.Door.GetComponent<PEN_Cryo>()
                    ?? c.Door.GetComponentInChildren<PEN_Cryo>(true)
                    ?? FindInParents<PEN_Cryo>(c.Door);
                if (linked == x) return c;
            }
            return null;
        }

        static void PosePenCryoOpen(PEN_Cryo x)
        {
            StampPenCryoOpen(x);
            if (x.Door != null)
            {
                var e = x.Door.localEulerAngles;
                e.y = x.doorOpenAngle;
                x.Door.localEulerAngles = e;
            }
            if (x.cover != null)
            {
                var e = x.cover.localEulerAngles;
                e.y = x.coverOpenAngle;
                x.cover.localEulerAngles = e;
            }
            if (x.coverMover != null)
            {
                var p = x.coverMover.localPosition;
                p.y = x.moverYPos;
                x.coverMover.localPosition = p;
            }
            if (x.Opener != null)
            {
                var p = x.Opener.localPosition;
                p.y = x.openerPos;
                x.Opener.localPosition = p;
            }
            if (x.scanner != null) x.scanner.enabled = false;
            if (x.fluid != null)
            {
                var p = x.fluid.localPosition;
                p.y = x.fluidLevel;
                x.fluid.localPosition = p;
            }
            StopAll(x.Fog);
            StopAll(x.Steam);
        }

        static void StopAll(UnhollowerBaseLib.Il2CppReferenceArray<ParticleSystem> ps)
        {
            if (ps == null) return;
            for (int i = 0; i < ps.Length; i++)
                if (ps[i] != null) ps[i].Stop(true);
        }
    }
}
