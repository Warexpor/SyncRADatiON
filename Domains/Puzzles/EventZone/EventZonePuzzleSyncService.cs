using SyncRADation.Patches;
using SyncRADation.Sync;
using static SyncRADation.Networking.PuzzleDomainUtil;

namespace SyncRADation.Networking
{
    /// <summary>
    /// EventZoneTriggered flag + in-room Invoke (host-authored; cutscene / dialogue stay with Story). An observer in
    /// another room never Invokes (it would yank them through a traverse they never started); never EventScreen /
    /// Interaction.trigger().
    /// </summary>
    public sealed class EventZonePuzzleSyncService
    {
        internal static PuzzleStateEntry Read(EventZone x, long wid)
            => Mk(PuzzleType.EventZoneTriggered, wid, x.triggered, false, false, 0, 0, 0, 0, 0);

        internal static void Apply(EventZone x, PuzzleStateEntry e)
        {
            if (x == null) return;
            bool was = x.triggered;
            x.triggered = e.Bool0;
            if (!e.Bool0 || was) return;
            EventZonePatch.MarkFired(unchecked((ulong)e.WorldId));
            bool inRoom = LocalInspect.InLocalRoom(x.gameObject);
            if (!PuzzleSyncService.LiveEdge || !inRoom)
                PlaytestLog.Verbose("Puzzle", "skip EventZone invoke " + x.gameObject.name);
            PuzzleEdge.Solved("EventZone", was, true, durable: null,
                onLive: () => { if (x.onInRange != null) x.onInRange.Invoke(); }, inRoom: inRoom);
        }
    }
}
