using UnityEngine;

namespace SyncRADation.Networking
{
    /// <summary>
    /// StorageBox lid pose only — box item blobs stay on StorageBoxSyncService.
    /// <para>StorageBox.open is an Unhollower property (the old reflection GetField("open") returned null, so the lid
    /// never synced). It is per player and transient: native Open() (Ghidra StorageBox.c) sets open, puts the player
    /// in the box inventory (gameState 4 → goToBox) and clears open when they leave; triggered() refuses while open.
    /// So a peer's lid is presentation only: never the Open coroutine (it would pull the other player into the box
    /// screen) and never the flag (it would lock them out). Bool0 = lid open here, the own flag or a peer's posed lid,
    /// so reads converge with what was applied.</para>
    /// </summary>
    public sealed class StorageLidSyncService
    {
        // Open() lerps lid.localRotation = Euler(0, (1 - t) * -115, 0): identity open, -115 shut.
        const float ShutYaw = -115f;

        public static bool TryRead(StorageBox x, long wid, out PuzzleStateEntry entry)
        {
            entry = default;
            if (x == null) return false;
            bool open = x.open || LidLooksOpen(x);
            entry = PuzzleDomainUtil.Mk(PuzzleType.StorageBox, wid, open, false, false, 0, 0, 0, 0, 0);
            return true;
        }

        public static void Apply(StorageBox x, PuzzleStateEntry e, bool cinematic)
        {
            if (x == null) return;
            // The local player's own box session owns its lid.
            bool local = false;
            try { local = x.open; } catch (System.Exception ex) { Guard.Swallow(ex); }
            if (local) return;
            try
            {
                if (x.lid == null) return;
                if (e.Bool0)
                    x.lid.localRotation = Quaternion.identity;
                else if (LidLooksOpen(x))
                    x.lid.localRotation = Quaternion.Euler(0f, ShutYaw, 0f);
            }
            catch (System.Exception ex) { Guard.Swallow(ex); }
        }

        static bool LidLooksOpen(StorageBox x)
        {
            try
            {
                var lid = x.lid;
                return lid != null && Quaternion.Angle(lid.localRotation, Quaternion.identity) < -ShutYaw * 0.5f;
            }
            catch (System.Exception e) { Guard.Swallow(e); return false; }
        }
    }
}
