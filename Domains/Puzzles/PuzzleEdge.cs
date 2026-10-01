using System;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    /// <summary>
    /// One rule for "solved" applies, so a join dump / held re-snap never skips the consequence:
    /// a live rising edge (was unsolved, this apply is a live peer change) runs the native onSolved;
    /// every other apply (full refresh, ReapplyHeld, repeat) runs only the idempotent durable consequence
    /// (door open, object SetActive) — never EventScreen / cutscene / coroutines.
    /// Read <c>wasSolved</c> BEFORE latching the native flag: the latch is what used to make later
    /// re-applies look like "already solved" and skip the door.
    /// <para>at: the puzzle component. A live edge for a puzzle outside the local player's room takes the durable
    /// form too: onSolved handlers start cutscenes / exitEvent, and CutscenePatches lets StartCutscene through
    /// while applying, which yanked a player in another room into the solver's cutscene.</para>
    /// </summary>
    internal static class PuzzleEdge
    {
        internal static void Solved(string site, bool wasSolved, bool nowSolved, Action durable, Action onLive = null,
            Component at = null)
        {
            if (!nowSolved) return;
            bool rising = !wasSolved && PuzzleSyncService.LiveEdge && (at == null || InRoom(at));
            Action run = rising && onLive != null ? onLive : durable;
            if (run == null) return;
            try { run(); }
            catch (Exception ex)
            {
                PuzzleSyncService.WarnOnce("edge-" + site, ex.Message);
                // A failed live consequence must still leave the world unblocked.
                if (run != durable && durable != null)
                {
                    try { durable(); }
                    catch (Exception ex2) { PuzzleSyncService.WarnOnce("edge-durable-" + site, ex2.Message); }
                }
            }
        }

        /// <summary>
        /// The local player is in the puzzle's room (its room-side screen anchor, since zoom-in stages sit away
        /// from their room) or zoomed into its screen.
        /// </summary>
        internal static bool InRoom(Component c)
        {
            if (c == null) return false;
            try
            {
                if (PuzzleFx.Viewing(c)) return true;
                return LocalInspect.InLocalRoom(PuzzleFx.Anchor(c).gameObject);
            }
            catch (Exception e) { Guard.Swallow(e); return false; }
        }
    }
}
