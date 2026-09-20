using UnityEngine;

namespace SyncRADation.Networking
{
    /// <summary>ROT_Pipes valve / leak snap + read/apply.</summary>
    public sealed class PipesSyncService
    {
        public static bool TryRead(ROT_Pipes x, long wid, out PuzzleStateEntry entry)
        {
            entry = default;
            if (x == null) return false;
            bool off = x.loaded || (x.Blockers != null && !x.Blockers.activeSelf);
            entry = PuzzleDomainUtil.Mk(PuzzleType.ROT_Pipes, wid, off, false, false, 0, 0, 0, 0, 0);
            return true;
        }

        public static void Apply(ROT_Pipes x, PuzzleStateEntry e, bool cinematic)
        {
            if (x != null && e.Bool0)
                SnapPipes(x, cinematic);
        }

        public static void SnapPipes(ROT_Pipes x, bool play)
        {
            if (x == null) return;
            try { x.loaded = true; } catch { }
            if (play && PuzzleSyncService.TryStartWorldAnim(PuzzleType.ROT_Pipes, x.gameObject))
            {
                try { x.TurnValve(); }
                catch { PosePipes(x); }
            }
            else
                PosePipes(x);
        }

        static void PosePipes(ROT_Pipes x)
        {
            if (x == null) return;
            try { if (x.Blockers != null) x.Blockers.SetActive(false); } catch { }
            try { if (x.interaction != null) x.interaction.SetActive(false); } catch { }
            try
            {
                var leaks = x.leaks;
                if (leaks != null)
                {
                    for (int i = 0; i < leaks.Length; i++)
                    {
                        try { if (leaks[i] != null) leaks[i].Stop(); } catch { }
                    }
                }
            }
            catch { }
            try
            {
                var lights = x.lights;
                if (lights != null)
                {
                    for (int i = 0; i < lights.Length; i++)
                    {
                        try { if (lights[i] != null) lights[i].enabled = false; } catch { }
                    }
                }
            }
            catch { }
            try { if (x.loopSFX != null) x.loopSFX.Stop(); } catch { }
        }
    }
}
