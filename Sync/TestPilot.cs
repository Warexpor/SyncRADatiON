// Unattended test driver for automated multi-instance runs (scripts/pilot/). Off unless the game starts with
// --sync-pilot <mode> [--sync-pilot-dir <dir>]: "host[:Scene]" hosts LAN and loads that chapter, "join" connects to
// 127.0.0.1 and follows the host into its scene. In play it runs the lines appended to <dir>/cmd.txt (default: a
// "pilot" folder next to SIGNALIS_Data) and writes what they did to <dir>/out.txt and the log ([Pilot]). Commands go
// through the same game functions a player's action would (Interaction.trigger, TakeDamage, HurtElster, ...).
// TestPilot.Commands.cs: the command table. TestPilot.Digest.cs: the per-peer world digest the scripts diff.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SyncRADation.Networking;
using UnityEngine;

namespace SyncRADation.Sync
{
    public static partial class TestPilot
    {
        // persistent: pilot process run (set once from the command line)
        private static string _mode = "";
        // persistent: pilot process run (set once from the command line)
        private static string _dir;

        /// <summary>The game was started under the pilot (--sync-pilot).</summary>
        public static bool Active => _mode.Length > 0;

        private enum Stage { Boot, Hosting, Joining, Loading, InWorld }

        // persistent: the pilot drives one process run
        private static Stage _stage = Stage.Boot;
        // persistent: pilot run clock
        private static float _stageAt;
        // persistent: pilot run clock
        private static float _nextStepAt;
        // persistent: pilot run clock (next connect attempt of a joiner)
        private static float _nextConnectAt;
        // persistent: bytes of cmd.txt already run
        private static long _cmdOffset;
        // persistent: "wait" command
        private static float _waitUntil;
        // persistent: Unity log hook installed once
        private static bool _hooked;
        // persistent: pilot run error tally
        private static readonly Dictionary<string, int> _errorCounts = new Dictionary<string, int>();
        // persistent: "at" command waiting for its moment
        private static string[] _atCmd;
        // persistent: when (Unix ms) the "at" command runs
        private static long _atMs;
        // persistent: settle watch (scene this run last reported settled / seen)
        private static string _seenScene = "";
        // persistent: settle watch
        private static string _settledScene = "";
        // persistent: settle watch (realtime the local world first looked settled)
        private static float _settleSince = -1f;
        // persistent: party watch (the sorted session ids last reported)
        private static string _roster = "";

        /// <summary>Called once from the command line parse.</summary>
        public static void Configure(string mode, string dir)
        {
            _mode = (mode ?? "").Trim();
            _dir = string.IsNullOrEmpty(dir) ? null : dir;
            if (_mode.Length == 0) return;
            Application.runInBackground = true;
            // Pilot instances run several to a machine, headless at 640x360: quarter-size textures (two mip levels
            // dropped) keep each instance's memory down; nothing the pilot checks reads a texture.
            try { QualitySettings.masterTextureLimit = 2; } catch (Exception e) { Guard.Swallow(e); }
        }

        private static string Dir
        {
            get
            {
                if (_dir == null)
                    _dir = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "pilot");
                try { Directory.CreateDirectory(_dir); } catch (Exception e) { Guard.Swallow(e); }
                return _dir;
            }
        }

        public static void Tick()
        {
            if (!Active) return;
            var net = LanNetworkManager.Instance;
            if (net == null) return;
            try
            {
                Step(net);
            }
            catch (Exception ex)
            {
                Out("pilot error: " + ex.GetType().Name + ": " + ex.Message);
                _nextStepAt = Time.realtimeSinceStartup + 2f;
            }
        }

        private static void Enter(Stage s)
        {
            _stage = s;
            _stageAt = Time.realtimeSinceStartup;
            Out("stage " + s);
        }

        private static string ActiveScene()
        {
            try { return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? ""; }
            catch (Exception e) { Guard.Swallow(e); return ""; }
        }

        /// <summary>The local world is playable: a chapter (not a menu or loading scene), the local Elster, no load running.</summary>
        internal static bool LocalSettled()
        {
            string scene = ActiveScene();
            if (scene.Length == 0 || SceneFollowService.IsMenuScene(scene) || SceneFollowService.LocalIsTransient())
                return false;
            if (PlayerState.player == null) return false;
            try { if (PlayerState.gameState == PlayerState.gameStates.loading) return false; }
            catch (Exception e) { Guard.Swallow(e); return false; }
            return true;
        }

        private static void Step(LanNetworkManager net)
        {
            float now = Time.realtimeSinceStartup;
            // "at": checked every frame, so peers told the same moment act within a frame of each other.
            if (_atCmd != null && _stage == Stage.InWorld && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= _atMs)
            {
                string[] cmd = _atCmd;
                _atCmd = null;
                Out("  at " + _atMs + ": " + string.Join(" ", cmd));
                RunGuarded(net, cmd);
            }
            if (_stage == Stage.InWorld)
            {
                KeepGod();
                KeepWalking(now);
                AutoDialogue(now);
                RunDeferred(now);
            }
            if (now < _nextStepAt) return;
            _nextStepAt = now + 0.25f;

            if (!_hooked)
            {
                _hooked = true;
                try
                {
                    Application.SetStackTraceLogType(LogType.Error, StackTraceLogType.ScriptOnly);
                    Application.SetStackTraceLogType(LogType.Exception, StackTraceLogType.ScriptOnly);
                    Application.add_logMessageReceived((Application.LogCallback)new Action<string, string, LogType>(OnUnityLog));
                }
                catch (Exception ex) { Out("unity log hook failed: " + ex.Message); }
                Out("pilot " + _mode + " v" + PluginInfo.Version + " dir=" + Dir);
            }

            WatchScene(net, now);

            switch (_stage)
            {
                case Stage.Boot:
                    // The title menu is up and has settled.
                    if (!SceneFollowService.IsMainMenu(ActiveScene()) || now < 6f) return;
                    if (_mode.StartsWith("host", StringComparison.OrdinalIgnoreCase))
                    {
                        net.StartHost(Config.ModConfig.ConnectPort?.Value ?? PluginInfo.DefaultPort);
                        Out("StartHost -> " + net.Role + " (" + net.StatusText + ")");
                        Enter(Stage.Hosting);
                    }
                    else if (_mode.Equals("join", StringComparison.OrdinalIgnoreCase))
                    {
                        _nextConnectAt = 0f;
                        Enter(Stage.Joining);
                    }
                    else
                    {
                        Out("unknown --sync-pilot mode '" + _mode + "'");
                        _nextStepAt = float.MaxValue;
                    }
                    return;

                case Stage.Hosting:
                {
                    if (now - _stageAt < 2f) return;
                    int colon = _mode.IndexOf(':');
                    string scene = colon > 0 ? _mode.Substring(colon + 1) : "DET_Detention";
                    Out("loading chapter " + scene);
                    Cheats.LocationTeleporter.LoadChapterByName(scene);
                    Enter(Stage.Loading);
                    return;
                }

                case Stage.Joining:
                    if (!SceneFollowService.IsMainMenu(ActiveScene()))
                    {
                        Enter(Stage.Loading);
                        return;
                    }
                    if (!net.IsConnected && net.Role == NetworkRole.Offline && now >= _nextConnectAt)
                    {
                        _nextConnectAt = now + 15f;
                        string addr = Config.ModConfig.ConnectAddress?.Value ?? "127.0.0.1";
                        int port = Config.ModConfig.ConnectPort?.Value ?? PluginInfo.DefaultPort;
                        Out("connect " + addr + ":" + port);
                        net.ConnectToHost(addr, port);
                    }
                    return;

                case Stage.Loading:
                    if (_settledScene.Length > 0 && now - _stageAt > 3f)
                        Enter(Stage.InWorld);
                    return;

                case Stage.InWorld:
                    // The next line waits for a deferred action (its target's room waking) to finish.
                    if (now >= _waitUntil && _deferred == null)
                        RunCommands(net);
                    return;
            }
        }

        /// <summary>One line when the local scene changes, when it settles, and when the party roster changes.</summary>
        private static void WatchScene(LanNetworkManager net, float now)
        {
            string scene = ActiveScene();
            if (scene != _seenScene)
            {
                _seenScene = scene;
                _settledScene = "";
                _settleSince = -1f;
                Out("scene -> " + scene);
            }
            if (_settledScene.Length == 0)
            {
                if (LocalSettled())
                {
                    if (_settleSince < 0f) _settleSince = now;
                    else if (now - _settleSince >= 2f)
                    {
                        _settledScene = scene;
                        Out("settled " + scene + " room=" + RoomName() + " role=" + net.Role);
                    }
                }
                else _settleSince = -1f;
            }

            string roster = "";
            try
            {
                if (net.IsConnected)
                {
                    var ids = net.GetSessionPlayerIdsSorted();
                    var sb = new StringBuilder();
                    for (int i = 0; i < ids.Count; i++) sb.Append(i > 0 ? "," : "").Append('p').Append(ids[i]);
                    roster = sb.ToString();
                }
            }
            catch (Exception e) { Guard.Swallow(e); }
            if (roster != _roster)
            {
                _roster = roster;
                Out("party [" + roster + "] me=p" + net.LocalPlayerId + " role=" + net.Role);
            }
        }

        // ------------------------------------------------------------ commands

        private static void RunCommands(LanNetworkManager net)
        {
            string path = Path.Combine(Dir, "cmd.txt");
            if (!File.Exists(path)) return;
            byte[] buf;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Length < _cmdOffset) _cmdOffset = 0; // the file was replaced
                if (fs.Length == _cmdOffset) return;
                fs.Seek(_cmdOffset, SeekOrigin.Begin);
                buf = new byte[fs.Length - _cmdOffset];
                int read = fs.Read(buf, 0, buf.Length);
                if (read < buf.Length) Array.Resize(ref buf, read);
            }
            // Whole lines only (a partial last line waits for its newline); a "wait" stops the batch and the lines after
            // it run once it is over.
            long baseOffset = _cmdOffset;
            int start = 0;
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i] != (byte)'\n') continue;
                string line = Encoding.UTF8.GetString(buf, start, i - start).Trim();
                start = i + 1;
                _cmdOffset = baseOffset + start;
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                Out("> " + line);
                RunGuarded(net, line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
                if (Time.realtimeSinceStartup < _waitUntil) return;
            }
        }

        private static void RunGuarded(LanNetworkManager net, string[] a)
        {
            try { Run(net, a); }
            catch (Exception ex) { Out("  failed: " + ex.GetType().Name + ": " + ex.Message); }
        }

        // ------------------------------------------------------------ Unity errors

        /// <summary>Each distinct Unity error or exception: the first three with their stack, then a count.</summary>
        private static void OnUnityLog(string message, string stack, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            string key = message != null && message.Length > 160 ? message.Substring(0, 160) : message ?? "";
            int n;
            _errorCounts.TryGetValue(key, out n);
            _errorCounts[key] = ++n;
            if (n <= 3)
                Out("unity " + type + " #" + n + ": " + key + "\n    " + (stack ?? "").Trim().Replace("\n", "\n    "));
            else if (n == 100 || n == 1000 || n == 10000)
                Out("unity " + type + " x" + n + ": " + key);
        }

        // ------------------------------------------------------------ output

        internal static string Pos(Vector3 v) =>
            v.x.ToString("0.0", CultureInfo.InvariantCulture) + "," + v.y.ToString("0.0", CultureInfo.InvariantCulture)
            + "," + v.z.ToString("0.0", CultureInfo.InvariantCulture);

        internal static string RoomName()
        {
            try
            {
                var r = PlayerState.currentRoom;
                if (r == null) return "-";
                return string.IsNullOrEmpty(r.roomName) ? r.gameObject.name : r.roomName;
            }
            catch (Exception e) { Guard.Swallow(e); return "?"; }
        }

        private static void Out(string line)
        {
            ModRuntime.Log?.Msg("[Pilot] " + line);
            try
            {
                File.AppendAllText(Path.Combine(Dir, "out.txt"),
                    DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + line + "\n");
            }
            catch (IOException) { }
        }
    }
}
