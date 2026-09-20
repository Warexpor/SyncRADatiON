using SyncRADation.Patches;
using UnityEngine;

namespace SyncRADation.Networking
{
    /// <summary>
    /// MED_Pump / MED_FloodedBathroom + flood control switches — snap/read/apply.
    /// </summary>
    public sealed class PumpFloodSyncService
    {
        public static bool TryRead(PuzzleType type, Component c, long wid, out PuzzleStateEntry entry)
        {
            entry = default;
            switch (type)
            {
                case PuzzleType.MED_Pump:
                {
                    var x = (MED_Pump)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.solved, false, false, x.a, x.b, x.c, 0, 0);
                    return true;
                }
                case PuzzleType.MED_FloodedBathroom:
                {
                    var x = (MED_FloodedBathroom)c;
                    bool drained = x.Ladder != null && x.Ladder.activeSelf;
                    entry = PuzzleDomainUtil.Mk(type, wid, drained, false, false, 0, 0, 0, 0, x.level);
                    return true;
                }
                case PuzzleType.FloodControlSwitch:
                {
                    var x = (FloodControlSwitch)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.state, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.FloodControls:
                {
                    var x = (FloodControls)c;
                    int codeBits = 0;
                    try
                    {
                        var code = x.code;
                        if (code != null && code.Length <= 32)
                            codeBits = ResidencyPuzzleSyncService.PackBoolArray(code);
                    }
                    catch { }
                    entry = PuzzleDomainUtil.Mk(type, wid, x.done, x.locked, false, codeBits, 0, 0, 0, 0);
                    return true;
                }
                default:
                    return false;
            }
        }

        public static void ApplyPump(MED_Pump x, PuzzleStateEntry e, bool cinematic)
        {
            if (x == null) return;
            try { x.a = e.Int0; x.b = e.Int1; x.c = e.Int2; } catch { }
            if (e.Bool0)
                SnapMedPump(x, cinematic);
        }

        public static void ApplyFlood(MED_FloodedBathroom x, PuzzleStateEntry e, bool cinematic)
        {
            if (x != null && e.Bool0)
                SnapFlood(x, cinematic);
        }

        public static void ApplyFloodSwitch(FloodControlSwitch x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.state = e.Bool0;
            if (e.Bool0) PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        public static void ApplyFloodControls(FloodControls x, PuzzleStateEntry e, bool mutateWorld)
        {
            if (x == null) return;
            x.done = e.Bool0;
            x.locked = e.Bool1;
            try
            {
                var code = x.code;
                if (code != null && code.Length <= 32)
                    ResidencyPuzzleSyncService.UnpackBoolArray(code, e.Int0);
            }
            catch { }
            if (!e.Bool0) return;
            // dlc.locked is a flag snap — apply on join FullRefresh too.
            try { if (x.dlc != null) x.dlc.locked = false; } catch { }
            if (mutateWorld)
                PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        public static void SnapMedPump(MED_Pump x, bool play)
        {
            if (x == null) return;
            try { x.solved = true; } catch { }
            try
            {
                if (x.flood != null)
                    SnapFlood(x.flood, play);
            }
            catch { }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
        }

        public static void SnapFlood(MED_FloodedBathroom x, bool play)
        {
            if (x == null) return;
            if (play && PuzzleSyncService.TryStartWorldAnim(PuzzleType.MED_FloodedBathroom, x.gameObject))
            {
                try { x.Drain(); }
                catch { PoseFlood(x); }
            }
            else
                PoseFlood(x);
        }

        static void PoseFlood(MED_FloodedBathroom x)
        {
            if (x == null) return;
            try { x.setLevel(x.endDepth); } catch { }
            try { x.level = x.endDepth; } catch { }
            try
            {
                if (x.waterTrans != null)
                {
                    var p = x.waterTrans.localPosition;
                    p.y = x.endDepth;
                    x.waterTrans.localPosition = p;
                }
            }
            catch { }
            try { if (x.Ladder != null) x.Ladder.SetActive(true); } catch { }
            try { if (x.ObservationFlood != null) x.ObservationFlood.SetActive(false); } catch { }
        }
    }
}
