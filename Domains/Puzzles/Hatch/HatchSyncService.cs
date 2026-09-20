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

        public static void ApplySeilbahn(EXC_Seilbahn x, PuzzleStateEntry e, bool cinematic)
        {
            if (x != null && e.Bool0)
                SnapSeilbahn(x, cinematic);
        }

        public static void ApplyHatch(EXC_Hatch x, PuzzleStateEntry e, bool cinematic)
        {
            if (x != null && e.Bool0)
                SnapHatch(x, cinematic);
        }

        public static void SnapSeilbahn(EXC_Seilbahn x, bool play)
        {
            if (x == null) return;
            try { x.down = true; } catch { }
            try { if (x.interaction != null) x.interaction.SetActive(false); } catch { }
            try { if (x.Red != null) x.Red.SetActive(false); } catch { }
            try { if (x.Green != null) x.Green.SetActive(true); } catch { }
            if (play && PuzzleSyncService.TryStartWorldAnim(PuzzleType.EXC_Seilbahn, x.gameObject))
            {
                try { x.goDown(); }
                catch { }
            }
        }

        public static void SnapHatch(EXC_Hatch x, bool play)
        {
            if (x == null) return;
            try { if (x.Inter != null) x.Inter.SetActive(false); } catch { }
            try { if (x.Ladder != null) x.Ladder.SetActive(true); } catch { }
            try
            {
                if (x.doorway != null)
                    DoorNative.ApplyConnectedDoors(x.doorway, false);
            }
            catch { }
            if (play && PuzzleSyncService.TryStartWorldAnim(PuzzleType.EXC_Hatch, x.gameObject))
            {
                try { x.OpenHatch(); }
                catch { }
            }
        }
    }
}
