using SyncRADation.Sync;
using UnityEngine;
using static SyncRADation.Networking.PuzzleDomainUtil;

namespace SyncRADation.Networking
{
    /// <summary>CentralElevator / call button / EXC_Elevator read/apply.</summary>
    public sealed class ElevatorSyncService
    {
        /// <summary>Int0 = floor, Int1 = state, Int2 = targetFloor (cabin authority).</summary>
        internal static PuzzleStateEntry ReadCentral(CentralElevatorControl x, long wid)
            => Mk(PuzzleType.CentralElevator, wid, false, false, false, x.floor, (int)x.state, x.targetFloor, 0, 0);

        internal static PuzzleStateEntry ReadCallButton(ElevatorCallButton x, long wid)
            => Mk(PuzzleType.ElevatorCallButton, wid, x.called, false, false, 0, 0, 0, 0, 0);

        /// <summary>
        /// Native Update scrolls pos.z (Repeat over distance) and writes mover.localPosition = pos every frame (Ghidra
        /// EXC_Elevator.c); mover Y never changes. Bool0 = riding, Bool1 = stopped, Float0 = pos.z where the cabin
        /// stopped. While riding it stays 0: each peer scrolls its own shaft, and a moving float would resend every
        /// poll and snap the shaft backwards by the latency.
        /// </summary>
        internal static PuzzleStateEntry ReadExc(EXC_Elevator x, long wid)
        {
            bool riding = x.riding, stopped = x.stopped;
            float stopZ = stopped && !riding ? x.pos.z : 0f;
            return Mk(PuzzleType.EXC_Elevator, wid, riding, stopped, false, 0, 0, 0, 0, stopZ);
        }

        internal static void ApplyCentral(CentralElevatorControl x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.floor = e.Int0;
            x.state = (CentralElevatorControl.evState)e.Int1;
            x.targetFloor = e.Int2;
        }

        /// <summary>
        /// A rising edge runs native CallElevator (starts elevatorMove / elevatorBroken): a bare called=true skips the
        /// coroutine, the host cabin never moves and later presses early-out.
        /// </summary>
        internal static void ApplyCallButton(ElevatorCallButton x, PuzzleStateEntry e)
        {
            if (x == null) return;
            bool wasCalled = x.called;
            if (e.Bool0 && !wasCalled)
                PuzzleDomainUtil.Native("elevator-call", x.CallElevator);
            else if (!e.Bool0 && wasCalled)
                x.called = false;
        }

        /// <summary>Stopped: the host's pos.z, then native stopInstant re-reads it from the mover. Riding: start the native ride once.</summary>
        internal static void ApplyExc(EXC_Elevator x, PuzzleStateEntry e)
        {
            if (x == null) return;
            bool wasRiding = x.riding;
            x.riding = e.Bool0;
            x.stopped = e.Bool1;
            if (e.Bool1 && !e.Bool0)
            {
                // pos.z drives the mover (Update: mover.localPosition = pos); write both so the pose holds.
                var p = x.pos;
                p.z = e.Float0;
                x.pos = p;
                if (x.mover != null) x.mover.localPosition = p;
                try { x.stopInstant(); }
                catch (System.Exception ex)
                {
                    Guard.Swallow("Puzzle.elevator-stop", ex);
                    x.acc = 0f;
                }
                return;
            }
            if (e.Bool0 && !wasRiding)
                PuzzleDomainUtil.Native("elevator-ride", x.startRide);
        }
    }
}
