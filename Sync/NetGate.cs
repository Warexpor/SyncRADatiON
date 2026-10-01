// Re-entrancy + role helpers for Harmony prefixes that must not fight host apply.
using SyncRADation.Networking;

namespace SyncRADation.Sync
{
    public static class NetGate
    {
        // Re-entrancy counter; cleared by SessionReset ("NetGate", connection scope) and StopNetwork.
        private static int _applying;

        public static bool IsApplying => _applying > 0;

        public static void BeginApply() => _applying++;

        // Warn-once flag (per process): an unbalanced EndApply is a code bug, one line is enough to find it.
        private static bool _underflowLogged;

        public static void EndApply()
        {
            if (_applying > 0)
            {
                _applying--;
                return;
            }
            if (_underflowLogged) return;
            _underflowLogged = true;
            PlaytestLog.Warn("Net", "NetGate.EndApply without a matching BeginApply (unbalanced apply scope, or a reset ran inside one): "
                + System.Environment.StackTrace);
        }

        public static void Reset()
        {
            _applying = 0;
            ApplySender = -1;
        }

        /// <summary>
        /// Host: the client whose packet is being applied right now (-1 when none). That client already ran the
        /// action natively, so side effects relayed from this apply (emitter sounds) skip it.
        /// </summary>
        public static int ApplySender = -1;

        public static bool Live
        {
            get
            {
                var n = LanNetworkManager.Instance;
                return n != null && n.IsConnected;
            }
        }

        /// <summary>
        /// Live AND at least one remote peer finished the handshake (host: a client; client: the host).
        /// A host with nobody connected is vanilla: gate every suppressive / dedupe branch on this, not on Live.
        /// </summary>
        public static bool Party
        {
            get
            {
                var n = LanNetworkManager.Instance;
                return n != null && n.IsConnected && n.HasReadyPeers;
            }
        }

        /// <summary>Live host (handshaken session, peers or not).</summary>
        public static bool Host
        {
            get
            {
                var n = LanNetworkManager.Instance;
                return n != null && n.IsConnected && n.Role == NetworkRole.Host;
            }
        }

        /// <summary>Live client (host handshake done).</summary>
        public static bool Client
        {
            get
            {
                var n = LanNetworkManager.Instance;
                return n != null && n.IsConnected && n.Role == NetworkRole.Client;
            }
        }

        // ---- Raw transport role (no handshake requirement). Use inside packet handlers / teardown paths where the
        // role matters but IsConnected may already be false (StopNetwork clears the handshake before the role), and for UI.

        /// <summary>Transport running in any role: hosting, connecting or connected (Role != Offline).</summary>
        public static bool Active
        {
            get
            {
                var n = LanNetworkManager.Instance;
                return n != null && n.Role != NetworkRole.Offline;
            }
        }

        /// <summary>Role == Host (also true during teardown before the role drops to Offline).</summary>
        public static bool HostRole
        {
            get
            {
                var n = LanNetworkManager.Instance;
                return n != null && n.Role == NetworkRole.Host;
            }
        }

        /// <summary>Role == Client, including a client still connecting / handshaking.</summary>
        public static bool ClientRole
        {
            get
            {
                var n = LanNetworkManager.Instance;
                return n != null && n.Role == NetworkRole.Client;
            }
        }

        /// <summary>Host or offline / solo: this install owns the world (not a client in any phase). Same as !ClientRole.</summary>
        public static bool WorldOwner => !ClientRole;
    }
}
