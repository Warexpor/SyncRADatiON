using System.Collections.Generic;

namespace SyncRADation.Networking
{
    /// <summary>
    /// Transitional one-line forwards onto domain NetHandlers, kept only while Puzzles call sites still use them
    /// (PuzzleSyncService). Everything else calls the owning handler directly (net.DoorHandlers.SendDoorState(...),
    /// net.SessionHandlers...); delete a forward once its last caller has moved.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        public void SendPuzzleState(IList<PuzzleStateEntry> entries, bool fullRefresh, int exceptPlayerId = -1) =>
            PuzzleHandlers.SendPuzzleState(entries, fullRefresh, exceptPlayerId);

        public void RequestWorldSnapshot() => SessionHandlers.RequestWorldSnapshot();
    }
}
