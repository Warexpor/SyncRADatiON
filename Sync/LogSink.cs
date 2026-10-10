// The one place a mod log line leaves through (ModRuntime.Log). Every line gets the role prefix after its tag
// ("[Door] H ..."), warnings and errors are counted per tag for the session summary, and nothing here throws.
using System.Collections.Generic;
using System.Text;
using MelonLoader;

namespace SyncRADation.Sync
{
    public sealed class LogSink
    {
        readonly MelonLogger.Instance _ml;

        // persistent: per-process counters, printed and cleared by Summary (session end / quit)
        static readonly Dictionary<string, int> _warns = new Dictionary<string, int>(32);
        // persistent: per-process counters, printed and cleared by Summary
        static readonly Dictionary<string, int> _errors = new Dictionary<string, int>(8);
        // persistent: per-process counter, cleared by Summary
        static int _lines;

        public LogSink(MelonLogger.Instance ml) { _ml = ml; }

        public void Msg(string s) => Write(0, WithRole(s));
        public void Warning(string s) => Write(1, WithRole(s));
        public void Error(string s) => Write(2, WithRole(s));

        /// <summary>A line that already carries its role prefix (PlaytestLog).</summary>
        internal void Raw(int level, string line) => Write(level, line);

        void Write(int level, string line)
        {
            try
            {
                _lines++;
                if (level > 0) Count(level == 1 ? _warns : _errors, TagOf(line));
                if (level == 0) _ml.Msg(line);
                else if (level == 1) _ml.Warning(line);
                else _ml.Error(line);
            }
            catch (System.Exception) { /* the log must never take the game down */ }
        }

        static void Count(Dictionary<string, int> into, string tag)
        {
            int n;
            into.TryGetValue(tag, out n);
            into[tag] = n + 1;
        }

        static string TagOf(string line)
        {
            if (string.IsNullOrEmpty(line) || line[0] != '[') return "-";
            int end = line.IndexOf(']');
            return end > 1 ? line.Substring(1, end - 1) : "-";
        }

        /// <summary>"[Tag] text" becomes "[Tag] H text" in a live session; a line without a leading tag is left alone.</summary>
        static string WithRole(string s)
        {
            if (string.IsNullOrEmpty(s) || s[0] != '[') return s;
            string role = PlaytestLog.RolePrefix();
            if (role.Length == 0) return s;
            int end = s.IndexOf("] ", System.StringComparison.Ordinal);
            if (end < 0) return s;
            // "[Stall] [WARNING]"-style second tags and lines that already say their role stay as they are.
            int at = end + 2;
            if (at + 1 < s.Length && (s[at] == 'H' || s[at] == 'C') && s[at + 1] == ' ') return s;
            return s.Substring(0, at) + role + s.Substring(at);
        }

        /// <summary>
        /// One block when a session ends or the game quits: how much was logged and every tag that warned or failed,
        /// loudest first. The first thing to read in a log: a clean session says "no warnings".
        /// </summary>
        public static void Summary(string why)
        {
            var log = ModRuntime.Log;
            if (log == null) return;
            // Nothing since the last summary (quit right after the session ended): one block is enough.
            if (_lines == 0 && _warns.Count == 0 && _errors.Count == 0) return;
            try
            {
                var sb = new StringBuilder("[Log] summary (" + why + "): " + _lines + " lines");
                sb.Append(Describe(", warnings ", _warns)).Append(Describe(", errors ", _errors));
                if (_warns.Count == 0 && _errors.Count == 0) sb.Append(", no warnings");
                sb.Append(", guard internal failures ").Append(Guard.InternalFailures);
                _warns.Clear();
                _errors.Clear();
                _lines = 0;
                log._ml.Msg(sb.ToString());
            }
            catch (System.Exception) { /* never throws */ }
        }

        static string Describe(string head, Dictionary<string, int> counts)
        {
            if (counts.Count == 0) return "";
            var list = new List<KeyValuePair<string, int>>(counts);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            int total = 0;
            var sb = new StringBuilder();
            for (int i = 0; i < list.Count; i++)
            {
                total += list[i].Value;
                if (i < 8) sb.Append(i == 0 ? " (" : ", ").Append(list[i].Key).Append(' ').Append(list[i].Value);
            }
            if (list.Count > 8) sb.Append(", +").Append(list.Count - 8).Append(" tags");
            return head + total + sb.Append(')');
        }
    }
}
