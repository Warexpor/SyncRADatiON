using System;
using SyncRADation.Sync;
using static SyncRADation.Networking.PuzzleDomainUtil;

namespace SyncRADation.Networking
{
    /// <summary>ROT_Pipes valve / leak snap + read/apply.</summary>
    public sealed class PipesSyncService
    {
        internal static PuzzleStateEntry Read(ROT_Pipes x, long wid)
        {
            bool off = x.loaded || (x.Blockers != null && !x.Blockers.activeSelf);
            return Mk(PuzzleType.ROT_Pipes, wid, off, false, false, 0, 0, 0, 0, 0);
        }

        /// <summary>Live: the native TurnValve (once per scene); otherwise / on failure the valve-closed end pose.</summary>
        internal static void Apply(ROT_Pipes x, PuzzleStateEntry e)
        {
            if (x == null || !e.Bool0) return;
            x.loaded = true;
            if (PuzzleSyncService.LiveEdge && PuzzleSyncService.TryStartWorldAnim(PuzzleType.ROT_Pipes, x.gameObject))
            {
                try { x.TurnValve(); return; }
                catch (Exception ex) { Guard.Swallow("Puzzle.pipes-valve", ex); }
            }
            if (x.Blockers != null) x.Blockers.SetActive(false);
            if (x.interaction != null) x.interaction.SetActive(false);
            var leaks = x.leaks;
            if (leaks != null)
                for (int i = 0; i < leaks.Length; i++)
                    if (leaks[i] != null) leaks[i].Stop();
            var lights = x.lights;
            if (lights != null)
                for (int i = 0; i < lights.Length; i++)
                    if (lights[i] != null) lights[i].enabled = false;
            if (x.loopSFX != null) x.loopSFX.Stop();
        }
    }
}
