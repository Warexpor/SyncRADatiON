using System;
using SyncRADation.Sync;
using UnityEngine;
using static SyncRADation.Networking.PuzzleDomainUtil;

namespace SyncRADation.Networking
{
    /// <summary>EXC_Hatch + EXC_Seilbahn snap/read/apply.</summary>
    public sealed class HatchSyncService
    {
        internal static PuzzleStateEntry ReadSeilbahn(EXC_Seilbahn x, long wid)
            => Mk(PuzzleType.EXC_Seilbahn, wid, x.down, false, false, 0, 0, 0, 0, 0);

        internal static PuzzleStateEntry ReadHatch(EXC_Hatch x, long wid)
        {
            bool open = x.Ladder != null && x.Ladder.activeSelf;
            return Mk(PuzzleType.EXC_Hatch, wid, open, false, false, 0, 0, 0, 0, 0);
        }

        /// <summary>
        /// goDown() is a toggle (Ghidra EXC_Seilbahn.c): down=false starts _goDown, down=true starts _goUp, and each
        /// coroutine latches down on its first step. Only a direction change rides, and down is never latched before
        /// the call (that made a peer's ride play the opposite direction and ping-pong). Both directions sync. The ride
        /// shakes the screen and scrolls World for whoever runs it: only a player in that room rides along.
        /// </summary>
        internal static void ApplySeilbahn(EXC_Seilbahn x, PuzzleStateEntry e)
        {
            if (x == null || x.down == e.Bool0) return;
            if (PuzzleSyncService.LiveEdge && x.gameObject.activeInHierarchy && PuzzleEdge.InRoom(x))
            {
                PuzzleFx.Begin(x);
                try { x.goDown(); }
                catch (Exception ex) { Guard.Swallow("Puzzle.seilbahn-ride", ex); }
                finally { PuzzleFx.End(); }
                if (x.down == e.Bool0) return;
            }
            PoseSeilbahn(x, e.Bool0);
        }

        internal static void ApplyHatch(EXC_Hatch x, PuzzleStateEntry e)
        {
            if (x == null || !e.Bool0) return;
            if (x.Inter != null) x.Inter.SetActive(false);
            if (x.Ladder != null) x.Ladder.SetActive(true);
            if (x.doorway != null)
                DoorNative.ApplyConnectedDoors(x.doorway, false);
            if (PuzzleSyncService.LiveEdge && PuzzleSyncService.TryStartWorldAnim(PuzzleType.EXC_Hatch, x.gameObject))
                Native("hatch-open", x.OpenHatch);
        }

        /// <summary>
        /// End pose of a ride without the coroutine (Ghidra EXC_Seilbahn.c _goDown / _goUp, absolute writes):
        /// down = BlockerU z -5.5, World x 170, BlockerD z 0; up = the mirror. Both rides end with the
        /// interaction on, Green on, Red off.
        /// </summary>
        static void PoseSeilbahn(EXC_Seilbahn x, bool down)
        {
            x.down = down;
            if (x.BlockerU != null)
            {
                var p = x.BlockerU.localPosition;
                p.z = down ? -5.5f : 0f;
                x.BlockerU.localPosition = p;
            }
            if (x.World != null)
            {
                var p = x.World.localPosition;
                p.x = down ? 170f : 0f;
                x.World.localPosition = p;
            }
            if (x.BlockerD != null)
            {
                var p = x.BlockerD.localPosition;
                p.z = down ? 0f : -5.5f;
                x.BlockerD.localPosition = p;
            }
            if (x.interaction != null) x.interaction.SetActive(true);
            if (x.Red != null) x.Red.SetActive(false);
            if (x.Green != null) x.Green.SetActive(true);
        }
    }
}
