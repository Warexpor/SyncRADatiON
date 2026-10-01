using UnityEngine;

namespace SyncRADation.Networking
{
    /// <summary>CentralElevator / call button / EXC_Elevator snap/read/apply.</summary>
    public sealed class ElevatorSyncService
    {
        public static bool TryRead(PuzzleType type, Component c, long wid, out PuzzleStateEntry entry)
        {
            entry = default;
            switch (type)
            {
                case PuzzleType.CentralElevator:
                {
                    var x = (CentralElevatorControl)c;
                    // Int0=floor Int1=state Int2=targetFloor (cabin authority).
                    entry = PuzzleDomainUtil.Mk(type, wid, false, false, false, x.floor, (int)x.state, x.targetFloor, 0, 0);
                    return true;
                }
                case PuzzleType.ElevatorCallButton:
                {
                    var x = (ElevatorCallButton)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.called, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.EXC_Elevator:
                {
                    var x = (EXC_Elevator)c;
                    // Native Update scrolls pos.z (Repeat over distance) and writes mover.localPosition = pos every
                    // frame (Ghidra EXC_Elevator.c); mover Y never changes. Float0 = pos.z where the cabin stopped.
                    // While riding it stays 0: each peer scrolls its own shaft, and a moving float would resend
                    // every poll and snap the shaft backwards by the latency.
                    float stopZ = 0f;
                    bool riding = x.riding, stopped = x.stopped;
                    if (stopped && !riding)
                    {
                        try { stopZ = x.pos.z; } catch (System.Exception e) { Guard.Swallow(e); }
                    }
                    // Bool0=riding Bool1=stopped Float0=stopped pos.z.
                    entry = PuzzleDomainUtil.Mk(type, wid, riding, stopped, false, 0, 0, 0, 0, stopZ);
                    return true;
                }
                default:
                    return false;
            }
        }

        public static void ApplyCentral(CentralElevatorControl x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.floor = e.Int0;
            x.state = (CentralElevatorControl.evState)e.Int1;
            try { x.targetFloor = e.Int2; } catch (System.Exception ex) { Guard.Swallow(ex); }
        }

        public static void ApplyCallButton(ElevatorCallButton x, PuzzleStateEntry e)
        {
            if (x == null) return;
            // Rising edge must invoke native CallElevator (starts elevatorMove / elevatorBroken).
            // Bare called=true skips the coroutine → host cabin never moves; later presses early-out.
            bool wasCalled = false;
            try { wasCalled = x.called; } catch (System.Exception ex) { Guard.Swallow(ex); }
            if (e.Bool0 && !wasCalled)
            {
                try { x.CallElevator(); }
                catch (System.Exception ex) { Guard.Swallow(ex); }
            }
            else if (!e.Bool0 && wasCalled)
            {
                x.called = false;
            }
        }

        public static void ApplyExc(EXC_Elevator x, PuzzleStateEntry e)
        {
            if (x == null) return;
            bool wasRiding = false;
            try { wasRiding = x.riding; } catch (System.Exception ex) { Guard.Swallow(ex); }
            x.riding = e.Bool0;
            x.stopped = e.Bool1;
            // Prefer final stopped pose (the host's pos.z, then native stopInstant re-reads it from the mover);
            // when riding, start native ride once.
            if (e.Bool1 && !e.Bool0)
            {
                SnapExcStop(x, e.Float0);
                try { x.stopInstant(); }
                catch (System.Exception ex)
                {
                    Guard.Swallow(ex);
                    try { x.acc = 0f; } catch (System.Exception ex2) { Guard.Swallow(ex2); }
                }
                return;
            }
            if (e.Bool0 && !wasRiding)
            {
                try { x.startRide(); }
                catch (System.Exception ex) { Guard.Swallow(ex); }
            }
        }

        /// <summary>pos.z drives the mover (Update: mover.localPosition = pos); write both so the pose holds.</summary>
        static void SnapExcStop(EXC_Elevator x, float z)
        {
            try
            {
                var p = x.pos;
                p.z = z;
                x.pos = p;
                if (x.mover != null) x.mover.localPosition = p;
            }
            catch (System.Exception ex) { Guard.Swallow(ex); }
        }
    }
}
