// Party-held unique keys/objects so UseItemInteraction works for either Elster.
using System.Collections.Generic;
using SyncRADation.Sync;

namespace SyncRADation.Networking
{
    public static class PartyKeyRing
    {
        private static readonly HashSet<ushort> _keys = new HashSet<ushort>();

        public static void Reset() => _keys.Clear();

        public static bool Has(Items.itemlist item) => _keys.Contains((ushort)item);

        public static bool Has(AnItem item)
        {
            if (item == null) return false;
            try { return Has(item._item); } catch { return false; }
        }

        public static void Note(Items.itemlist item)
        {
            if (item == Items.itemlist.None) return;
            if (_keys.Add((ushort)item))
                PlaytestLog.Event("KeyRing", "note " + item + " count=" + _keys.Count);
        }

        public static void Note(AnItem item)
        {
            if (item == null) return;
            try
            {
                if (item.type == AnItem.AnItemType.Key || item.type == AnItem.AnItemType.Object)
                    Note(item._item);
            }
            catch
            {
                try { Note(item._item); } catch { }
            }
        }

        public static void Remove(Items.itemlist item) => _keys.Remove((ushort)item);

        public static void ApplyMessage(PartyKeyRingMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (net != null && net.Role == NetworkRole.Host) return;
            _keys.Clear();
            if (msg.ItemEnums == null) return;
            for (int i = 0; i < msg.ItemEnums.Length; i++)
                _keys.Add(msg.ItemEnums[i]);
            PlaytestLog.Event("KeyRing", "apply count=" + _keys.Count);
        }

        public static void Broadcast()
        {
            var net = LanNetworkManager.Instance;
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected) return;
            var arr = new ushort[_keys.Count];
            int i = 0;
            foreach (var k in _keys)
                arr[i++] = k;
            net.SendPartyKeyRing(arr);
        }

        public static bool InLocalBag(AnItem item)
        {
            if (item == null) return false;
            try
            {
                var dict = InventoryManager.elsterItems;
                if (dict == null) return false;
                var en = dict.GetEnumerator();
                while (en.MoveNext())
                {
                    if (en.Current.key == item && en.Current.value > 0)
                    {
                        en.Dispose();
                        return true;
                    }
                }
                en.Dispose();
            }
            catch { }
            return false;
        }

        public static bool LocalOrRingHas(AnItem item)
        {
            if (item == null) return false;
            return InLocalBag(item) || Has(item);
        }
    }
}
