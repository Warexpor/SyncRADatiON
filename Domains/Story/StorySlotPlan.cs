// Pure planning for a batch of story flags written into one SProgress slot kind (no Unity / Il2Cpp references:
// compiled into the unit tests). ProgressSlot applies the plan to the live Il2Cpp lists.
using System.Collections.Generic;
using UnityEngine;

namespace SyncRADation.Networking
{
    internal static class StorySlotPlan
    {
        /// <summary>One list write: set the value at Index, or append value + key when Index is -1.</summary>
        public struct Write
        {
            public int Index;
            public StoryFlagEntry Entry;
        }

        /// <summary>
        /// Writes that bring <paramref name="keys"/> (one kind's key list, values read through <paramref name="current"/>)
        /// to <paramref name="incoming"/> (entries of that kind). Native Get* / Set* stop at the first matching key
        /// (SProgress.c), so the first occurrence is the one compared and written. Unchanged keys produce no write; a key
        /// missing from the list is appended once (a repeat of it in the batch then sets the appended slot).
        /// </summary>
        public static List<Write> Plan(IList<string> keys, System.Func<int, StoryFlagEntry> current, IList<StoryFlagEntry> incoming)
        {
            var index = new Dictionary<string, int>(keys.Count + incoming.Count, System.StringComparer.Ordinal);
            for (int i = 0; i < keys.Count; i++)
            {
                string k = keys[i];
                if (k != null && !index.ContainsKey(k)) index[k] = i;
            }
            var writes = new List<Write>();
            int next = keys.Count;
            var planned = new Dictionary<int, int>(); // list index -> writes[] slot already writing it in this batch
            for (int i = 0; i < incoming.Count; i++)
            {
                var e = incoming[i];
                if (string.IsNullOrEmpty(e.Key)) continue;
                int at, slot;
                if (!index.TryGetValue(e.Key, out at))
                {
                    index[e.Key] = next;
                    planned[next] = writes.Count;
                    writes.Add(new Write { Index = -1, Entry = e });
                    next++;
                    continue;
                }
                if (planned.TryGetValue(at, out slot))
                {
                    // Written earlier in this batch: the newer value replaces it in the same write.
                    var w = writes[slot];
                    w.Entry = e;
                    writes[slot] = w;
                    continue;
                }
                if (SameValue(current(at), e)) continue;
                planned[at] = writes.Count;
                writes.Add(new Write { Index = at, Entry = e });
            }
            return writes;
        }

        /// <summary>Same stored value for the entry's kind (floats / vectors compare like Mathf.Approximately).</summary>
        public static bool SameValue(StoryFlagEntry a, StoryFlagEntry b)
        {
            if (a.Kind != b.Kind) return false;
            switch (a.Kind)
            {
                case 0: return a.BoolVal == b.BoolVal;
                case 1: return a.IntVal == b.IntVal;
                case 2: return Mathf.Approximately(a.FloatVal, b.FloatVal);
                case 3: return string.Equals(a.StringVal ?? "", b.StringVal ?? "", System.StringComparison.Ordinal);
                case 4:
                    return Mathf.Approximately(a.FloatVal, b.FloatVal) && Mathf.Approximately(a.VecY, b.VecY)
                        && Mathf.Approximately(a.VecZ, b.VecZ);
            }
            return false;
        }
    }
}
