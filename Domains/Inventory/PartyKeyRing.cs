// Party-held unique keys/objects so UseItemInteraction works for either Elster.
using System.Collections.Generic;
using SyncRADation.ItemSystem;
using SyncRADation.Sync;

namespace SyncRADation.Networking
{
    public static class PartyKeyRing
    {
        private static readonly HashSet<ushort> _keys = new HashSet<ushort>();

        public static void Reset()
        {
            _keys.Clear();
            _uiName = null;
            _masqueradeOff = 0;
        }

        // hasItem/getCount ring masquerade (StoryPatches) is off while > 0: SaveManager.Save writes every
        // hasItem item with its getCount into the saver's bag (Ghidra SaveManager.c Save), so ring keys
        // held by other peers would be saved as this player's items.
        static int _masqueradeOff;
        public static bool MasqueradeOff => _masqueradeOff > 0;
        public static void SuspendMasquerade() => _masqueradeOff++;
        public static void ResumeMasquerade() { if (_masqueradeOff > 0) _masqueradeOff--; }

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
            try { Note(item._item); } catch (System.Exception e) { Guard.Swallow(e); }
        }

        public static void Remove(Items.itemlist item)
        {
            if (_keys.Remove((ushort)item))
                PlaytestLog.Event("KeyRing", "drop " + item + " count=" + _keys.Count);
        }

        /// <summary>Client→host craft revoke prefix. Not a real itemlist (AirlockKey=0; None=113).</summary>
        public const ushort CraftRevokeSentinel = 0xFFFF;

        public static void ApplyMessage(PartyKeyRingMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (msg.ItemEnums == null) return;

            // Craft/consume revoke: strip the ring entry and any physical bag copy on every peer.
            // A snapshot Broadcast alone would leave a peer's bag copy behind (InLocalBag still true).
            if (msg.ItemEnums.Length > 0 && msg.ItemEnums[0] == CraftRevokeSentinel)
            {
                for (int i = 1; i < msg.ItemEnums.Length; i++)
                {
                    var item = (Items.itemlist)msg.ItemEnums[i];
                    if (!IsKeyOrObject(item)) continue;
                    Remove(item);
                    StripBagMirrors(item);
                }
                PlaytestLog.Event("KeyRing", "craft-revoke count=" + _keys.Count
                    + (NetGate.HostRole ? " host" : " peer"));
                if (NetGate.Host)
                {
                    // Fan-out sentinel so non-crafter clients strip their bag copies too.
                    try { net.InventoryHandlers.SendPartyKeyRing(msg.ItemEnums); } catch (System.Exception e) { Guard.Swallow(e); }
                    Broadcast();
                }
                return;
            }

            if (NetGate.HostRole)
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
            if (!NetGate.Host) return;
            net.InventoryHandlers.SendPartyKeyRing(Snapshot());
        }

        public static void OfferToHost(AnItem item)
        {
            Note(item);
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            if (NetGate.HostRole)
            {
                Broadcast();
                return;
            }
            if (item == null) return;
            try
            {
                if (!IsKeyOrObject(item)) return;
                net.InventoryHandlers.SendPartyKeyRing(new[] { (ushort)item._item });
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        /// <summary>
        /// After a successful <c>CombineRecipes.combine</c>, drop Key/Object ingredients
        /// from the party ring and strip bag copies. Host fans out
        /// CraftRevokeSentinel then Broadcasts; client sends sentinel so host fans out.
        /// </summary>
        public static void ConsumeCraftIngredients(AnItem itemA, AnItem itemB)
        {
            if (NetGate.IsApplying || !NetGate.Live) return;
            var revoke = new List<ushort>(2);
            CollectCraftRevoke(revoke, itemA);
            CollectCraftRevoke(revoke, itemB);
            if (revoke.Count == 0) return;

            for (int i = 0; i < revoke.Count; i++)
            {
                var item = (Items.itemlist)revoke[i];
                Remove(item);
                StripBagMirrors(item);
            }

            SendCraftRevoke(revoke);
        }

        /// <summary>
        /// Ring + bag copy drop for a consumed unique (UseItem ConsumesKey). Host fans
        /// out CraftRevokeSentinel so peers holding a bag copy of the key also strip it.
        /// </summary>
        public static void RevokeConsumed(Items.itemlist item)
        {
            if (!IsKeyOrObject(item)) return;
            Remove(item);
            StripBagMirrors(item);
            if (NetGate.IsApplying || !NetGate.Live) return;
            var revoke = new List<ushort>(1);
            revoke.Add((ushort)item);
            SendCraftRevoke(revoke);
        }

        static void SendCraftRevoke(List<ushort> revoke)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected || revoke == null || revoke.Count == 0) return;
            var arr = new ushort[revoke.Count + 1];
            arr[0] = CraftRevokeSentinel;
            for (int i = 0; i < revoke.Count; i++)
                arr[i + 1] = revoke[i];
            try { net.InventoryHandlers.SendPartyKeyRing(arr); }
            catch (System.Exception e) { Guard.Swallow(e); }
            if (NetGate.HostRole)
                Broadcast();
        }

        /// <summary>
        /// Drop physical bag copies (claim / storage grants) of a unique from the local bag when the
        /// party ring revokes it (craft combine, UseItem consume, G-drop DetachDroppedKey).
        /// Mirrors ConsumeDropped on the dropper; remotes strip via this helper.
        /// </summary>
        public static void StripBagMirrors(Items.itemlist item)
        {
            if (!IsKeyOrObject(item)) return;
            var bag = ItemBag.Bag(new List<ItemBag.Stack>(6));
            for (int i = 0; i < bag.Count; i++)
            {
                var held = bag[i].Item;
                if (held == null || bag[i].Count <= 0) continue;
                try { if (held._item == item) InventoryManager.RemoveItem(held, bag[i].Count); }
                catch (System.Exception e) { Guard.Swallow(e); }
            }
            try
            {
                if (InventoryManager.CurrentItem != null && InventoryManager.CurrentItem._item == item)
                    InventoryManager.CurrentItem = null;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        static void CollectCraftRevoke(List<ushort> dst, AnItem item)
        {
            if (item == null || !IsKeyOrObject(item)) return;
            try
            {
                ushort id = (ushort)item._item;
                if (!dst.Contains(id))
                    dst.Add(id);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        /// <summary>Party save snapshot: copy of the ring (Key/Object enums).</summary>
        public static ushort[] Export() => Snapshot();

        /// <summary>
        /// Party wipe / load: replace the ring with a saved snapshot (host). Caller broadcasts.
        /// </summary>
        public static void Import(ushort[] enums)
        {
            _keys.Clear();
            if (enums != null)
            {
                for (int i = 0; i < enums.Length; i++)
                {
                    if (IsKeyOrObject((Items.itemlist)enums[i]))
                        _keys.Add(enums[i]);
                }
            }
            PlaytestLog.Event("KeyRing", "import count=" + _keys.Count);
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
            catch (System.Exception e) { Guard.Swallow(e); }
            return MatchByName(item);
        }

        static AnItem MatchByName(AnItem item)
        {
            if (item == null) return null;
            string want = null;
            try { want = item._name; } catch (System.Exception e) { Guard.Swallow(e); }
            if (string.IsNullOrEmpty(want)) return null;
            try
            {
                var all = InventoryManager.allItems;
                if (all == null) return null;
                var en = all.GetEnumerator();
                int count = all.Count;
                for (int step = 0; step < count && en.MoveNext(); step++)
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
                    catch (System.Exception e) { Guard.Swallow(e); }
                }
                en.Dispose();
            }
            catch (System.Exception e) { Guard.Swallow(e); }
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
                try { cat = CatalogOf(UseItemInteraction.currentUseItem); } catch (System.Exception e) { Guard.Swallow(e); }
            }
            if (cat == null) return null;
            try
            {
                var n = InventoryManager.getName(cat);
                if (!BadLoc(n)) return n;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                var n = cat.localizedName();
                if (!BadLoc(n)) return n;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                if (!string.IsNullOrEmpty(cat._name) && !BadLoc(cat._name))
                    return cat._name;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return null;
        }

        public const int DialoguerKeyNameId = 3;

        public static void BindUseDialogue(AnItem item)
        {
            var cat = CatalogOf(item);
            if (cat != null)
            {
                try { UseItemInteraction.currentUseItem = cat; } catch (System.Exception e) { Guard.Swallow(e); }
                try
                {
                    if (item != null && item != cat && string.IsNullOrEmpty(item._name)
                        && !string.IsNullOrEmpty(cat._name))
                        item._name = cat._name;
                }
                catch (System.Exception e) { Guard.Swallow(e); }
            }
            string name = DisplayName(cat ?? item);
            if (string.IsNullOrEmpty(name)) return;
            _uiName = name;
            try { Dialoguer.SetGlobalString(DialoguerKeyNameId, name); } catch (System.Exception e) { Guard.Swallow(e); }
            try { Dialoguer.SetGlobalString(0, name); } catch (System.Exception e) { Guard.Swallow(e); }
        }

        static string _uiName;

        /// <summary>Re-apply the local inspect/use name to Dialoguer s0/s3 (flavor dialogue start / continue).</summary>
        public static void RestoreUiNames()
        {
            if (string.IsNullOrEmpty(_uiName)) return;
            try { Dialoguer.SetGlobalString(DialoguerKeyNameId, _uiName); } catch (System.Exception e) { Guard.Swallow(e); }
            try { Dialoguer.SetGlobalString(0, _uiName); } catch (System.Exception e) { Guard.Swallow(e); }
        }

        public static AnItem FindInBag(AnItem item)
        {
            if (item == null) return null;
            try { return ItemBag.FindInBag(item._item); }
            catch (System.Exception e) { Guard.Swallow(e); return null; }
        }

        public static bool InLocalBag(AnItem item) => FindInBag(item) != null;

        /// <summary>
        /// Native UseItem / Interactor.InteractItem compare AnItem by reference.
        /// Scene <c>key</c> is the catalog SO; a dropped grant may be a different
        /// instance with the same <c>_item</c>. Point both at the bag copy.
        /// A ring key held by another peer is never added to this bag: UseItemInteraction.StartDialogue
        /// only asks hasItem(key) (ring masquerade) and its onMessageEvent RemoveItem is a no-op for an
        /// item not in the bag (Ghidra UseItemInteraction.c / InventoryManager.c RemoveItem ContainsKey).
        /// </summary>
        public static AnItem BindSceneKey(AnItem sceneKey)
        {
            var cat = CatalogOf(sceneKey) ?? sceneKey;
            if (cat == null) return sceneKey;
            return FindInBag(cat) ?? cat;
        }

        public static void BindHeldArg(ref AnItem item)
        {
            if (item == null) return;
            var held = FindInBag(item);
            if (held != null)
            {
                item = held;
                return;
            }
            var cat = CatalogOf(item);
            if (cat != null) item = cat;
        }

        public static bool LocalOrRingHas(AnItem item)
        {
            if (item == null) return false;
            return InLocalBag(item) || Has(item);
        }
    }
}
