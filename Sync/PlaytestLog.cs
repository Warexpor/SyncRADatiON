// Discrete playtest traces: what happened, once, with the values that explain it. Rules (docs/LOGGING.md):
//   Event    always on: an edge in session / world / story state.
//   Verbose  VerboseLogging: extra lines from code that runs anyway.
//   Rate     a line that can repeat fast: at most one per key per interval, and the next one that goes out says how
//            many were dropped ("+N suppressed"), so a throttled line still shows how loud the thing is.
//   Warn     something the mod did not expect; counted per tag for the session summary (LogSink).
// An identical line repeated back to back is written once; when the run ends a "(xN in a row: <line>)" line follows.
using System.Collections.Generic;
using UnityEngine;

namespace SyncRADation.Sync
{
    public static class PlaytestLog
    {
        const float RepeatWindow = 3f;
        const int RateKeyCap = 512;

        struct RateSlot
        {
            public float NextAt;
            public int Suppressed;
        }

        static string _lastLine = "";
        static float _lastAt = -999f;
        static int _repeats;
        // Per-key throttle bookkeeping: cleared by Reset (session end) and when it outgrows RateKeyCap.
        static readonly Dictionary<string, RateSlot> _rate = new Dictionary<string, RateSlot>(64);

        public static void Reset()
        {
            FlushRepeats();
            _lastLine = "";
            _lastAt = -999f;
            _rate.Clear();
        }

        public static void Event(string tag, string msg)
        {
            Emit(tag, msg, 0);
        }

        /// <summary>Event that is never collapsed (the scene-loaded line).</summary>
        public static void EventAlways(string tag, string msg)
        {
            FlushRepeats();
            _lastLine = "";
            Emit(tag, msg, 0);
        }

        public static void Verbose(string tag, string msg)
        {
            if (!ModRuntime.VerboseLogging) return;
            Emit(tag, msg, 0);
        }

        public static void Warn(string tag, string msg)
        {
            Emit(tag, msg, 1);
        }

        public static void Miss(string tag, string what, ulong id)
        {
            Warn(tag, "MISS " + what + " id=" + id.ToString("X16"));
        }

        /// <summary>True when a line under this key may go out now; build the message only then (see Rate).</summary>
        public static bool RateOk(string key, float intervalSec, out int suppressed)
        {
            suppressed = 0;
            float now = Now();
            RateSlot slot;
            if (_rate.TryGetValue(key, out slot))
            {
                if (now < slot.NextAt)
                {
                    slot.Suppressed++;
                    _rate[key] = slot;
                    return false;
                }
                suppressed = slot.Suppressed;
            }
            else if (_rate.Count >= RateKeyCap)
                _rate.Clear();
            _rate[key] = new RateSlot { NextAt = now + intervalSec, Suppressed = 0 };
            return true;
        }

        /// <summary>At most one line per key per interval; the line that goes out carries the dropped count.</summary>
        public static void Rate(string tag, string key, float intervalSec, string msg, bool warn = false)
        {
            int suppressed;
            if (!RateOk(key, intervalSec, out suppressed)) return;
            Emit(tag, suppressed > 0 ? msg + " (+" + suppressed + " suppressed)" : msg, warn ? 1 : 0);
        }

        static float Now()
        {
            try { return Time.unscaledTime; }
            catch (System.Exception) { return 0f; }
        }

        static void FlushRepeats()
        {
            if (_repeats <= 0) return;
            int n = _repeats;
            _repeats = 0;
            // Names the line: lines written straight to the sink may have come in between.
            string what = _lastLine.Length > 60 ? _lastLine.Substring(0, 60) + "..." : _lastLine;
            ModRuntime.Log?.Raw(0, "  (x" + (n + 1) + " in a row: " + what + ")");
        }

        static void Emit(string tag, string msg, int level)
        {
            var log = ModRuntime.Log;
            if (log == null) return;
            string line = "[" + tag + "] " + RolePrefix() + msg;
            float now = Now();
            if (line == _lastLine && now - _lastAt < RepeatWindow)
            {
                _repeats++;
                return;
            }
            FlushRepeats();
            _lastLine = line;
            _lastAt = now;
            log.Raw(level, line);
        }

        internal static string RolePrefix()
        {
            try
            {
                if (!NetGate.Live) return "";
                return NetGate.HostRole ? "H " : "C ";
            }
            catch
            {
                return "";
            }
        }
    }
}
