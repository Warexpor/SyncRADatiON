// Re-entrancy + role helpers for Harmony prefixes that must not fight host apply.
using SyncRADation.Networking;

namespace SyncRADation.Sync
{
    public static class NetGate
    {
        private static int _applying;

        public static bool IsApplying => _applying > 0;

        public static void BeginApply() => _applying++;

        public static void EndApply()
        {
            if (_applying > 0) _applying--;
        }

        public static bool Live
        {
            get
            {
                var n = LanNetworkManager.Instance;
                return n != null && n.IsConnected;
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
