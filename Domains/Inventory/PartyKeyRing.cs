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
            _bagEnsureAt.Clear();
            _uiName = null;
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

        /// <summary>
        /// Client→host craft revoke prefix. Not a real itemlist (AirlockKey=0; None=113).
        /// Old hosts skip non-Key/Object and no-op-add remaining enums.
        /// </summary>
        public const ushort CraftRevokeSentinel = 0xFFFF;

        public static void ApplyMessage(PartyKeyRingMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (msg.ItemEnums == null) return;

            // Craft/consume revoke: strip ring + EnsureInBag bag mirrors on every peer.
            // Snapshot Broadcast alone leaves peer bag ghosts (InLocalBag still true).
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
                    + (net != null && net.Role == NetworkRole.Host ? " host" : " peer"));
                if (net != null && net.Role == NetworkRole.Host && net.IsConnected)
                {
                    // Fan-out sentinel so non-crafter clients strip mirrors too.
                    try { net.SendPartyKeyRing(msg.ItemEnums); } catch { }
                    Broadcast();
                }
                return;
            }

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

        /// <summary>
        /// After a successful <c>CombineRecipes.combine</c>, drop Key/Object ingredients
        /// from the party ring and strip EnsureInBag bag mirrors. Host fans out
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
        /// Ring + bag mirror drop for a consumed unique (UseItem ConsumesKey). Host fans
        /// out CraftRevokeSentinel so peers who EnsureInBag-mirrored the key also strip.
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
            try { net.SendPartyKeyRing(arr); }
            catch { }
            if (net.Role == NetworkRole.Host)
                Broadcast();
        }

        /// <summary>
        /// Drop EnsureInBag / grant mirrors of a unique from the local bag when the
        /// party ring revokes it (craft combine, UseItem consume, G-drop DetachDroppedKey).
        /// Mirrors ConsumeDropped on the dropper; remotes strip via this helper.
        /// </summary>
        public static void StripBagMirrors(Items.itemlist item)
        {
            if (!IsKeyOrObject(item)) return;
            ushort id = (ushort)item;
            _bagAttempt.Remove(id);
            _bagEnsureAt.Remove(id);
            try
            {
                var dict = InventoryManager.elsterItems;
                if (dict != null)
                {
                    var extra = new List<AnItem>();
                    var counts = new List<int>();
                    var en = dict.GetEnumerator();
                    while (en.MoveNext())
                    {
                        var key = en.Current.key;
                        if (key != null && key._item == item && en.Current.value > 0)
                        {
                            extra.Add(key);
                            counts.Add(en.Current.value);
                        }
                    }
                    en.Dispose();
                    for (int i = 0; i < extra.Count; i++)
                    {
                        try { InventoryManager.RemoveItem(extra[i], counts[i]); } catch { }
                    }
                }
            }
            catch { }
            try
            {
                if (InventoryManager.CurrentItem != null && InventoryManager.CurrentItem._item == item)
                    InventoryManager.CurrentItem = null;
            }
            catch { }
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
            _uiName = name;
            try { Dialoguer.SetGlobalString(DialoguerKeyNameId, name); } catch { }
            try { Dialoguer.SetGlobalString(0, name); } catch { }
        }

        static string _uiName;

        /// <summary>
        /// Story dumps overwrite Dialoguer s0/s3 with the host's last use (often AirlockKey).
        /// Re-apply the local inspect/use name after XML apply.
        /// </summary>
        public static void RestoreUiNames()
        {
            if (string.IsNullOrEmpty(_uiName)) return;
            try { Dialoguer.SetGlobalString(DialoguerKeyNameId, _uiName); } catch { }
            try { Dialoguer.SetGlobalString(0, _uiName); } catch { }
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

        public static bool InLocalBag(Items.itemlist item)
        {
            if (item == Items.itemlist.None) return false;
            try
            {
                var cat = InventoryManager.getItem(item);
                return cat != null && InLocalBag(cat);
            }
            catch { return false; }
        }

        static bool _ensuringBag;
        /// <summary>Full-bag refuse set; cleared when BagLikelyHasRoom becomes true.</summary>
        static readonly HashSet<ushort> _bagAttempt = new HashSet<ushort>();
        static readonly Dictionary<ushort, float> _bagEnsureAt = new Dictionary<ushort, float>();
        const float EnsureCooldown = 0.35f;

        /// <summary>
        /// Native UseItem / Interactor.InteractItem compare AnItem by reference.
        /// Scene <c>key</c> is the catalog SO; a dropped grant may be a different
        /// instance with the same <c>_item</c>. Point both at the bag copy.
        /// </summary>
        public static AnItem BindSceneKey(AnItem sceneKey)
        {
            var cat = CatalogOf(sceneKey) ?? sceneKey;
            if (cat == null) return sceneKey;
            EnsureInBag(cat);
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

        /// <summary>
        /// Put a party-ring key into the local 6-slot bag so native UseItem / cutscene
        /// run as if Elster was carrying it. No-op if already held or not on the ring.
        /// Full-bag fails are retried once a slot frees (BindSceneKey runs every Update).
        /// </summary>
        public static bool EnsureInBag(AnItem item)
        {
            if (_ensuringBag) return InLocalBag(item);
            var cat = CatalogOf(item) ?? item;
            if (cat == null) return false;
            if (InLocalBag(cat)) return true;
            if (!Has(cat)) return false;
            ushort id = (ushort)cat._item;
            if (_bagAttempt.Contains(id))
            {
                // Prior full-bag refuse — retry only when a slot may have freed.
                if (!BagLikelyHasRoom())
                    return false;
                _bagAttempt.Remove(id);
            }
            // BindSceneKey runs every UseItem Update — cooldown avoids AddItem spam.
            float now = UnityEngine.Time.unscaledTime;
            float last;
            if (_bagEnsureAt.TryGetValue(id, out last) && now - last < EnsureCooldown)
                return false;
            _bagEnsureAt[id] = now;
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
            else if (!BagLikelyHasRoom())
                _bagAttempt.Add(id);
            return ok;
        }

        static bool BagLikelyHasRoom()
        {
            try
            {
                int used = 0;
                var dict = InventoryManager.elsterItems;
                if (dict == null) return true;
                var en = dict.GetEnumerator();
                while (en.MoveNext())
                {
                    if (en.Current.key != null && en.Current.value > 0)
                        used++;
                }
                en.Dispose();
                int max = InventoryManager.maxSlots;
                if (max <= 0) max = 6;
                return used < max;
            }
            catch { return true; }
        }

        public static bool LocalOrRingHas(AnItem item)
        {
            if (item == null) return false;
            return InLocalBag(item) || Has(item);
        }
    }
}
