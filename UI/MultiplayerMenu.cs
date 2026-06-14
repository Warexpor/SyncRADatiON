using SyncRADation.Config;
using SyncRADation.Networking;
using UnityEngine;

namespace SyncRADation.UI
{
    public static class MultiplayerMenu
    {
        private static bool _showMenu;
        private static string _address = "127.0.0.1";
        private static int _port = PluginInfo.DefaultPort;
        private static Rect _windowRect = new Rect(100f, 100f, 320f, 260f);
        private static Rect _contentRect = new Rect(0f, 0f, 300f, 20f);

        public static void Toggle()
        {
            _showMenu = !_showMenu;
        }

        public static void OnGUI()
        {
            if (!_showMenu)
                return;

            GUI.Box(_windowRect, "SyncRADation Alpha");

            var net = LanNetworkManager.Instance;
            if (net == null)
            {
                GUI.Label(CR(10, 30, 300, 20), "Network not initialized");
                return;
            }

            GUI.Label(CR(10, 60, 300, 20), "Status: " + net.StatusText);

            if (net.Role == NetworkRole.Offline)
            {
                GUI.Label(CR(10, 90, 70, 20), "Address:");
                _address = GUI.TextField(CR(85, 90, 220, 20), _address);

                GUI.Label(CR(10, 120, 70, 20), "Port:");
                string portStr = GUI.TextField(CR(85, 120, 220, 20), _port.ToString());
                int.TryParse(portStr, out _port);

                if (GUI.Button(CR(10, 150, 140, 30), "Host Game"))
                    net.StartHost(_port);

                if (GUI.Button(CR(160, 150, 140, 30), "Connect"))
                    net.ConnectToHost(_address, _port);
            }
            else
            {
                if (GUI.Button(CR(10, 150, 140, 30), "Disconnect"))
                    net.StopNetwork();
            }

            if (GUI.Button(CR(10, 220, 300, 25), "Close (F2)"))
                _showMenu = false;
        }

        private static Rect CR(float x, float y, float w, float h)
        {
            _contentRect.x = _windowRect.x + x;
            _contentRect.y = _windowRect.y + y;
            _contentRect.width = w;
            _contentRect.height = h;
            return _contentRect;
        }
    }
}
