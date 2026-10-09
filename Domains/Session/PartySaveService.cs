// Party save snapshots. The host stamps every native SaveManager.Save with a token
// (slot + counter + host stamp) and snapshots the party key ring; every client snapshots its
// own 6-slot bag under the same token. A party wipe (or a late join into the same save)
// restores those snapshots instead of letting each peer load its own save slot.
// Persisted as plain text under MelonLoader UserData/SyncRADation/.
// Solo play stays vanilla: outside a hosted session the Save/Load postfixes only write two ints (no file IO, no
// token). The run is remembered in memory (_runSlot / _runDirty) and a token is minted when a host session starts.
using System;
using System.Collections.Generic;
using SyncRADation.ItemSystem;
using System.Globalization;
using System.IO;
using System.Text;
using SyncRADation.Sync;

namespace SyncRADation.Networking
{
    public struct PartySaveToken
    {
        public int Slot;
        public int Counter;
        public long Stamp;

        public bool Valid => Counter > 0;

        public string Key => Stamp.ToString("X", CultureInfo.InvariantCulture) + "-" + Slot + "-" + Counter;

        public static bool TryParse(string key, out PartySaveToken token)
        {
            token = default(PartySaveToken);
            if (string.IsNullOrEmpty(key)) return false;
            var parts = key.Split('-');
            if (parts.Length != 3) return false;
            long stamp;
            int slot, counter;
            if (!long.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out stamp)) return false;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out slot)) return false;
            if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out counter)) return false;
            token = new PartySaveToken { Slot = slot, Counter = counter, Stamp = stamp };
            return true;
        }
    }

    public struct BagEntry
    {
        public ushort Item;
        public int Count;
    }

    public static class PartySaveService
    {
        private const int MaxBagSnapshots = 24;
        private const string HostFile = "host_saves.txt";
        private const string LegacyBagFile = "bag_snapshots.txt";
        private const int MaxInstanceSlots = 16;

        // Token/ring/bag tables mirror files under UserData: persistent across sessions on purpose (Reset only drops Current/_bagAtDeath).
        // persistent: mirrors host_saves.txt
        private static bool _hostLoaded;
        // persistent: mirrors the bag file
        private static bool _bagsLoaded;
        // persistent: mirrors host_saves.txt
        private static long _stamp;
        // persistent: mirrors host_saves.txt
        private static int _next;
        // persistent: mirrors host_saves.txt
        private static readonly Dictionary<int, PartySaveToken> _slotTokens = new Dictionary<int, PartySaveToken>();
        // persistent: mirrors host_saves.txt
        private static readonly Dictionary<string, ushort[]> _rings = new Dictionary<string, ushort[]>();
        // Which save a slot's token was minted for (SaveFingerprint). A slot number exists once per profile
        // (FileBasedPrefs.profile "01".."05"), and a solo save over a party slot, or a game restart in between, keeps
        // the slot but is another save: its token / ring / bag snapshots must not be applied to it.
        // persistent: mirrors host_saves.txt
        private static readonly Dictionary<int, string> _slotPrints = new Dictionary<int, string>();
        // persistent: mirrors the bag file
        private static readonly List<string> _bagOrder = new List<string>();
        // persistent: mirrors the bag file
        private static readonly Dictionary<string, BagEntry[]> _bags = new Dictionary<string, BagEntry[]>();
        private static BagEntry[] _bagAtDeath;

        // Run tracking (solo + hosted): which slot this run was last saved to / loaded from.
        // persistent: the run outlives the session
        private static int _runSlot;
        // persistent: the run outlives the session
        private static bool _runDirty;
        // Fingerprint of the run's save at its last Save / Load (in memory, read from the live SProgress).
        // persistent: the run outlives the session
        private static string _runPrint = "";
        // Client: the join token restores a bag once per (host stamp, slot) (a reconnect to the same host and save mid-session
        // must not roll the bag back; a different host or save is a different run). Persistent on purpose: not a session value.
        // persistent: once per (host stamp, slot)
        private static bool _joinSeen;
        // persistent: once per (host stamp, slot)
        private static long _joinStamp;
        // persistent: once per (host stamp, slot)
        private static int _joinSlot;
        // Solo: key/object items in the bag at the last solo save / load (in memory, no IO). A solo-saved run is
        // minted into a token only when hosting starts, by which time PartyKeyRing.Reset() has emptied the ring.
        // persistent: solo run state, read when hosting starts
        private static ushort[] _soloKeys;
        // Per-process bag file slot (see BagPath): two client processes from one install never share a file.
        // persistent: per-process file slot
        private static int _instanceSlot = -1;
        // persistent: per-process file lock
        private static System.IO.FileStream _instanceLock;

        /// <summary>Host: token of the save the session is running from (set by Save / Load). Unused on clients.</summary>
        public static PartySaveToken Current;

        // ------------------------------------------------------------------ persistence

        private static string Dir()
        {
            string dir = Path.Combine(MelonLoader.MelonUtils.UserDataDirectory, "SyncRADation");
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// Stable per-process slot among processes sharing one install: the first free slot whose lock file we can
        /// hold exclusively for the process lifetime. A restarted client gets its old slot back (snapshot survives a
        /// restart); a concurrent second process gets another one (no last-writer-wins on one file).
        /// </summary>
        private static int InstanceSlot()
        {
            if (_instanceSlot >= 0) return _instanceSlot;
            _instanceSlot = 0;
            try
            {
                string dir = Dir();
                for (int n = 0; n < MaxInstanceSlots; n++)
                {
                    try
                    {
                        var fs = new FileStream(Path.Combine(dir, "bag_snapshots_" + n + ".lock"),
                            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                        _instanceLock = fs; // held until the process exits
                        _instanceSlot = n;
                        break;
                    }
                    catch (IOException ex) { Guard.Swallow("PartySave.InstanceSlotBusy", ex); } // held by another live process: next slot
                }
            }
            catch (Exception ex)
            {
                Guard.Swallow("PartySave.InstanceSlot", ex);
            }
            PlaytestLog.Event("PartySave", "bag snapshot instance slot " + _instanceSlot);
            return _instanceSlot;
        }

        private static string BagPath()
        {
            return Path.Combine(Dir(), "bag_snapshots_" + InstanceSlot() + ".txt");
        }

        private static void EnsureHostLoaded()
        {
            if (_hostLoaded) return;
            _hostLoaded = true;
            try
            {
                string path = Path.Combine(Dir(), HostFile);
                if (File.Exists(path))
                {
                    foreach (string raw in File.ReadAllLines(path))
                    {
                        string line = raw.Trim();
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq);
                        string v = line.Substring(eq + 1);
                        if (k == "stamp")
                            long.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _stamp);
                        else if (k == "next")
                            int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _next);
                        else if (k == "slot")
                        {
                            PartySaveToken t;
                            if (PartySaveToken.TryParse(v, out t)) _slotTokens[t.Slot] = t;
                        }
                        else if (k == "print")
                        {
                            int bar = v.IndexOf('|');
                            int slot;
                            if (bar > 0 && int.TryParse(v.Substring(0, bar), NumberStyles.Integer, CultureInfo.InvariantCulture, out slot))
                                _slotPrints[slot] = v.Substring(bar + 1);
                        }
                        else if (k == "ring")
                        {
                            int bar = v.IndexOf('|');
                            if (bar > 0)
                                _rings[v.Substring(0, bar)] = ParseRing(v.Substring(bar + 1));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[PartySave] host file read failed: " + ex.Message);
            }
            if (_stamp == 0)
            {
                _stamp = (DateTime.UtcNow.Ticks ^ (long)Environment.TickCount * 7919L) & 0x7FFFFFFFFFFFL;
                if (_stamp == 0) _stamp = 1;
                SaveHostFile();
            }
        }

        private static void SaveHostFile()
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("stamp=").Append(_stamp.ToString("X", CultureInfo.InvariantCulture)).Append('\n');
                sb.Append("next=").Append(_next).Append('\n');
                foreach (var kvp in _slotTokens)
                    sb.Append("slot=").Append(kvp.Value.Key).Append('\n');
                foreach (var kvp in _slotPrints)
                    sb.Append("print=").Append(kvp.Key).Append('|').Append(kvp.Value).Append('\n');
                foreach (var kvp in _rings)
                    sb.Append("ring=").Append(kvp.Key).Append('|').Append(JoinRing(kvp.Value)).Append('\n');
                File.WriteAllText(Path.Combine(Dir(), HostFile), sb.ToString());
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[PartySave] host file write failed: " + ex.Message);
            }
        }

        private static void EnsureBagsLoaded()
        {
            if (_bagsLoaded) return;
            _bagsLoaded = true;
            try
            {
                string path = BagPath();
                if (!File.Exists(path) && InstanceSlot() == 0)
                    path = Path.Combine(Dir(), LegacyBagFile); // pre-0.5.59 shared file: first process adopts it
                if (!File.Exists(path)) return;
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (!line.StartsWith("bag=", StringComparison.Ordinal)) continue;
                    string v = line.Substring(4);
                    int bar = v.IndexOf('|');
                    if (bar <= 0) continue;
                    string key = v.Substring(0, bar);
                    _bags[key] = ParseBag(v.Substring(bar + 1));
                    _bagOrder.Remove(key);
                    _bagOrder.Add(key);
                }
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[PartySave] bag file read failed: " + ex.Message);
            }
        }

        private static void SaveBagFile()
        {
            try
            {
                var sb = new StringBuilder();
                for (int i = 0; i < _bagOrder.Count; i++)
                {
                    BagEntry[] bag;
                    if (!_bags.TryGetValue(_bagOrder[i], out bag)) continue;
                    sb.Append("bag=").Append(_bagOrder[i]).Append('|').Append(JoinBag(bag)).Append('\n');
                }
                File.WriteAllText(BagPath(), sb.ToString());
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[PartySave] bag file write failed: " + ex.Message);
            }
        }

        private static ushort[] ParseRing(string csv)
        {
            var list = new List<ushort>();
            foreach (string part in csv.Split(','))
            {
                ushort u;
                if (ushort.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out u))
                    list.Add(u);
            }
            return list.ToArray();
        }

        private static string JoinRing(ushort[] ring)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < ring.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(ring[i]);
            }
            return sb.ToString();
        }

        private static BagEntry[] ParseBag(string csv)
        {
            var list = new List<BagEntry>();
            foreach (string part in csv.Split(','))
            {
                int colon = part.IndexOf(':');
                if (colon <= 0) continue;
                ushort item;
                int count;
                if (!ushort.TryParse(part.Substring(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out item)) continue;
                if (!int.TryParse(part.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out count)) continue;
                if (count > 0) list.Add(new BagEntry { Item = item, Count = count });
            }
            return list.ToArray();
        }

        private static string JoinBag(BagEntry[] bag)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < bag.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(bag[i].Item).Append(':').Append(bag[i].Count);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ host side

        private static int CurrentSlot()
        {
            try { return SaveManager.slotID; }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[PartySave] slotID read failed: " + ex.Message);
                return 1;
            }
        }

        /// <summary>
        /// The save the live SProgress holds: profile folder + SaveManager session id ("sid") + save count ("Saves"),
        /// all written by SaveManager.Save and read back by Load. "" when unreadable.
        /// </summary>
        private static string SaveFingerprint()
        {
            try
            {
                return (FileBasedPrefs.profile ?? "") + "/" + (SProgress.GetString("sid", "") ?? "") + "/"
                    + SProgress.GetInt("Saves", 0).ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception ex) { Guard.Swallow("PartySave.Fingerprint", ex); return ""; }
        }

        /// <summary>The slot's token, only when it was minted for the save the run holds (legacy tokens had no print).</summary>
        private static bool TryTokenForRun(int slot, out PartySaveToken token)
        {
            if (!_slotTokens.TryGetValue(slot, out token)) return false;
            string print;
            if (!_slotPrints.TryGetValue(slot, out print) || string.IsNullOrEmpty(print) || string.IsNullOrEmpty(_runPrint)
                || string.Equals(print, _runPrint, StringComparison.Ordinal))
                return true;
            PlaytestLog.Event("PartySave", "slot=" + slot + " token " + token.Key + " is for another save (" + print
                + " != " + _runPrint + "): not used");
            token = default(PartySaveToken);
            return false;
        }

        /// <summary>Hosted session: a native SaveManager.Save happened. Mints the party token for the slot.</summary>
        public static PartySaveToken OnHostSaved()
        {
            _runSlot = CurrentSlot();
            _runDirty = false;
            _runPrint = SaveFingerprint();
            return Mint(_runSlot, PartyKeyRing.Export());
        }

        private static PartySaveToken Mint(int slot, ushort[] ring)
        {
            EnsureHostLoaded();
            var token = new PartySaveToken { Slot = slot, Counter = ++_next, Stamp = _stamp };
            _slotTokens[slot] = token;
            _slotPrints[slot] = _runPrint ?? "";
            _rings[token.Key] = ring ?? new ushort[0];
            PruneRings();
            SaveHostFile();
            Current = token;
            PlaytestLog.Event("PartySave", "host saved token=" + token.Key + " ring=" + _rings[token.Key].Length);
            return token;
        }

        /// <summary>Solo (no hosted session): remember the run's slot in memory only. No file IO, no token.</summary>
        public static void NoteSoloSave()
        {
            _runSlot = CurrentSlot();
            _runDirty = true;
            _runPrint = SaveFingerprint();
            _soloKeys = CaptureBagKeys();
        }

        /// <summary>Hosted session: a real native SaveManager.Load happened. Session now runs from that slot's last token.</summary>
        public static void OnHostLoaded()
        {
            EnsureHostLoaded();
            int slot = CurrentSlot();
            _runSlot = slot;
            _runDirty = false;
            _runPrint = SaveFingerprint();
            PartySaveToken token;
            if (!TryTokenForRun(slot, out token))
                token = default(PartySaveToken);
            Current = token;
            PlaytestLog.Event("PartySave", "host loaded slot=" + slot
                + (token.Valid ? " token=" + token.Key : " (no party snapshot)"));
        }

        /// <summary>Solo: a real native Load happened. Remember the slot; the token (if any) is picked up at session start.</summary>
        public static void NoteSoloLoad()
        {
            _runSlot = CurrentSlot();
            _runDirty = false;
            _runPrint = SaveFingerprint();
            Current = default(PartySaveToken);
            _soloKeys = CaptureBagKeys();
        }

        /// <summary>SaveManager.NewGame (any role): this run has no save; a stale token must never be used for it.</summary>
        public static void OnNewGame()
        {
            _runSlot = 0;
            _runDirty = false;
            _runPrint = "";
            Current = default(PartySaveToken);
            _soloKeys = null;
        }

        /// <summary>
        /// Host session begins: adopt the run in memory. A save made in solo gets its token now (one file write);
        /// a run merely loaded from a slot reuses that slot's last token. Decision: solo saves are never stamped
        /// at save time (vanilla cost), but the token is available from the moment hosting starts.
        /// </summary>
        public static void OnSessionStart()
        {
            Current = default(PartySaveToken);
            if (_runSlot <= 0) return;
            if (_runDirty)
            {
                _runDirty = false;
                // The ring the bag held when the solo save was made, not the (now empty / later) live ring.
                Mint(_runSlot, _soloKeys);
                return;
            }
            EnsureHostLoaded();
            PartySaveToken token;
            if (TryTokenForRun(_runSlot, out token))
            {
                Current = token;
            }
            else if (_soloKeys != null)
            {
                // Loaded in solo from a slot that never had a party token: the keys the save restored are its ring.
                Mint(_runSlot, _soloKeys);
            }
        }

        /// <summary>Token of the last party save in a slot (invalid when none, or minted for another save than the run's).</summary>
        public static PartySaveToken TokenForSlot(int slot)
        {
            EnsureHostLoaded();
            PartySaveToken token;
            if (slot == _runSlot) return TryTokenForRun(slot, out token) ? token : default(PartySaveToken);
            return _slotTokens.TryGetValue(slot, out token) ? token : default(PartySaveToken);
        }

        /// <summary>Key ring as captured at the last party save (empty when none).</summary>
        public static ushort[] RingForCurrent()
        {
            EnsureHostLoaded();
            ushort[] ring;
            if (Current.Valid && _rings.TryGetValue(Current.Key, out ring))
                return ring;
            return new ushort[0];
        }

        private static void PruneRings()
        {
            var keep = new HashSet<string>();
            foreach (var kvp in _slotTokens)
                keep.Add(kvp.Value.Key);
            var drop = new List<string>();
            foreach (var kvp in _rings)
            {
                if (!keep.Contains(kvp.Key)) drop.Add(kvp.Key);
            }
            for (int i = 0; i < drop.Count; i++)
                _rings.Remove(drop[i]);
        }

        /// <summary>Host: unicast the token the session runs from to a joining client.</summary>
        public static void SendJoinToken(LanNetworkManager net, int targetPlayerId)
        {
            if (net == null || !NetGate.HostRole || targetPlayerId < 1) return;
            EnsureHostLoaded();
            if (!Current.Valid) return;
            net.PartyHandlers.SendPartySave(Current, PartySaveMessage.FlagJoin, targetPlayerId);
        }

        // ------------------------------------------------------------------ client side

        /// <summary>Client: host announced a save (snapshot bag) or the join token (restore if we have it).</summary>
        public static void OnHostAnnounced(PartySaveMessage msg)
        {
            var token = new PartySaveToken { Slot = msg.Slot, Counter = msg.Counter, Stamp = msg.Stamp };
            if (!token.Valid) return;
            EnsureBagsLoaded();

            if ((msg.Flags & PartySaveMessage.FlagJoin) != 0)
            {
                if (_joinSeen && _joinStamp == token.Stamp && _joinSlot == token.Slot)
                {
                    PlaytestLog.Event("PartySave", "rejoin: keep bag (token " + token.Key + ")");
                    return;
                }
                _joinSeen = true;
                _joinStamp = token.Stamp;
                _joinSlot = token.Slot;
                BagEntry[] bag;
                if (_bags.TryGetValue(token.Key, out bag))
                {
                    PlaytestLog.Event("PartySave", "join: restoring bag snapshot " + token.Key + " items=" + bag.Length);
                    RestoreBag(bag);
                }
                else
                {
                    PlaytestLog.Event("PartySave", "join: no bag snapshot for " + token.Key + " (keep bag)");
                }
                return;
            }

            SnapshotBag(token);
        }

        private static void SnapshotBag(PartySaveToken token)
        {
            // A downed peer already dropped its bag on the floor; the bag it held when it went
            // down is the truthful "as of this save" content.
            var bag = Players.NetworkDamageSystem.IsDead && _bagAtDeath != null ? _bagAtDeath : CaptureBag();
            string key = token.Key;
            _bags[key] = bag;
            _bagOrder.Remove(key);
            _bagOrder.Add(key);
            while (_bagOrder.Count > MaxBagSnapshots)
            {
                _bags.Remove(_bagOrder[0]);
                _bagOrder.RemoveAt(0);
            }
            SaveBagFile();
            PlaytestLog.Event("PartySave", "bag snapshot " + key + " items=" + bag.Length);
        }

        /// <summary>Latest snapshot for the token, else the bag held when this peer went down, else null.</summary>
        public static BagEntry[] ResolveWipeBag(PartySaveToken token, out string source)
        {
            EnsureBagsLoaded();
            BagEntry[] bag;
            if (token.Valid && _bags.TryGetValue(token.Key, out bag))
            {
                source = "snapshot " + token.Key;
                return bag;
            }
            if (_bagAtDeath != null)
            {
                source = "bag at down";
                return _bagAtDeath;
            }
            source = "none";
            return null;
        }

        public static void NoteBagAtDown()
        {
            _bagAtDeath = CaptureBag();
        }

        public static void ClearBagAtDown()
        {
            _bagAtDeath = null;
        }

        /// <summary>
        /// Network stop: forget the downed-bag capture and the session token. The run (slot / dirty flag) and the
        /// persisted token tables survive: hosting again from the same run re-adopts them in OnSessionStart.
        /// </summary>
        public static void Reset()
        {
            _bagAtDeath = null;
            Current = default(PartySaveToken);
        }

        // ------------------------------------------------------------------ bag capture / restore

        /// <summary>Bag contents minus Key/Object (the party ring is source of truth for uniques).</summary>
        public static BagEntry[] CaptureBag()
        {
            var list = new List<BagEntry>(8);
            try
            {
                // ItemBag reads counts by key (the KeyValuePair enumerator's values are garbage in this build).
                var bag = ItemBag.Bag(new List<ItemBag.Stack>(8));
                for (int i = 0; i < bag.Count; i++)
                {
                    var item = bag[i].Item;
                    int count = bag[i].Count;
                    if (item == null || count <= 0) continue;
                    Items.itemlist e;
                    try { e = item._item; } catch { continue; }
                    if (e == Items.itemlist.None) continue;
                    if (PartyKeyRing.IsKeyOrObject(item)) continue;
                    list.Add(new BagEntry { Item = (ushort)e, Count = count });
                }
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[PartySave] CaptureBag failed: " + ex.Message);
            }
            return list.ToArray();
        }

        /// <summary>Key/Object items currently in the bag (the keys a save made now would carry), in memory only.</summary>
        private static ushort[] CaptureBagKeys()
        {
            var list = new List<ushort>(4);
            try
            {
                var bag = ItemBag.Bag(new List<ItemBag.Stack>(8));
                for (int i = 0; i < bag.Count; i++)
                {
                    var item = bag[i].Item;
                    if (item == null || bag[i].Count <= 0) continue;
                    Items.itemlist e;
                    try { e = item._item; }
                    catch (Exception ex) { Guard.Swallow("PartySave.KeyItem", ex); continue; }
                    if (e == Items.itemlist.None || !PartyKeyRing.IsKeyOrObject(item)) continue;
                    if (!list.Contains((ushort)e)) list.Add((ushort)e);
                }
            }
            catch (Exception ex)
            {
                Guard.Swallow("PartySave.CaptureBagKeys", ex);
            }
            return list.ToArray();
        }

        /// <summary>Empty the local bag (keys too; party keys stay usable through the ring masquerade) and refill from entries.</summary>
        public static void RestoreBag(BagEntry[] entries)
        {
            if (entries == null) return;
            NetGate.BeginApply();
            try
            {
                var have = new List<AnItem>();
                var counts = new List<int>();
                var bag = ItemBag.Bag(new List<ItemBag.Stack>(8));
                for (int i = 0; i < bag.Count; i++)
                {
                    if (bag[i].Item == null || bag[i].Count <= 0) continue;
                    have.Add(bag[i].Item);
                    counts.Add(bag[i].Count);
                }
                for (int i = 0; i < have.Count; i++)
                {
                    try { InventoryManager.RemoveItem(have[i], counts[i]); }
                    catch (Exception ex) { ModRuntime.Log?.Warning("[PartySave] bag clear: " + ex.Message); }
                }
                try { InventoryManager.CurrentItem = null; }
                catch (Exception ex) { ModRuntime.Log?.Warning("[PartySave] CurrentItem reset: " + ex.Message); }

                int restored = 0;
                for (int i = 0; i < entries.Length; i++)
                {
                    try
                    {
                        var cat = InventoryManager.getItem((Items.itemlist)entries[i].Item);
                        if (cat == null) continue;
                        InventoryManager.AddItem(cat, entries[i].Count);
                        restored++;
                    }
                    catch (Exception ex)
                    {
                        ModRuntime.Log?.Warning("[PartySave] bag add " + entries[i].Item + ": " + ex.Message);
                    }
                }
                PlaytestLog.Event("PartySave", "bag restored " + restored + "/" + entries.Length);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[PartySave] RestoreBag failed: " + ex.Message);
            }
            finally
            {
                NetGate.EndApply();
            }
        }
    }
}
