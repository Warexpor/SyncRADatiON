// IMGUI connection UI: host, connect, disconnect, scene status, N-player roster
using System.Collections.Generic;
using SyncRADation.Config;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.UI
{
    public static class MultiplayerMenu
    {
        private const float BaseHeight = 400f;
        private const float RowHeight = 18f;

        // persistent: F2 window state
        private static bool _showMenu;
        // persistent: F2 window state
        private static string _address = "127.0.0.1";
        // persistent: F2 window state
        private static int _port = PluginInfo.DefaultPort;
        // persistent: F2 window state
        private static Rect _windowRect = new Rect(100f, 100f, 360f, BaseHeight);
        // persistent: layout scratch rect
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

            var net = LanNetworkManager.Instance;
            if (net == null)
            {
                GUI.Box(_windowRect, "SyncRADation v" + PluginInfo.Version);
                GUI.Label(CR(10, 30, 320, 20), "Network not initialized");
                return;
            }

            bool active = NetGate.Active;
            // Roster rows (remote players only; the local line is part of the role row).
            List<int> roster = active ? net.GetSessionPlayerIdsSorted() : null;
            int rows = roster != null ? roster.Count : 0;
            // Two footer rows (game build, audit summary) + one per missing audit item.
            int footerRows = 2 + ModRuntime.PatchAuditMissing.Count;
            _windowRect.height = BaseHeight + (rows > 0 ? (rows + 1) * RowHeight : 0f)
                + footerRows * RowHeight
                + (active ? RowHeight : 0f);

            GUI.Box(_windowRect, "SyncRADation v" + PluginInfo.Version);

            GUI.Label(CR(10, 30, 340, 40), "Status: " + net.StatusText);
            GUI.Label(CR(10, 70, 320, 20), "Scene: " + (WorldRegistry.SceneName ?? "?")
                + " | enemies=" + WorldRegistry.EnemyCount
                + " doors=" + WorldRegistry.DoorCount);

            if (net.SceneMismatch)
                GUI.Label(CR(10, 90, 320, 35), "SCENE MISMATCH — following host chapter…");
            else
                GUI.Label(CR(10, 90, 320, 20), "Room: " + WorldRegistry.GetLocalRoomName());

            float y = 130f;
            if (!active)
            {
                GUI.Label(CR(10, y, 70, 20), "Address:");
                _address = GUI.TextField(CR(85, y, 230, 20), _address);

                GUI.Label(CR(10, y + 30, 70, 20), "Port:");
                string portStr = GUI.TextField(CR(85, y + 30, 230, 20), _port.ToString());
                int.TryParse(portStr, out _port);

                if (GUI.Button(CR(10, y + 65, 150, 30), "Host Game"))
                    net.StartHost(_port);

                if (GUI.Button(CR(170, y + 65, 150, 30), "Connect"))
                    net.ConnectToHost(_address, _port);
                y += 100f;
            }
            else
            {
                GUI.Label(CR(10, y, 320, 20), "Role: " + net.Role + " | you=#" + net.LocalPlayerId
                    + " | players " + net.GetPlayerCount() + "/" + PluginInfo.MaxPlayers);
                y += 22f;

                // One row per session player: id, role, scene, ping.
                for (int i = 0; i < roster.Count; i++)
                {
                    int pid = roster[i];
                    string tag = pid == net.LocalPlayerId ? " (you)" : (pid == 0 ? " (host)" : "");
                    string scene = net.SceneOf(pid);
                    string ping = pid != net.LocalPlayerId && NetGate.HostRole
                        ? "  " + net.GetPeerPing(pid) + "ms" : "";
                    GUI.Label(CR(20, y, 320, 20), "#" + pid + tag
                        + (string.IsNullOrEmpty(scene) ? "" : "  " + scene) + ping);
                    y += RowHeight;
                }
                y += 4f;

                if (GUI.Button(CR(10, y, 150, 30), "Disconnect"))
                    net.StopNetwork();
                y += 40f;
            }

            if (active)
            {
                GUI.Label(CR(10, y, 340, 20), net.SceneHandlers.WorldSyncStatus());
                y += RowHeight;
            }

            GUI.Label(CR(10, y, 340, 20), ModConfig.Describe(ModConfig.EffectiveSyncFlags)
                + (NetGate.ClientRole ? " (host)" : ""));
            y += 25f;

            if (active && GUI.Button(CR(10, y, 150, 28), "Resync world"))
            {
                if (NetGate.ClientRole)
                    net.SessionHandlers.RequestWorldSnapshot();
                else if (net.HasReadyPeers)
                    net.SessionHandlers.SendFullWorldSnapshot(); // host: re-dump to every client
            }
            y += 35f;

            GUI.Label(CR(10, y, 340, 20), "G/DROP=drop  TAKE=pickup  F6/F7/F11 cheats");
            y += 30f;

            if (GUI.Button(CR(10, y, 330, 25), "Close (F2)"))
                _showMenu = false;
            y += 35f;

            GUI.Label(CR(10, y, 330, 20), "LAN | protocol v" + PluginInfo.ProtocolVersion
                + " | HP " + Players.NetworkDamageSystem.PlayerHP.ToString("F0"));
            y += RowHeight + 2f;

            GUI.Label(CR(10, y, 340, 20), "Game " + GameBuild.Label);
            y += RowHeight;
            GUI.Label(CR(10, y, 340, 20), ModRuntime.PatchAuditSummary);
            y += RowHeight;
            var missing = ModRuntime.PatchAuditMissing;
            for (int i = 0; i < missing.Count; i++)
            {
                GUI.Label(CR(20, y, 330, 20), "missing: " + missing[i]);
                y += RowHeight;
            }
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
