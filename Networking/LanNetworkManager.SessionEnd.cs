using System;

namespace SyncRADation.Networking
{
    /// <summary>Deliberate session end (a player quit to / reached the main menu). Story/Scene call this, not the net core.</summary>
    public sealed partial class LanNetworkManager
    {
        /// <summary>
        /// Host: tell every peer why (disconnect reason string, shown in their status line), then stop at the next
        /// Update after PollEvents. Client: stop synchronously so the native load that follows runs offline (a deferred
        /// stop would let the gated load be treated as a follow request). No-op when already offline.
        /// </summary>
        public void EndSession(string reason)
        {
            if (_role == NetworkRole.Offline) return;
            if (_role == NetworkRole.Host && _stopPending) return; // credits re-issue the load every frame: one notice is enough
            ModRuntime.Log?.Msg("[Network] session end: " + reason);
            if (_role == NetworkRole.Host)
            {
                foreach (var kvp in _peers)
                {
                    try { RejectPeer(kvp.Value, reason); }
                    catch (Exception e) { Guard.Swallow(e); }
                }
                RequestStop(reason);
                return;
            }
            _stopReason = reason ?? "";
            StopNetwork();
        }
    }
}
