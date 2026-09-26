using UnityEngine;

namespace SyncRADation.Networking
{
    /// <summary>
    /// RadioStationTutorial / ROT_RadioAlignment / DET_RadioCodeLock / RadioManagerState.
    /// </summary>
    public sealed class RadioPuzzleSyncService
    {
        public static bool TryRead(PuzzleType type, Component c, long wid, out PuzzleStateEntry entry)
        {
            entry = default;
            switch (type)
            {
                case PuzzleType.RadioStationTutorial:
                {
                    var x = (RadioStationTutorialPuzzle)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.solved, false, false, 0, 0, 0, 0, 0);
                    return true;
                }
                case PuzzleType.ROT_RadioAlignment:
                {
                    var x = (ROT_RadioAlignment)c;
                    entry = PuzzleDomainUtil.Mk(type, wid, x.east, false, false, x.correctAntenna, x.setAntenna, 0, 0, x.QualityE);
                    return true;
                }
                case PuzzleType.DET_RadioCodeLock:
                {
                    var x = (DET_RadioCodeLock)c;
                    bool padSolved = false;
                    try
                    {
                        if (x.keypad != null)
                            padSolved = x.keypad.solved || x.keypad.opening;
                    }
                    catch { }
                    // Bool0=keypad solved — Apply snaps pad + TryUnlockDoors (door side-effect).
                    entry = PuzzleDomainUtil.Mk(type, wid, padSolved, false, false, x.frequency, x.code, x.hintStation, 0, 0);
                    return true;
                }
                default:
                    return false;
            }
        }

        /// <summary>Host-only global (WorldId 0).</summary>
        public static PuzzleStateEntry ReadManagerState()
        {
            int radioBools = 0;
            try
            {
                if (RadioManager.moduleInstalled) radioBools |= 1;
            }
            catch { }
            return PuzzleDomainUtil.Mk(PuzzleType.RadioManagerState, 0, radioBools != 0, false, false, radioBools, 0, 0, 0, 0f);
        }

        public static void ApplyTutorial(RadioStationTutorialPuzzle x, PuzzleStateEntry e)
        {
            if (x != null) x.solved = e.Bool0;
        }

        public static void ApplyManager(PuzzleStateEntry e)
        {
            // Latch true only — host false must not wipe a peer who just acquired the module.
            if ((e.Int0 & 1) != 0)
                RadioManager.moduleInstalled = true;
        }

        public static void ApplyAlignment(ROT_RadioAlignment x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.east = e.Bool0;
            x.correctAntenna = e.Int0;
            x.setAntenna = e.Int1;
            x.QualityE = e.Float0;
            try { x.LoadState(); } catch { }
        }

        public static void ApplyCode(DET_RadioCodeLock x, PuzzleStateEntry e)
        {
            if (x == null) return;
            x.frequency = e.Int0;
            x.code = e.Int1;
            x.hintStation = e.Int2;
            if (!e.Bool0) return;
            try
            {
                if (x.keypad != null)
                {
                    x.keypad.solved = true;
                    try { x.keypad.opening = true; } catch { }
                }
            }
            catch { }
            PuzzleSyncService.TryUnlockDoors(x.gameObject);
            try
            {
                if (x.keypad != null)
                    PuzzleSyncService.TryUnlockDoors(x.keypad.gameObject);
            }
            catch { }
        }
    }
}
