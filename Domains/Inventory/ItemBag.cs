// The local 6-slot bag (InventoryManager.elsterItems) and the shared box (boxItems): one walk, one count, one room check.
using System.Collections.Generic;
using SyncRADation.Networking;
using SyncRADation.Sync;
using ItemDict = Il2CppSystem.Collections.Generic.Dictionary<AnItem, int>;

namespace SyncRADation.ItemSystem
{
    public static class ItemBag
    {
        public struct Stack
        {
            public AnItem Item;
            public int Count;
        }

        // Per-reader scratch (persistent pure caches): a reader never calls another reader while it walks its own list.
        static readonly List<Stack> _countScratch = new List<Stack>(8);
        static readonly List<Stack> _roomScratch = new List<Stack>(8);
        static readonly List<Stack> _findScratch = new List<Stack>(8);

        /// <summary>
        /// Every entry of a native item dictionary, copied out in one walk. The native enumerator is disposed before
        /// the caller sees anything, so the caller may add / remove items while it iterates the copy.
        /// </summary>
        public static List<Stack> Read(ItemDict dict, List<Stack> into)
        {
            into.Clear();
            if (dict == null) return into;
            try
            {
                var en = dict.GetEnumerator();
                while (en.MoveNext())
                    into.Add(new Stack { Item = en.Current.key, Count = en.Current.value });
                en.Dispose();
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return into;
        }

        public static List<Stack> Bag(List<Stack> into)
        {
            ItemDict dict = null;
            try { dict = InventoryManager.elsterItems; } catch (System.Exception e) { Guard.Swallow(e); }
            return Read(dict, into);
        }

        public static List<Stack> Box(List<Stack> into)
        {
            ItemDict dict = null;
            try { dict = InventoryManager.boxItems; } catch (System.Exception e) { Guard.Swallow(e); }
            return Read(dict, into);
        }

        static Items.itemlist KindOf(AnItem item)
        {
            if (item == null) return Items.itemlist.None;
            try { return item._item; } catch { return Items.itemlist.None; }
        }

        /// <summary>Units of this item in the local bag (all bag instances; a bogus &gt;99 entry counts as one).</summary>
        public static int CountInBag(Items.itemlist id)
        {
            if (id == Items.itemlist.None) return 0;
            int n = 0;
            var all = Bag(_countScratch);
            for (int i = 0; i < all.Count; i++)
            {
                if (KindOf(all[i].Item) != id) continue;
                int v = all[i].Count;
                if (v > 0 && v <= 99) n += v;
                else if (v > 99) n += 1;
            }
            return n;
        }

        /// <summary>The bag instance holding this item kind (may be a different AnItem than the catalog one), or null.</summary>
        public static AnItem FindInBag(Items.itemlist want)
        {
            if (want == Items.itemlist.None) return null;
            var all = Bag(_findScratch);
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Count > 0 && all[i].Item != null && KindOf(all[i].Item) == want)
                    return all[i].Item;
            }
            return null;
        }

        /// <summary>True when the bag already holds a full stack (AddItem would silently drop everything).</summary>
        public static bool StackAtCap(Items.itemlist id)
        {
            try
            {
                var item = InventoryManager.getItem(id);
                if (item == null || PartyKeyRing.IsKeyOrObject(item)) return false;
                int max = item.maxNumber;
                return max > 0 && CountInBag(id) >= max;
            }
            catch (System.Exception e) { Guard.Swallow(e); return false; }
        }

        /// <summary>
        /// The bag can take this item: an existing stack below its max, or a free slot for a new stack
        /// (native AddItem ignores maxSlots, so every mod-side add checks this first).
        /// </summary>
        public static bool BagHasRoom(Items.itemlist id)
        {
            try
            {
                if (FindInBag(id) != null)
                    return !StackAtCap(id);
                int used = 0;
                var all = Bag(_roomScratch);
                for (int i = 0; i < all.Count; i++)
                    if (all[i].Item != null && all[i].Count > 0) used++;
                int max = InventoryManager.maxSlots;
                if (max <= 0) max = 6;
                return used < max;
            }
            catch { return true; }
        }

        /// <summary>Units of this item in the shared box (native boxContainsItemCount; boxContainsItem as the fallback).</summary>
        public static int BoxStock(AnItem item)
        {
            if (item == null) return 0;
            try { return InventoryManager.boxContainsItemCount(item); }
            catch
            {
                try { return InventoryManager.boxContainsItem(item) ? 1 : 0; }
                catch (System.Exception e) { Guard.Swallow(e); return 0; }
            }
        }
    }
}
