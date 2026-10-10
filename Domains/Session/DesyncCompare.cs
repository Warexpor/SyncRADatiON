// Compare rules of the live desync check (DesyncCheck) and of scripts/pilot/digest-diff.py, on "section key value" lines.
// Pure (no Unity, no game types): unit tested.
using System;
using System.Collections.Generic;

namespace SyncRADation.Networking
{
    /// <summary>The compare rules of scripts/pilot/digest-diff.py, on "section key value" lines. Pure: unit tested.</summary>
    public static class DesyncCompare
    {
        /// <summary>Puzzle types whose state is each player's own by design (docs/SYNC.md).</summary>
        // persistent: constant table
        static readonly HashSet<string> PerPlayerTypes = new HashSet<string> { "EnemyManagerState" };

        static void Load(List<string> lines, Dictionary<string, string> into)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                string l = lines[i];
                if (string.IsNullOrEmpty(l)) continue;
                int a = l.IndexOf(' ');
                if (a < 0) { into[l + " "] = ""; continue; }
                int b = l.IndexOf(' ', a + 1);
                if (l.StartsWith("self ", StringComparison.Ordinal)) continue;
                if (b < 0) into[l] = "";
                else into[l.Substring(0, b)] = l.Substring(b + 1);
            }
        }

        /// <summary>
        /// Fills <paramref name="diffs"/> with "section key" → "host=X client=Y" for every line that really differs.
        /// </summary>
        public static void Diff(List<string> host, List<string> client, Dictionary<string, string> diffs, out int compared, out int skipped)
        {
            var h = new Dictionary<string, string>(host.Count);
            var c = new Dictionary<string, string>(client.Count);
            Load(host, h);
            Load(client, c);
            compared = 0;
            skipped = 0;
            foreach (var kv in h)
            {
                string cv;
                bool has = c.TryGetValue(kv.Key, out cv);
                One(kv.Key, kv.Value, has ? cv : null, diffs, ref compared, ref skipped);
            }
            foreach (var kv in c)
                if (!h.ContainsKey(kv.Key))
                    One(kv.Key, null, kv.Value, diffs, ref compared, ref skipped);
        }

        static void One(string key, string hv, string cv, Dictionary<string, string> diffs, ref int compared, ref int skipped)
        {
            string showH = hv, showC = cv;
            string section = key.Substring(0, key.IndexOf(' '));
            string name = key.Substring(section.Length + 1);
            if (section == "pickup")
            {
                // The taker destroyed its prop, the others hide it; a prop a peer never scanned was already gone there.
                hv = hv == "here" ? "here" : "taken";
                cv = cv == "here" ? "here" : "taken";
            }
            if (section == "enemy" && hv != null && cv != null)
            {
                // The trailing name is for reading: a room's enemies are renamed once that room's manager started.
                hv = DropLastWord(hv);
                cv = DropLastWord(cv);
            }
            if (section == "puzzle" && name.StartsWith("AraNest:", StringComparison.Ordinal) && hv != null && cv != null)
            {
                // b=<triggered><activated><dead>: activated is this peer's own player being in the nest's room.
                hv = MaskSecondBool(hv);
                cv = MaskSecondBool(cv);
            }
            if (hv == cv) { compared++; return; }
            if (section == "puzzle")
            {
                int colon = name.IndexOf(':');
                if (colon > 0 && PerPlayerTypes.Contains(name.Substring(0, colon))) { skipped++; return; }
            }
            if (hv != null && cv != null)
            {
                // Only differs where one peer never woke that room: native Start / the held re-snap settles it on entry.
                if (hv.EndsWith(" asleep", StringComparison.Ordinal) || cv.EndsWith(" asleep", StringComparison.Ordinal)) { skipped++; return; }
                if (hv.IndexOf("=asleep", StringComparison.Ordinal) >= 0 || cv.IndexOf("=asleep", StringComparison.Ordinal) >= 0)
                {
                    // A field only one peer has awake: compare the rest.
                    if (WithoutAsleepFields(hv, hv, cv) == WithoutAsleepFields(cv, hv, cv)) { skipped++; return; }
                }
            }
            compared++;
            diffs[key] = "host=" + (showH ?? "(none)") + " client=" + (showC ?? "(none)");
        }

        static string DropLastWord(string v)
        {
            int i = v.LastIndexOf(' ');
            return i < 0 ? v : v.Substring(0, i);
        }

        static string MaskSecondBool(string v)
        {
            var parts = v.Split(' ');
            for (int i = 0; i < parts.Length; i++)
                if (parts[i].StartsWith("b=", StringComparison.Ordinal) && parts[i].Length >= 5)
                    parts[i] = parts[i].Substring(0, 3) + "-" + parts[i].Substring(4);
            return string.Join(" ", parts);
        }

        static string WithoutAsleepFields(string v, string a, string b)
        {
            var drop = new HashSet<string>();
            foreach (string src in new[] { a, b })
                foreach (string t in src.Split(' '))
                    if (t.EndsWith("=asleep", StringComparison.Ordinal)) drop.Add(t.Substring(0, t.IndexOf('=')));
            var keep = new List<string>();
            foreach (string t in v.Split(' '))
            {
                int eq = t.IndexOf('=');
                if (eq > 0 && drop.Contains(t.Substring(0, eq))) continue;
                keep.Add(t);
            }
            return string.Join(" ", keep.ToArray());
        }
    }
}
