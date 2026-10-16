using UnityEngine;
using static SyncRADation.Networking.PuzzleDomainUtil;

namespace SyncRADation.Networking
{
    /// <summary>
    /// StorageBox lid pose only — box item blobs stay on StorageBoxSyncService.
    /// <para>StorageBox.open is an Unhollower property. It is per player and transient: native Open() (Ghidra
    /// StorageBox.c) sets open, puts the player in the box inventory (gameState 4 → goToBox) and clears open when they
    /// leave; triggered() refuses while open. So a peer's lid is presentation only: never the Open coroutine (it would
    /// pull the other player into the box screen) and never the flag (it would lock them out). Bool0 = lid open here,
    /// the own flag or a peer's posed lid, so reads converge with what was applied.</para>
    /// </summary>
    public sealed class StorageLidSyncService
    {
        // Open() lerps lid.localRotation = Euler(0, (1 - t) * -115, 0): identity open, -115 shut.
        const float ShutYaw = -115f;

        internal static PuzzleStateEntry Read(StorageBox x, long wid)
            => Mk(PuzzleType.StorageBox, wid, x.open || LidLooksOpen(x), false, false, 0, 0, 0, 0, 0);

        internal static void Apply(StorageBox x, PuzzleStateEntry e)
        {
            // The local player's own box session owns its lid.
            if (x == null || x.open || x.lid == null) return;
            if (e.Bool0)
                x.lid.localRotation = Quaternion.identity;
            else if (LidLooksOpen(x))
                x.lid.localRotation = Quaternion.Euler(0f, ShutYaw, 0f);
        }

        static bool LidLooksOpen(StorageBox x)
        {
            var lid = x.lid;
            return lid != null && Quaternion.Angle(lid.localRotation, Quaternion.identity) < -ShutYaw * 0.5f;
        }
    }
}
