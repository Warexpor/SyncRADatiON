// [Unity]: the game's own errors and exceptions (Debug.LogError / LogException / an exception thrown out of a native
// Update) written into Latest.log. Without this they only reach Unity's Player.log, and a native script that died
// because of something the mod did left no trace next to the mod's lines.
// Per message: the first two with the stack, then one line at 10, 100, 1000... A message seen outside a session too
// is the base game's own noise and says so ("also offline").
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SyncRADation.Sync
{
    public static class UnityLogTap
    {
        const int MaxKeys = 400;
        const int StackLines = 8;

        sealed class Seen
        {
            public int Count;
            public bool Offline;
        }

        // persistent: per-process counters (a recurring game error stays counted across sessions)
        static readonly Dictionary<string, Seen> _seen = new Dictionary<string, Seen>(64);
        // persistent: once per process
        static bool _hooked;

        public static void Install()
        {
            if (_hooked) return;
            _hooked = true;
            try
            {
                Application.SetStackTraceLogType(LogType.Error, StackTraceLogType.ScriptOnly);
                Application.SetStackTraceLogType(LogType.Exception, StackTraceLogType.ScriptOnly);
                Application.add_logMessageReceived((Application.LogCallback)new Action<string, string, LogType>(OnLog));
            }
            catch (Exception e) { Guard.Swallow(e); }
        }

        static void OnLog(string message, string stack, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            try
            {
                string key = message ?? "";
                if (key.Length > 200) key = key.Substring(0, 200);
                key = key.Replace('\n', ' ').Replace('\r', ' ');
                Seen s;
                if (!_seen.TryGetValue(key, out s))
                {
                    if (_seen.Count >= MaxKeys) return;
                    _seen[key] = s = new Seen();
                }
                s.Count++;
                bool live = NetGate.Live;
                if (!live) s.Offline = true;
                int n = s.Count;
                bool withStack = n <= 2;
                if (!withStack && n != 10 && n != 100 && n != 1000 && n != 10000) return;
                string line = "[Unity] " + type + " #" + n + (s.Offline && live ? " (also offline)" : "") + ": " + key;
                if (withStack) line += Stack(stack);
                var log = ModRuntime.Log;
                if (log == null) return;
                // Counted in the session summary only when it happened in a session: the base game logs errors of
                // its own in the menu.
                if (!live) log.Msg(line);
                else if (type == LogType.Exception) log.Error(line);
                else log.Warning(line);
            }
            catch (Exception) { /* a log callback must never throw back into Unity */ }
        }

        static string Stack(string stack)
        {
            if (string.IsNullOrEmpty(stack)) return "";
            var lines = stack.Trim().Split('\n');
            var sb = new System.Text.StringBuilder(256);
            for (int i = 0; i < lines.Length && i < StackLines; i++)
            {
                string l = lines[i].Trim();
                if (l.Length == 0) continue;
                if (l.Length > 160) l = l.Substring(0, 160) + "...";
                sb.Append("\n    at ").Append(l);
            }
            if (lines.Length > StackLines) sb.Append("\n    (+").Append(lines.Length - StackLines).Append(" frames)");
            return sb.ToString();
        }
    }
}
