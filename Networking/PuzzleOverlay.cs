// Puzzle overlay kill keyed by WorldId (spent pads / cryo family).
using UnityEngine;

namespace SyncRADation.Networking
{
    public static class PuzzleOverlay
    {
        public static bool ShouldKill(Interaction it)
        {
            var net = LanNetworkManager.Instance;
            if (net == null) return false;
            return net.PuzzleSync.ShouldKillOverlay(it);
        }
    }
}
