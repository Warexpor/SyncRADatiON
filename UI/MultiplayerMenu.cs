// IMGUI connection UI: host, connect, disconnect, scene status
using SyncRADation.Config;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.UI
{
    public static class MultiplayerMenu
    {
        private static bool _showMenu;
        private static string _address = "127.0.0.1";
        private static int _port = PluginInfo.DefaultPort;
        private static Rect _windowRect = new Rect(100f, 100f, 360f, 400f);
        private static Rect _contentRect = new Rect(0f, 0f, 300f, 20f);

        public static void Toggle()
        {
            _showMenu = !_showMenu;
            if (_showMenu)
            {
                _address = ModConfig.ConnectAddress?.Value ?? "127.0.0.1";
                _port = ModConfig.ConnectPort?.Value ?? PluginInfo.DefaultPort;
            }
        }

        public static void OnGUI()
        {
            if (!_showMenu)
                return;

            GUI.Box(_windowRect, "SyncRADation v" + PluginInfo.Version);

            var net = LanNetworkManager.Instance;
            if (net == null)
            {
                GUI.Label(CR(10, 30, 320, 20), "Network not initialized");
                return;
            }

            GUI.Label(CR(10, 50, 320, 20), "Status: " + net.StatusText);
            GUI.Label(CR(10, 70, 320, 20), "Scene: " + (WorldRegistry.SceneName ?? "?")
                + " | enemies=" + WorldRegistry.EnemyCount
                + " doors=" + WorldRegistry.DoorCount);

            if (net.SceneMismatch)
                GUI.Label(CR(10, 90, 320, 35), "SCENE MISMATCH — following host chapter…");
            else
                GUI.Label(CR(10, 90, 320, 20), "Room: " + WorldRegistry.GetLocalRoomName());

            if (net.Role == NetworkRole.Offline)
            {
                GUI.Label(CR(10, 130, 70, 20), "Address:");
                _address = GUI.TextField(CR(85, 130, 230, 20), _address);

                GUI.Label(CR(10, 160, 70, 20), "Port:");
                string portStr = GUI.TextField(CR(85, 160, 230, 20), _port.ToString());
                int.TryParse(portStr, out _port);

                if (GUI.Button(CR(10, 195, 150, 30), "Host Game"))
                    net.StartHost(_port);

                if (GUI.Button(CR(170, 195, 150, 30), "Connect"))
                    net.ConnectToHost(_address, _port);
            }
            else
            {
                GUI.Label(CR(10, 130, 320, 20), "Role: " + net.Role + " | id=" + net.LocalPlayerId
                    + " | players~" + net.GetPlayerCount());
                if (GUI.Button(CR(10, 160, 150, 30), "Disconnect"))
                    net.StopNetwork();
            }

            GUI.Label(CR(10, 230, 340, 20), "FF=" + (ModConfig.FriendlyFire?.Value == true ? "ON" : "OFF")
                + " puzzles=" + (ModConfig.PuzzlesEnabled ? "ON" : "OFF")
                + " pickups=" + (ModConfig.SyncWorldPickups?.Value == true ? "ON" : "OFF"));

            if (net.Role != NetworkRole.Offline && GUI.Button(CR(10, 255, 150, 28), "Resync world"))
            {
                if (net.Role == NetworkRole.Host)
                    net.SendFullWorldSnapshot();
                else
                    net.RequestWorldSnapshot();
            }

            GUI.Label(CR(10, 290, 340, 20), "G=drop E=pickup F6/F7/F11 cheats");

            if (GUI.Button(CR(10, 320, 330, 25), "Close (F2)"))
                _showMenu = false;

            GUI.Label(CR(10, 355, 330, 20), "LAN | protocol v" + PluginInfo.ProtocolVersion
                + " | HP " + Players.NetworkDamageSystem.PlayerHP.ToString("F0"));
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
