// Bag trace: one [Bag] line whenever the local 6-slot inventory changes (+item / -item with counts), so a
// pickup that "vanished" shows whether it ever reached the bag and what removed it. Polled twice a second
// while connected, Diagnostics pref only.
using System.Collections.Generic;
using SyncRADation.Config;
using SyncRADation.Networking;
using UnityEngine;

namespace SyncRADation.Sync
{
    public static class BagTrace
    {
        const float Interval = 0.5f;
        static float _next;
        static bool _primed;
        static readonly Dictionary<int, int> _last = new Dictionary<int, int>();
        static readonly Dictionary<int, int> _now = new Dictionary<int, int>();

        public static void Reset()
        {
            _primed = false;
            _last.Clear();
        }

        public static void Tick()
        {
            if (!ModConfig.DiagnosticsOn) return;
            if (!NetGate.Live) { _primed = false; return; }
            float t = Time.unscaledTime;
            if (t < _next) return;
            _next = t + Interval;

            _now.Clear();
            var dict = InventoryManager.elsterItems;
            if (dict == null) return;
            // Walk the keys and ask the native getCount: the Il2Cpp KeyValuePair enumerator returns garbage
            // values here ("None x2090114272").
            var en = dict.Keys.GetEnumerator();
            int count = dict.Count;
            for (int step = 0; step < count && en.MoveNext(); step++)
            {
                var key = en.Current;
                if (key == null) continue;
                int id = (int)key._item;
                if (id == (int)Items.itemlist.None) continue;
                int n = InventoryManager.getCount(key);
                if (n <= 0) continue;
                int c;
                _now.TryGetValue(id, out c);
                _now[id] = c + n;
            }

            if (!_primed)
            {
                _primed = true;
                Swap();
                PlaytestLog.Event("Bag", "start " + Describe(_last));
                return;
            }

            string diff = "";
            foreach (var kvp in _now)
            {
                int before;
                _last.TryGetValue(kvp.Key, out before);
                if (kvp.Value != before)
                    diff += " " + (kvp.Value > before ? "+" : "-") + (Items.itemlist)kvp.Key + " " + before + "->" + kvp.Value;
            }
            foreach (var kvp in _last)
            {
                if (!_now.ContainsKey(kvp.Key))
                    diff += " -" + (Items.itemlist)kvp.Key + " " + kvp.Value + "->0";
            }
            Swap();
            if (diff.Length > 0)
                PlaytestLog.Event("Bag", "change" + diff + " | ring=" + (PartyKeyRing.Export()?.Length ?? 0));
        }

        static void Swap()
        {
            _last.Clear();
            foreach (var kvp in _now) _last[kvp.Key] = kvp.Value;
        }

        static string Describe(Dictionary<int, int> d)
        {
            if (d.Count == 0) return "(empty)";
            var s = "";
            foreach (var kvp in d) s += (Items.itemlist)kvp.Key + "x" + kvp.Value + " ";
            return s.TrimEnd();
        }
    }
}
