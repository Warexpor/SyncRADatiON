// Discrete playtest traces. Always-on for session/world/story edges.
// VerboseLogging: FMOD, clone/FX internals, incremental puzzle apply.
// Identical lines collapse for RepeatWindow so loops do not flood Latest.log.
using UnityEngine;

namespace SyncRADation.Sync
{
    public static class PlaytestLog
    {
        const float RepeatWindow = 3f;

        static string _lastLine = "";
        static float _lastAt = -999f;

        public static void Reset()
        {
            _lastLine = "";
            _lastAt = -999f;
        }

        public static void Event(string tag, string msg)
        {
            Emit(tag, msg, false);
        }

        public static void Verbose(string tag, string msg)
        {
            if (!ModRuntime.VerboseLogging) return;
            Emit(tag, msg, false);
        }

        public static void Warn(string tag, string msg)
        {
            Emit(tag, msg, true);
        }

        public static void Miss(string tag, string what, ulong id)
        {
            Warn(tag, "MISS " + what + " id=" + id.ToString("X16"));
        }

        static void Emit(string tag, string msg, bool warning)
        {
            var log = ModRuntime.Log;
            if (log == null) return;
            string line = "[" + tag + "] " + RolePrefix() + msg;
            float now = 0f;
            try { now = Time.unscaledTime; } catch { }
            if (line == _lastLine && now - _lastAt < RepeatWindow)
                return;
            _lastLine = line;
            _lastAt = now;
            if (warning) log.Warning(line);
            else log.Msg(line);
        }

        static string RolePrefix()
        {
            try
            {
                var n = ModRuntime.Network;
                if (n == null || !n.IsConnected) return "";
                return n.Role == Networking.NetworkRole.Host ? "H " : "C ";
            }
            catch
            {
                return "";
            }
        }
    }
}
