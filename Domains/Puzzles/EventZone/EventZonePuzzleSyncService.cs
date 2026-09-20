using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    /// <summary>
    /// EventZoneTriggered flag + in-room Invoke. Not cutscene/dialogue (Story owns those).
    /// Respects InLocalRoom + MutateWorld — never EventScreen / Interaction.trigger().
    /// </summary>
    public sealed class EventZonePuzzleSyncService
    {
        public static bool TryRead(EventZone x, long wid, out PuzzleStateEntry entry)
        {
            entry = default;
            if (x == null) return false;
            entry = PuzzleDomainUtil.Mk(PuzzleType.EventZoneTriggered, wid, x.triggered, false, false, 0, 0, 0, 0, 0);
            return true;
        }

        public static void Apply(EventZone x, PuzzleStateEntry e, bool mutateWorld)
        {
            if (x == null) return;
            bool was = false;
            try { was = x.triggered; } catch { }
            x.triggered = e.Bool0;
            if (!(e.Bool0 && !was)) return;

            SyncRADation.Patches.EventZonePatch.MarkFired(unchecked((ulong)e.WorldId));
            if (mutateWorld && LocalInspect.InLocalRoom(x.gameObject))
            {
                try { if (x.onInRange != null) x.onInRange.Invoke(); } catch { }
            }
            else
                PlaytestLog.Verbose("Puzzle", "skip EventZone invoke " + x.gameObject.name);
        }
    }
}
