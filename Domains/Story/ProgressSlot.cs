// Direct reads / writes of the live SProgress slot: ProgressSlotBehaviour keeps one key list and one parallel value
// list per kind (bool / int / float / string / Vector3).
// Native Set* (SProgress.c SetBool): Init, AddKey (only while SProgress.fallback), a linear search for the first matching
// key, set that value or append value then key, Autosave when saveEveryUpdate. A full story commit through Set* cost a
// native search plus the SProgressPatches prefix per key (O(keys x slot)); the bulk path indexes each kind once and
// writes its lists directly (O(keys + slot)). It is only taken while neither fallback nor saveEveryUpdate asks for the
// extra native work.
using System.Collections.Generic;
using SyncRADation.Sync;
using UnityEngine;
using IL = Il2CppSystem.Collections.Generic;

namespace SyncRADation.Networking
{
    internal static class ProgressSlot
    {
        // A commit this small stays on the native per-key path: marshalling a kind's whole key list costs more than a
        // handful of native searches.
        const int BulkMin = 16;

        static StoryFlagEntry B(string k, bool v) => new StoryFlagEntry { Kind = 0, Key = k, BoolVal = v };
        static StoryFlagEntry I(string k, int v) => new StoryFlagEntry { Kind = 1, Key = k, IntVal = v };
        static StoryFlagEntry F(string k, float v) => new StoryFlagEntry { Kind = 2, Key = k, FloatVal = v };
        static StoryFlagEntry S(string k, string v) => new StoryFlagEntry { Kind = 3, Key = k, StringVal = v ?? "" };
        static StoryFlagEntry V(string k, Vector3 v) =>
            new StoryFlagEntry { Kind = 4, Key = k, FloatVal = v.x, VecY = v.y, VecZ = v.z };

        /// <summary>One write through the native setter (and so through SProgressPatches).</summary>
        internal static void SetNative(StoryFlagEntry e)
        {
            switch (e.Kind)
            {
                case 0: SProgress.SetBool(e.Key, e.BoolVal); break;
                case 1: SProgress.SetInt(e.Key, e.IntVal); break;
                case 2: SProgress.SetFloat(e.Key, e.FloatVal); break;
                case 3: SProgress.SetString(e.Key, e.StringVal ?? ""); break;
                case 4: SProgress.SetVector(e.Key, new Vector3(e.FloatVal, e.VecY, e.VecZ)); break;
            }
        }

        /// <summary>
        /// Host: <paramref name="into"/> becomes the slot's shared keys (per-player keys stay out). False when the slot
        /// could not be read completely; the table is then empty, never a partial copy.
        /// </summary>
        internal static bool ReadShared(Dictionary<string, StoryFlagEntry> into)
        {
            into.Clear();
            try
            {
                var p = SProgress.progress;
                if (p == null) return true;
                Read(p.boolKeys, p.bools, into, B);
                Read(p.intKeys, p.ints, into, I);
                Read(p.floatKeys, p.floats, into, F);
                Read(p.stringKeys, p.strings, into, S);
                Read(p.vectorKeys, p.vectors, into, V);
                return true;
            }
            catch (System.Exception ex) { StorySyncService.WarnOnce("ProgressSlot.ReadShared", ex); }
            into.Clear();
            return false;
        }

        static void Read<T>(IL.List<string> keys, IL.List<T> vals, Dictionary<string, StoryFlagEntry> into,
            System.Func<string, T, StoryFlagEntry> make)
        {
            if (keys == null || vals == null) return;
            int n = Mathf.Min(keys.Count, vals.Count);
            for (int i = 0; i < n; i++)
            {
                string k = keys[i];
                if (!string.IsNullOrEmpty(k) && !StoryWire.IsPerPlayerKey(k)) into[k] = make(k, vals[i]);
            }
        }

        /// <summary>
        /// Client: write a commit's flags into the slot, skipping keys that already hold the value. Returns false when a
        /// kind failed part-way (the others are still written); <paramref name="written"/> counts the changed keys.
        /// </summary>
        internal static bool Apply(StoryFlagEntry[] flags, out int written)
        {
            written = 0;
            if (flags == null || flags.Length == 0) return true;
            var p = SProgress.progress;
            if (flags.Length < BulkMin || p == null || SProgress.fallback || SProgress.saveEveryUpdate)
                return ApplyNative(flags, ref written);
            bool ok = ApplyKind(p.boolKeys, p.bools, flags, 0, B, e => e.BoolVal, ref written);
            ok &= ApplyKind(p.intKeys, p.ints, flags, 1, I, e => e.IntVal, ref written);
            ok &= ApplyKind(p.floatKeys, p.floats, flags, 2, F, e => e.FloatVal, ref written);
            ok &= ApplyKind(p.stringKeys, p.strings, flags, 3, S, e => e.StringVal ?? "", ref written);
            ok &= ApplyKind(p.vectorKeys, p.vectors, flags, 4, V, e => new Vector3(e.FloatVal, e.VecY, e.VecZ), ref written);
            return ok;
        }

        static bool ApplyNative(StoryFlagEntry[] flags, ref int written)
        {
            bool ok = true;
            for (int i = 0; i < flags.Length; i++)
            {
                var f = flags[i];
                if (string.IsNullOrEmpty(f.Key) || StorySyncService.SameAsLocal(f)) continue;
                try
                {
                    SetNative(f);
                    written++;
                }
                catch (System.Exception ex)
                {
                    ok = false;
                    StorySyncService.WarnOnce("ProgressSlot.SetNative", ex);
                }
            }
            return ok;
        }

        static bool ApplyKind<T>(IL.List<string> keys, IL.List<T> vals, StoryFlagEntry[] flags, byte kind,
            System.Func<string, T, StoryFlagEntry> make, System.Func<StoryFlagEntry, T> value, ref int written)
        {
            List<StoryFlagEntry> mine = null;
            for (int i = 0; i < flags.Length; i++)
            {
                if (flags[i].Kind == kind) (mine ?? (mine = new List<StoryFlagEntry>())).Add(flags[i]);
            }
            if (mine == null) return true;
            try
            {
                var keyArr = new string[keys.Count];
                for (int i = 0; i < keyArr.Length; i++) keyArr[i] = keys[i];
                var writes = StorySlotPlan.Plan(keyArr, i => make(keyArr[i], vals[i]), mine);
                for (int i = 0; i < writes.Count; i++)
                {
                    var w = writes[i];
                    if (w.Index >= 0)
                        vals[w.Index] = value(w.Entry);
                    else
                    {
                        // Native order: value first, then key.
                        vals.Add(value(w.Entry));
                        keys.Add(w.Entry.Key);
                    }
                    written++;
                }
                return true;
            }
            catch (System.Exception ex) { StorySyncService.WarnOnce("ProgressSlot.Apply kind " + kind, ex); }
            return false;
        }

        /// <summary>
        /// Client, authoritative full commit: empty the slot of every shared key (what a fresh slot starts with). slotID
        /// and per-player keys (minimap, radio frequency, inventory slot, enemy saves, ...) stay: the host never sends them.
        /// </summary>
        internal static bool ClearShared()
        {
            try
            {
                var p = SProgress.progress;
                if (p == null) return true;
                KeepPerPlayer(p.boolKeys, p.bools);
                KeepPerPlayer(p.intKeys, p.ints);
                KeepPerPlayer(p.floatKeys, p.floats);
                KeepPerPlayer(p.stringKeys, p.strings);
                KeepPerPlayer(p.vectorKeys, p.vectors);
                return true;
            }
            catch (System.Exception ex) { StorySyncService.WarnOnce("ProgressSlot.ClearShared", ex); }
            return false;
        }

        // Rebuild instead of RemoveAt per key (each RemoveAt shifts the rest of the list): read what stays, then clear
        // and re-add it, so the parallel lists stay aligned.
        static void KeepPerPlayer<T>(IL.List<string> keys, IL.List<T> vals)
        {
            if (keys == null || vals == null) return;
            int n = Mathf.Min(keys.Count, vals.Count);
            var keepKeys = new List<string>();
            var keepVals = new List<T>();
            for (int i = 0; i < n; i++)
            {
                string k = keys[i];
                if (!StoryWire.IsPerPlayerKey(k)) continue;
                keepKeys.Add(k);
                keepVals.Add(vals[i]);
            }
            if (keepKeys.Count == keys.Count && keepVals.Count == vals.Count) return;
            vals.Clear();
            keys.Clear();
            for (int i = 0; i < keepKeys.Count; i++)
            {
                vals.Add(keepVals[i]);
                keys.Add(keepKeys[i]);
            }
        }
    }
}
