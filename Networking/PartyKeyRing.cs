// Party-held unique keys/objects so UseItemInteraction works for either Elster.
using System.Collections.Generic;
using SyncRADation.Sync;

namespace SyncRADation.Networking
{
    public static class PartyKeyRing
    {
        private static readonly HashSet<ushort> _keys = new HashSet<ushort>();

        public static void Reset()
        {
            _keys.Clear();
            _bagAttempt.Clear();
        }

        public static bool IsKeyOrObject(Items.itemlist item)
        {
            if (item == Items.itemlist.None) return false;
            try
            {
                var an = InventoryManager.getItem(item);
                if (an == null) return false;
                return an.type == AnItem.AnItemType.Key || an.type == AnItem.AnItemType.Object;
            }
            catch { return false; }
        }

        public static bool IsKeyOrObject(AnItem item)
        {
            if (item == null) return false;
            try
            {
                return item.type == AnItem.AnItemType.Key || item.type == AnItem.AnItemType.Object;
            }
            catch
            {
                try { return IsKeyOrObject(item._item); } catch { return false; }
            }
        }

        public static bool Has(Items.itemlist item) => _keys.Contains((ushort)item);

        public static bool Has(AnItem item)
        {
            if (item == null) return false;
            try { return Has(item._item); } catch { return false; }
        }

        public static void Note(Items.itemlist item)
        {
            if (!IsKeyOrObject(item)) return;
            if (_keys.Add((ushort)item))
                PlaytestLog.Event("KeyRing", "note " + item + " count=" + _keys.Count);
        }

        public static void Note(AnItem item)
        {
            if (item == null || !IsKeyOrObject(item)) return;
            try { Note(item._item); } catch { }
        }

        public static void Remove(Items.itemlist item)
        {
            if (_keys.Remove((ushort)item))
                PlaytestLog.Event("KeyRing", "drop " + item + " count=" + _keys.Count);
        }

        public static void ApplyMessage(PartyKeyRingMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (msg.ItemEnums == null) return;

            if (net != null && net.Role == NetworkRole.Host)
            {
                bool added = false;
                for (int i = 0; i < msg.ItemEnums.Length; i++)
                {
                    var item = (Items.itemlist)msg.ItemEnums[i];
                    if (!IsKeyOrObject(item)) continue;
                    if (_keys.Add(msg.ItemEnums[i]))
                        added = true;
                }
                if (added)
                {
                    PlaytestLog.Event("KeyRing", "host merge count=" + _keys.Count);
                    Broadcast();
                }
                return;
            }

            _keys.Clear();
            for (int i = 0; i < msg.ItemEnums.Length; i++)
            {
                var item = (Items.itemlist)msg.ItemEnums[i];
                if (IsKeyOrObject(item))
                    _keys.Add(msg.ItemEnums[i]);
            }
            PlaytestLog.Event("KeyRing", "apply count=" + _keys.Count);
        }

        public static void Broadcast()
        {
            var net = LanNetworkManager.Instance;
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected) return;
            net.SendPartyKeyRing(Snapshot());
        }

        public static void OfferToHost(AnItem item)
        {
            Note(item);
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            if (net.Role == NetworkRole.Host)
            {
                Broadcast();
                return;
            }
            if (item == null) return;
            try
            {
                if (!IsKeyOrObject(item)) return;
                net.SendPartyKeyRing(new[] { (ushort)item._item });
            }
            catch { }
        }

        static ushort[] Snapshot()
        {
            var arr = new ushort[_keys.Count];
            int i = 0;
            foreach (var k in _keys)
                arr[i++] = k;
            return arr;
        }

        public static AnItem CatalogOf(AnItem item)
        {
            if (item == null) return null;
            try
            {
                var kind = item._item;
                if (kind != Items.itemlist.None)
                {
                    var cat = InventoryManager.getItem(kind);
                    if (cat != null) return cat;
                    return item;
                }
            }
            catch { }
            return MatchByName(item);
        }

        static AnItem MatchByName(AnItem item)
        {
            if (item == null) return null;
            string want = null;
            try { want = item._name; } catch { }
            if (string.IsNullOrEmpty(want)) return null;
            try
            {
                var all = InventoryManager.allItems;
                if (all == null) return null;
                var en = all.GetEnumerator();
                while (en.MoveNext())
                {
                    var cat = en.Current.value;
                    if (cat == null) continue;
                    try
                    {
                        if (cat._name == want)
                        {
                            en.Dispose();
                            return cat;
                        }
                    }
                    catch { }
                }
                en.Dispose();
            }
            catch { }
            return null;
        }

        public static bool BadLoc(string s)
        {
            if (string.IsNullOrEmpty(s)) return true;
            return s.IndexOf("MISSING STRING", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static string DisplayName(AnItem item)
        {
            var cat = CatalogOf(item) ?? item;
            if (cat == null)
            {
                try { cat = CatalogOf(UseItemInteraction.currentUseItem); } catch { }
            }
            if (cat == null) return null;
            try
            {
                var n = InventoryManager.getName(cat);
                if (!BadLoc(n)) return n;
            }
            catch { }
            try
            {
                var n = cat.localizedName();
                if (!BadLoc(n)) return n;
            }
            catch { }
            try
            {
                if (!string.IsNullOrEmpty(cat._name) && !BadLoc(cat._name))
                    return cat._name;
            }
            catch { }
            return null;
        }

        public const int DialoguerKeyNameId = 3;

        public static void BindUseDialogue(AnItem item)
        {
            var cat = CatalogOf(item);
            if (cat != null)
            {
                try { UseItemInteraction.currentUseItem = cat; } catch { }
                try
                {
                    if (item != null && item != cat && string.IsNullOrEmpty(item._name)
                        && !string.IsNullOrEmpty(cat._name))
                        item._name = cat._name;
                }
                catch { }
            }
            string name = DisplayName(cat ?? item);
            if (string.IsNullOrEmpty(name)) return;
            try { Dialoguer.SetGlobalString(DialoguerKeyNameId, name); } catch { }
            try { Dialoguer.SetGlobalString(0, name); } catch { }
        }

        public static AnItem FindInBag(AnItem item)
        {
            if (item == null) return null;
            try
            {
                var dict = InventoryManager.elsterItems;
                if (dict == null) return null;
                Items.itemlist want = item._item;
                var en = dict.GetEnumerator();
                while (en.MoveNext())
                {
                    var held = en.Current.key;
                    if (held != null && en.Current.value > 0 && held._item == want)
                    {
                        en.Dispose();
                        return held;
                    }
                }
                en.Dispose();
            }
            catch { }
            return null;
        }

        public static bool InLocalBag(AnItem item) => FindInBag(item) != null;

        static bool _ensuringBag;
        static readonly HashSet<ushort> _bagAttempt = new HashSet<ushort>();

        /// <summary>
        /// Put a party-ring key into the local 6-slot bag so native UseItem / cutscene
        /// run as if Elster was carrying it. No-op if already held, not on the ring,
        /// or the bag already refused once this session.
        /// </summary>
        public static bool EnsureInBag(AnItem item)
        {
            if (_ensuringBag) return InLocalBag(item);
            var cat = CatalogOf(item) ?? item;
            if (cat == null) return false;
            if (InLocalBag(cat)) return true;
            if (!Has(cat)) return false;
            ushort id = (ushort)cat._item;
            if (!_bagAttempt.Add(id))
                return false;
            _ensuringBag = true;
            try
            {
                InventoryManager.AddItem(cat, 1);
            }
            catch { }
            finally { _ensuringBag = false; }
            bool ok = InLocalBag(cat);
            if (ok)
                PlaytestLog.Event("KeyRing", "ensure bag " + cat._item);
            return ok;
        }

        public static bool LocalOrRingHas(AnItem item)
        {
            if (item == null) return false;
            return InLocalBag(item) || Has(item);
        }
    }
}
