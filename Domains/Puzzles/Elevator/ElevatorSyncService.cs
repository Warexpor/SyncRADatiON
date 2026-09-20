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
                    float moverY = 0f;
                    try
                    {
                        if (x.mover != null)
                            moverY = x.mover.localPosition.y;
                    }
                    catch { }
                    // Bool0=riding Bool1=stopped Float0=mover Y (late-join / mid-ride pose).
                    entry = PuzzleDomainUtil.Mk(type, wid, x.riding, x.stopped, false, 0, 0, 0, 0, moverY);
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
            try { x.targetFloor = e.Int2; } catch { }
        }

        public static void ApplyCallButton(ElevatorCallButton x, PuzzleStateEntry e)
        {
            if (x != null) x.called = e.Bool0;
        }

        public static void ApplyExc(EXC_Elevator x, PuzzleStateEntry e)
        {
            if (x == null) return;
            bool wasRiding = false;
            try { wasRiding = x.riding; } catch { }
            x.riding = e.Bool0;
            x.stopped = e.Bool1;
            // Prefer final stopped pose; when riding, start native ride once.
            if (e.Bool1)
            {
                try { x.stopInstant(); }
                catch
                {
                    try
                    {
                        if (x.mover != null)
                        {
                            var p = x.mover.localPosition;
                            p.y = x.distance;
                            x.mover.localPosition = p;
                        }
                    }
                    catch { }
                }
                return;
            }
            if (e.Bool0 && !wasRiding)
            {
                try { x.startRide(); }
                catch { }
            }
            // Late-join / mid-ride: snap mover Y when provided.
            if (e.Float0 != 0f || e.Bool0)
            {
                try
                {
                    if (x.mover != null)
                    {
                        var p = x.mover.localPosition;
                        p.y = e.Float0;
                        x.mover.localPosition = p;
                    }
                }
                catch { }
            }
        }
    }
}
