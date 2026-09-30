// Visible swallowed exceptions. Replaces silent empty catch blocks with a catch that calls Guard.Swallow(e).
// Behaviour of the call site is unchanged (Guard never throws, never rethrows).
// Throttle: first hit per tag logs immediately; afterwards at most one line per tag per 30 s,
// carrying the count of hits suppressed in between. Suppressed path = one dictionary lookup, no allocation.
// Auto tag (no explicit tag): File.Member:line from [CallerFilePath]/[CallerMemberName]/[CallerLineNumber];
// these are compile-time constants so the call site allocates nothing either.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using MelonLoader;

namespace SyncRADation
{
    public static class Guard
    {
        struct Key : IEquatable<Key>
        {
            public readonly string A;
            public readonly string B;
            public readonly int N;

            public Key(string a, string b, int n)
            {
                A = a;
                B = b;
                N = n;
            }

            public bool Equals(Key o)
            {
                return N == o.N && string.Equals(A, o.A) && string.Equals(B, o.B);
            }

            public override bool Equals(object obj)
            {
                return obj is Key k && Equals(k);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = A != null ? A.GetHashCode() : 0;
                    h = h * 31 + (B != null ? B.GetHashCode() : 0);
                    return h * 31 + N;
                }
            }
        }

        sealed class Entry
        {
            public long NextAt;
            public int Suppressed;
        }

        // Guard statics: log-throttle bookkeeping, intentionally process-lifetime (survives sessions so a recurring fault stays throttled).
        static readonly Dictionary<Key, Entry> _seen = new Dictionary<Key, Entry>(256);
        static readonly object _lock = new object();
        static readonly long _repeatStopwatchTicks = 30L * Stopwatch.Frequency;
        static int _internalFailures;

        public static int InternalFailures => _internalFailures;

        /// <summary>Explicit stable tag, e.g. "Puzzle.ApplyKeypad3D".</summary>
        public static void Swallow(string tag, Exception e)
        {
            try
            {
                if (!ShouldLog(new Key(tag ?? "?", null, 0), out int suppressed)) return;
                Emit(tag ?? "?", e, suppressed);
            }
            catch (Exception)
            {
                _internalFailures++;
            }
        }

        /// <summary>Auto tag from the calling site.</summary>
        public static void Swallow(Exception e,
            [CallerMemberName] string member = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
        {
            try
            {
                if (!ShouldLog(new Key(file, member, line), out int suppressed)) return;
                Emit(AutoTag(file, member, line), e, suppressed);
            }
            catch (Exception)
            {
                _internalFailures++;
            }
        }

        public static void Try(string tag, Action a)
        {
            try
            {
                a();
            }
            catch (Exception e)
            {
                Swallow(tag, e);
            }
        }

        public static T Try<T>(string tag, Func<T> f, T fallback)
        {
            try
            {
                return f();
            }
            catch (Exception e)
            {
                Swallow(tag, e);
                return fallback;
            }
        }

        static bool ShouldLog(Key key, out int suppressed)
        {
            suppressed = 0;
            long now = Stopwatch.GetTimestamp();
            lock (_lock)
            {
                if (_seen.TryGetValue(key, out Entry en))
                {
                    if (now < en.NextAt)
                    {
                        if (en.Suppressed < int.MaxValue) en.Suppressed++;
                        return false;
                    }
                    suppressed = en.Suppressed;
                    en.Suppressed = 0;
                    en.NextAt = now + _repeatStopwatchTicks;
                    return true;
                }
                _seen[key] = new Entry { NextAt = now + _repeatStopwatchTicks };
                return true;
            }
        }

        static string AutoTag(string file, string member, int line)
        {
            string f = "?";
            if (!string.IsNullOrEmpty(file))
            {
                try { f = Path.GetFileNameWithoutExtension(file); }
                catch (Exception) { _internalFailures++; }
            }
            return f + "." + member + ":" + line;
        }

        static void Emit(string tag, Exception e, int suppressed)
        {
            var sb = new StringBuilder(160);
            sb.Append("[Guard] ").Append(tag).Append(": ");
            if (e == null)
            {
                sb.Append("(null exception)");
            }
            else
            {
                string tn = e.GetType().Name;
                sb.Append(tn).Append(": ");
                string msg;
                try { msg = e.Message; }
                catch (Exception) { msg = "(message unavailable)"; _internalFailures++; }
                if (msg != null)
                {
                    int nl = msg.IndexOf('\n');
                    if (nl >= 0) msg = msg.Substring(0, nl).TrimEnd('\r');
                    if (msg.Length > 240) msg = msg.Substring(0, 240) + "...";
                }
                sb.Append(msg);
                if (!IsIl2Cpp(e))
                {
                    string top = FirstStackLine(e);
                    if (top != null) sb.Append(" @ ").Append(top);
                }
            }
            if (suppressed > 0) sb.Append(" (+").Append(suppressed).Append(" suppressed)");
            string line = sb.ToString();

            var log = ModRuntime.Log;
            if (log != null) log.Warning(line);
            else MelonLogger.Warning(line);
        }

        static bool IsIl2Cpp(Exception e)
        {
            string full = e.GetType().FullName;
            return full != null && full.IndexOf("Il2Cpp", StringComparison.Ordinal) >= 0;
        }

        static string FirstStackLine(Exception e)
        {
            try
            {
                string st = e.StackTrace;
                if (string.IsNullOrEmpty(st)) return null;
                int nl = st.IndexOf('\n');
                string l = (nl >= 0 ? st.Substring(0, nl) : st).Trim();
                if (l.StartsWith("at ", StringComparison.Ordinal)) l = l.Substring(3);
                return l;
            }
            catch (Exception)
            {
                _internalFailures++;
                return null;
            }
        }
    }
}
