// The one place that knows how to forget state. Every static (or per-instance service) value that must not leak from one
// scene, one party-wipe reload or one network session into the next registers a step here with the scopes it belongs to;
// statics that are intentionally persistent carry a "// persistent: <reason>" line above the declaration instead
// (StaticStateGuardTests enforces one or the other). All registrations live in Bootstrap/SessionResetRegistrations.cs.
//
// Scopes (a step may carry several):
//   Scene      : RunScene, from LanNetworkManager.OnSceneChanged on every scene load (after the WorldId scan and the
//                registry rebuild in ModRuntime.OnSceneChanged), before the scene hello / world dump is sent.
//   Session    : RunAll on StopNetwork (which StartHost / ConnectToHost run first) and after a host wipe reload.
//   Connection : RunAll on StopNetwork only. A wipe keeps the same party: the session itself, and state that the save
//                reload just restored (bag, key ring), must survive it.
// Order: steps run in registration order inside one run, each isolated (a throwing step never skips the rest).
using System;
using System.Collections.Generic;

namespace SyncRADation.Sync
{
    [Flags]
    public enum ResetScope
    {
        None = 0,
        /// <summary>Every scene load (RunScene).</summary>
        Scene = 1,
        /// <summary>Network stop / start and after a host wipe reload (RunAll stop + wipe).</summary>
        Session = 2,
        /// <summary>Network stop / start only (RunAll stop).</summary>
        Connection = 4,
    }

    public static class SessionReset
    {
        public const string ReasonStop = "stop";
        public const string ReasonWipe = "wipe";

        private sealed class Entry
        {
            public string Tag;
            public ResetScope Scope;
            public Action Clear;
            /// <summary>Scene-only steps that need the scene being entered.</summary>
            public Action<string> OnScene;
        }

        // persistent: the registry itself (filled once at boot by SessionResetRegistrations)
        private static readonly List<Entry> _entries = new List<Entry>(96);
        // persistent: tag set of the registry above
        private static readonly HashSet<string> _tags = new HashSet<string>();
        // persistent: re-entrancy guard, always false between runs
        private static bool _runningAll;
        // persistent: re-entrancy guard, always false between runs
        private static bool _runningScene;

        public static int Count => _entries.Count;

        /// <summary>Steps carrying the given scope (boot log / tests).</summary>
        public static int CountOf(ResetScope scope)
        {
            int n = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                if ((_entries[i].Scope & scope) != 0) n++;
            }
            return n;
        }

        /// <summary>One step in one or more scopes. Duplicate tags, empty tags, null actions and ResetScope.None are ignored.</summary>
        public static void Register(string tag, ResetScope scope, Action clear)
        {
            if (clear == null || scope == ResetScope.None) return;
            Add(new Entry { Tag = tag, Scope = scope, Clear = clear });
        }

        /// <summary>Scene-scope step that needs the name of the scene being entered.</summary>
        public static void RegisterScene(string tag, Action<string> onScene)
        {
            if (onScene == null) return;
            Add(new Entry { Tag = tag, Scope = ResetScope.Scene, OnScene = onScene });
        }

        private static void Add(Entry e)
        {
            if (string.IsNullOrEmpty(e.Tag)) return;
            if (!_tags.Add(e.Tag))
            {
                ModRuntime.Log?.Warning("[SessionReset] duplicate tag '" + e.Tag + "' ignored");
                return;
            }
            _entries.Add(e);
        }

        /// <summary>
        /// Network stop (reason stop: Session + Connection steps) or host wipe reload (reason wipe: Session steps).
        /// One throwing step never skips the others. Re-entrancy safe.
        /// </summary>
        public static void RunAll(string reason)
        {
            if (_runningAll) return;
            _runningAll = true;
            try
            {
                bool wipe = string.Equals(reason, ReasonWipe, StringComparison.Ordinal);
                ResetScope mask = wipe ? ResetScope.Session : (ResetScope.Session | ResetScope.Connection);
                Run(mask, null, "");
            }
            finally
            {
                _runningAll = false;
            }
        }

        /// <summary>Scene load: every Scene step in registration order. Re-entrancy safe.</summary>
        public static void RunScene(string scene)
        {
            if (_runningScene) return;
            _runningScene = true;
            try
            {
                Run(ResetScope.Scene, scene ?? "", "scene:");
            }
            finally
            {
                _runningScene = false;
            }
        }

        private static void Run(ResetScope mask, string scene, string prefix)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                if ((e.Scope & mask) == 0) continue;
                try
                {
                    if (e.OnScene != null) { if (scene != null) e.OnScene(scene); }
                    else e.Clear();
                }
                catch (Exception ex) { Guard.Swallow("SessionReset." + prefix + e.Tag, ex); }
            }
        }
    }
}
