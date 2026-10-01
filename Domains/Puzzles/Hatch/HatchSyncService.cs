using SyncRADation.Patches;
using UnityEngine;

namespace SyncRADation.Networking
{
    /// <summary>EXC_Hatch + EXC_Seilbahn snap/read/apply.</summary>
    public sealed class HatchSyncService
    {
        public static bool TryRead(PuzzleType type, Component c, long wid, out PuzzleStateEntry entry)
        {
            entry = default;
            switch (type)
            {
                case PuzzleType.EXC_Seilbahn:
                {
                    var x = (EXC_Seilbahn)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.down, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.EXC_Hatch:
                {
                    var x = (EXC_Hatch)c;
                    bool open = x.Ladder != null && x.Ladder.activeSelf;
                    entry = PuzzleDomainUtil.Mk(type, wid, open, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                default:
                    return false;
            }
        }

        /// <summary>
        /// goDown() is a toggle (Ghidra EXC_Seilbahn.c): down=false starts _goDown, down=true starts _goUp, and each
        /// coroutine latches down on its first step. Only a direction change rides, and down is never latched before
        /// the call (that made a peer's ride play the opposite direction and ping-pong). Both directions sync.
        /// </summary>
        public static void ApplySeilbahn(EXC_Seilbahn x, PuzzleStateEntry e, bool cinematic)
        {
            if (x == null) return;
            bool down;
            try { down = x.down; } catch (System.Exception ex) { Guard.Swallow(ex); return; }
            if (down == e.Bool0) return;
            bool active = false;
            try { active = x.gameObject.activeInHierarchy; } catch (System.Exception ex) { Guard.Swallow(ex); }
            // The ride shakes the screen and scrolls World for whoever runs it: only a player in that room rides along.
            if (cinematic && active && PuzzleEdge.InRoom(x))
            {
                PuzzleFx.Begin(x);
                try { x.goDown(); }
                catch (System.Exception ex) { Guard.Swallow(ex); }
                finally { PuzzleFx.End(); }
                bool now = down;
                try { now = x.down; } catch (System.Exception ex) { Guard.Swallow(ex); }
                if (now == e.Bool0) return;
            }
            PoseSeilbahn(x, e.Bool0);
        }

        public static void ApplyHatch(EXC_Hatch x, PuzzleStateEntry e, bool cinematic)
        {
            if (x != null && e.Bool0)
                SnapHatch(x, cinematic);
        }

        /// <summary>
        /// End pose of a ride without the coroutine (Ghidra EXC_Seilbahn.c _goDown / _goUp, absolute writes):
        /// down = BlockerU z -5.5, World x 170, BlockerD z 0; up = the mirror. Both rides end with the
        /// interaction on, Green on, Red off.
        /// </summary>
        static void PoseSeilbahn(EXC_Seilbahn x, bool down)
        {
            if (x == null) return;
            try { x.down = down; } catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                if (x.BlockerU != null)
                {
                    var p = x.BlockerU.localPosition;
                    p.z = down ? -5.5f : 0f;
                    x.BlockerU.localPosition = p;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                if (x.World != null)
                {
                    var p = x.World.localPosition;
                    p.x = down ? 170f : 0f;
                    x.World.localPosition = p;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                if (x.BlockerD != null)
                {
                    var p = x.BlockerD.localPosition;
                    p.z = down ? 0f : -5.5f;
                    x.BlockerD.localPosition = p;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try { if (x.interaction != null) x.interaction.SetActive(true); } catch (System.Exception e) { Guard.Swallow(e); }
            try { if (x.Red != null) x.Red.SetActive(false); } catch (System.Exception e) { Guard.Swallow(e); }
            try { if (x.Green != null) x.Green.SetActive(true); } catch (System.Exception e) { Guard.Swallow(e); }
        }

        public static void SnapHatch(EXC_Hatch x, bool play)
        {
            if (x == null) return;
            try { if (x.Inter != null) x.Inter.SetActive(false); } catch (System.Exception e) { Guard.Swallow(e); }
            try { if (x.Ladder != null) x.Ladder.SetActive(true); } catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                if (x.doorway != null)
                    DoorNative.ApplyConnectedDoors(x.doorway, false);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            if (play && PuzzleSyncService.TryStartWorldAnim(PuzzleType.EXC_Hatch, x.gameObject))
            {
                try { x.OpenHatch(); }
                catch (System.Exception e) { Guard.Swallow(e); }
            }
        }
    }
}
