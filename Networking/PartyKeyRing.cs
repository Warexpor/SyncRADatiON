// Party-held unique keys/objects so UseItemInteraction works for either Elster.
using System.Collections.Generic;
using SyncRADation.Sync;

namespace SyncRADation.Networking
{
    public static class PartyKeyRing
    {
        private static readonly HashSet<ushort> _keys = new HashSet<ushort>();

        public static void Reset() => _keys.Clear();

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

        public static void Remove(Items.itemlist item) => _keys.Remove((ushort)item);

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

        public static bool LocalOrRingHas(AnItem item)
        {
            if (item == null) return false;
            return InLocalBag(item) || Has(item);
        }
    }
}
