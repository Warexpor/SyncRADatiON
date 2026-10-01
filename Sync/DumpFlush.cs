// Flush-before-unicast hooks. A join / resync dump to one peer (SessionNetHandlers, BeginUnicast) runs each domain's
// full send, and a full send records "already sent" state. A change still waiting for its next broadcast diff would then
// reach only the joiner. Before every unicast dump with other ready peers, RunAll sends each domain's pending diff to
// everyone. Domains register their flush once at boot (Bootstrap/DumpFlushRegistrations.cs or their own init):
//   DumpFlush.Register("Story", () => ...);
// A flush must send to all peers (it runs before BeginUnicast) and be a no-op when nothing is pending.
using System;
using System.Collections.Generic;

namespace SyncRADation.Sync
{
    public static class DumpFlush
    {
        private struct Hook
        {
            public string Name;
            public Action Flush;
        }

        // persistent: boot-time hook list
        private static readonly List<Hook> _hooks = new List<Hook>(8);
        // persistent: name set of the hook list above
        private static readonly HashSet<string> _names = new HashSet<string>();
        // persistent: re-entrancy guard, always false between runs
        private static bool _running;

        public static int Count => _hooks.Count;

        /// <summary>Register one domain's "send my pending diff to everyone". Duplicate / empty names and null actions are ignored.</summary>
        public static void Register(string name, Action flush)
        {
            if (string.IsNullOrEmpty(name) || flush == null) return;
            if (!_names.Add(name))
            {
                ModRuntime.Log?.Warning("[DumpFlush] duplicate hook '" + name + "' ignored");
                return;
            }
            _hooks.Add(new Hook { Name = name, Flush = flush });
        }

        /// <summary>Every hook in registration order, each isolated. Re-entrancy safe.</summary>
        public static void RunAll()
        {
            if (_running) return;
            _running = true;
            try
            {
                for (int i = 0; i < _hooks.Count; i++)
                {
                    try { _hooks[i].Flush(); }
                    catch (Exception ex) { Guard.Swallow("DumpFlush." + _hooks[i].Name, ex); }
                }
            }
            finally
            {
                _running = false;
            }
        }
    }
}
