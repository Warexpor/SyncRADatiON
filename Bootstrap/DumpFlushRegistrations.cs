// Flush-before-unicast hooks (Sync/DumpFlush), registered once at boot (ModRuntime.Start). Before a join / resync dump to
// one peer while other peers are ready, SessionNetHandlers runs every hook so a diff still waiting for its next broadcast
// reaches everyone, not only the joiner. One line per domain; each hook sends to all peers and is a no-op when clean.
using SyncRADation.Networking;
using SyncRADation.Sync;

namespace SyncRADation
{
    internal static class DumpFlushRegistrations
    {
        // persistent: boot-once guard
        private static bool _done;

        public static void RegisterAll()
        {
            if (_done) return;
            _done = true;

            // Story: dirty-key StoryCommit diff.
            DumpFlush.Register("Story", () => LanNetworkManager.Instance?.StorySync.FlushDiffNow());
            // Doors: pending open/lock diffs.
            DumpFlush.Register("Doors", DoorSyncService.FlushDiffNow);
            // Shared storage box: blob when its signature changed.
            DumpFlush.Register("Storage", () => LanNetworkManager.Instance?.StorageSync.FlushDiffNow());
            // Puzzles: read + send the pending diff now.
            DumpFlush.Register("Puzzles", () => LanNetworkManager.Instance?.PuzzleSync.FlushDiffNow());
            // World pickups: pending claim/hide diffs.
            DumpFlush.Register("Pickups", () => LanNetworkManager.Instance?.PickupSync.FlushDiffNow());
        }
    }
}
