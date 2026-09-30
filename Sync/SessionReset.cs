// One place that knows how to forget session state. Every static mutable collection / flag that must not
// leak from one network session (or one party-wipe reload) into the next registers a clear here;
// statics that are intentionally persistent carry a one-line comment saying why instead.
// Registrations live in Bootstrap/SessionResetRegistrations.cs (and in the owning class for private state).
// RunAll runs at StartHost / ConnectToHost (before the new session), StopNetwork, and after a host wipe reload.
using System;
using System.Collections.Generic;

namespace SyncRADation.Sync
{
    public static class SessionReset
    {
        public const string ReasonStart = "start";
        public const string ReasonStop = "stop";
        public const string ReasonWipe = "wipe";

        private struct Entry
        {
            public string Tag;
            public Action Clear;
            /// <summary>True: also cleared on a host wipe reload. False: connection-scoped (start / stop only).</summary>
            public bool OnWipe;
        }

        private static readonly List<Entry> _entries = new List<Entry>(64);
        private static readonly HashSet<string> _tags = new HashSet<string>();
        private static bool _running;

        public static int Count => _entries.Count;

        /// <summary>Session state: cleared on start, stop and after a wipe reload.</summary>
        public static void Register(string tag, Action clear)
        {
            Add(tag, clear, true);
        }

        /// <summary>Connection state: cleared on start / stop only (a wipe keeps the same party, so it must survive).</summary>
        public static void RegisterConnection(string tag, Action clear)
        {
            Add(tag, clear, false);
        }

        private static void Add(string tag, Action clear, bool onWipe)
        {
            if (string.IsNullOrEmpty(tag) || clear == null) return;
            if (!_tags.Add(tag))
            {
                ModRuntime.Log?.Warning("[SessionReset] duplicate tag '" + tag + "' ignored");
                return;
            }
            _entries.Add(new Entry { Tag = tag, Clear = clear, OnWipe = onWipe });
        }

        /// <summary>Runs every registered clear; one throwing never skips the others. Re-entrancy safe.</summary>
        public static void RunAll(string reason)
        {
            if (_running) return;
            _running = true;
            try
            {
                bool wipe = string.Equals(reason, ReasonWipe, StringComparison.Ordinal);
                for (int i = 0; i < _entries.Count; i++)
                {
                    var e = _entries[i];
                    if (wipe && !e.OnWipe) continue;
                    try { e.Clear(); }
                    catch (Exception ex) { Guard.Swallow("SessionReset." + e.Tag, ex); }
                }
            }
            finally
            {
                _running = false;
            }
        }
    }
}
