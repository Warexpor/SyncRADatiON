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

        public static bool Host
        {
            get
            {
                var n = LanNetworkManager.Instance;
                return n != null && n.IsConnected && n.Role == NetworkRole.Host;
            }
        }

        public static bool Client
        {
            get
            {
                var n = LanNetworkManager.Instance;
                return n != null && n.IsConnected && n.Role == NetworkRole.Client;
            }
        }
    }
}
