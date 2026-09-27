// Host-authoritative world ItemPickup: claim → grant to claimer, hide for everyone.
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;

namespace SyncRADation.Patches
{
    [HarmonyPatch(typeof(ItemPickup), nameof(ItemPickup.pickUp))]
    public static class ItemPickupPatches
    {
        static ulong _pendingId;
        static Items.itemlist _pendingItem;
        static float _pendingTime;

        internal static void NoteTakenFromCallback(ItemPickup p)
        {
            if (p != null && ItemSystem.DroppedItemManager.IsDropped(p))
            {
                TakeDropped(p);
                return;
            }
            NoteTaken(p);
        }

        internal static bool TakeDropped(ItemPickup p)
        {
            ArmDropped(p);
            if (_pendingDropKey < 0) return false;
            EnsureGranted();
            FinishDroppedNative(p, true, ignoreCount: true);
            try { ItemSystem.DroppedItemManager.RestorePlay(); } catch { }
            return true;
        }

        static void EnsureGranted()
        {
            var id = _pendingDropItem;
            if (id == Items.itemlist.None && _pendingDropKey >= 0)
                ItemSystem.DroppedItemManager.TryGet(_pendingDropKey, out id, out _);
            if (id == Items.itemlist.None) return;
            if (ItemSystem.DroppedItemManager.CountInBag(id) > 0) return;
            try
            {
                var item = InventoryManager.getItem(id);
                if (item != null)
                    InventoryManager.AddItem(item, _pendingDropCount > 0 ? _pendingDropCount : 1);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[Drop] grant: " + ex.Message);
            }
        }

        internal static void ArmDropped(ItemPickup p)
        {
            if (p == null || !ItemSystem.DroppedItemManager.IsDropped(p)) return;
            int key;
            if (ItemSystem.DroppedItemManager.TryKeyOf(p, out key))
                _pendingDropKey = key;
            _pendingDropItem = Items.itemlist.None;
            _pendingDropCount = 1;
            try
            {
                if (p._item != null) _pendingDropItem = p._item._item;
                _pendingDropCount = p.count > 0 ? p.count : 1;
            }
            catch { }
            if (_pendingDropItem == Items.itemlist.None && _pendingDropKey >= 0)
            {
                int c;
                ItemSystem.DroppedItemManager.TryGet(_pendingDropKey, out _pendingDropItem, out c);
                if (c > 0) _pendingDropCount = c;
            }
            _countBeforeDrop = ItemSystem.DroppedItemManager.CountInBag(_pendingDropItem);
        }

        internal static void TickPendingDrop()
        {
            if (_pendingDropKey < 0) return;
            if (!ItemSystem.DroppedItemManager.TryGet(_pendingDropKey, out _, out _))
            {
                _pendingDropKey = -1;
                return;
            }
            if (!BagGained(null)) return;
            FinishDroppedNative(null, true, ignoreCount: true);
        }

        static bool BagGained(ItemPickup p)
        {
            var id = _pendingDropItem;
            if (id == Items.itemlist.None && p != null)
            {
                try { if (p._item != null) id = p._item._item; } catch { }
            }
            if (id == Items.itemlist.None && _pendingDropKey >= 0)
                ItemSystem.DroppedItemManager.TryGet(_pendingDropKey, out id, out _);
            if (id == Items.itemlist.None) return false;
            return ItemSystem.DroppedItemManager.CountInBag(id) > _countBeforeDrop;
        }

        internal static void CommitDroppedIfTaken(ItemPickup p)
        {
            if (p != null && IsInspect(p)) return;
            bool trig = false;
            try { if (p != null) trig = p.triggered; } catch { }
            if (!trig) return;
            FinishDroppedNative(p, true, ignoreCount: true);
        }

        /// <summary>Notes craft/grant <b>result</b> on AddItem. Ingredients: see CombineRecipesCraftPatch.</summary>
        internal static void NoteCraftedKey(AnItem item)
        {
            if (NetGate.IsApplying || !NetGate.Live) return;
            if (!PartyKeyRing.IsKeyOrObject(item)) return;
            PartyKeyRing.OfferToHost(item);
        }

        /// <summary>Ring-drop Key/Object ingredients after a successful combine.</summary>
        internal static void NoteCraftConsumed(AnItem itemA, AnItem itemB, AnItem result)
        {
            if (result == null) return;
            if (NetGate.IsApplying || !NetGate.Live) return;
            PartyKeyRing.ConsumeCraftIngredients(itemA, itemB);
        }

        static bool IsInspect(ItemPickup p)
        {
            if (p == null) return false;
            try { if (p.showItemView) return true; } catch { }
            try { if (p.focusCamera) return true; } catch { }
            try { if (p.pauseGame) return true; } catch { }
            return false;
        }

        static void BindPickupName(ItemPickup p)
        {
            if (p == null) return;
            try
            {
                var kind = WorldPickupSyncService.ResolveItem(p);
                if (kind == Items.itemlist.None) return;
                var cat = InventoryManager.getItem(kind);
                if (cat == null) return;
                p._item = cat;
                PartyKeyRing.BindUseDialogue(cat);
            }
            catch { }
        }

        static int _pendingDropKey = -1;
        static Items.itemlist _pendingDropItem;
        static int _pendingDropCount;
        static int _countBeforeDrop;

        [HarmonyPrefix]
        public static bool Prefix(ItemPickup __instance)
        {
            if (__instance == null) return true;
            BindPickupName(__instance);

            if (ItemSystem.DroppedItemManager.IsDropped(__instance))
            {
                ArmDropped(__instance);
                return true;
            }

            if (NetGate.IsApplying) return true;

            // Death tarot MeatBlocker seals the NG+ KeyOfSacrifice wing (wiki softlock).
            // Hold take while that key is still a live unclaimed world unique.
            try
            {
                var deathItem = WorldPickupSyncService.ResolveItem(__instance);
                var netHold = LanNetworkManager.Instance;
                if (netHold != null && netHold.IsConnected
                    && netHold.PickupSync.HoldTarotDeathForSacrifice(deathItem))
                {
                    PlaytestLog.Event("Pickup", "hold TarotDeath until KeyOfSacrifice");
                    return false;
                }
            }
            catch { }

            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected)
                return true;

            if (Config.ModConfig.SyncWorldPickups?.Value != true)
                return true;

            try
            {
                if (__instance.slave) return true;
            }
            catch { }

            ulong id = WorldId.FromGameObject(__instance.gameObject);
            if (id == 0) return true;
            _pendingId = id;
            _pendingTime = UnityEngine.Time.unscaledTime;
            try
            {
                if (__instance._item != null)
                    _pendingItem = __instance._item._item;
                if (_pendingItem == Items.itemlist.None)
                    _pendingItem = WorldPickupSyncService.ResolveItem(__instance);
            }
            catch { }

            if (net.PickupSync.IsClaimed(id) || net.PickupSync.IsClaimedPickup(__instance))
            {
                PlaytestLog.Verbose("Pickup", "skip claimed " + __instance.gameObject.name
                    + " id=" + id.ToString("X16"));
                try { net.PickupSync.HidePickup(__instance); } catch { }
                return false;
            }

            // Inspect cards: native pickUp shows yes/no. Claim only after the item is in the bag.
            if (IsInspect(__instance))
                return true;

            if (WorldClaimNeedsBagRoom(_pendingItem) && !BagHasRoomForWorld(_pendingItem))
            {
                // Host: let native pickUp show _nospaceDialogue (no reservation).
                // Client: block native (would dual-grant) and skip claim wire.
                PlaytestLog.Event("Pickup", "deny bag full " + __instance.gameObject.name
                    + " item=" + _pendingItem);
                return net.Role == NetworkRole.Host;
            }

            if (net.Role == NetworkRole.Host)
            {
                if (!net.PickupSync.TryClaimOnHost(id, net.LocalPlayerId, out _, out _,
                    hideNow: false, hintItem: _pendingItem))
                {
                    PlaytestLog.Event("Pickup", "host deny " + __instance.gameObject.name
                        + " id=" + id.ToString("X16"));
                    try { net.PickupSync.HidePickup(__instance); } catch { }
                    return false;
                }
                PlaytestLog.Event("Pickup", "host take " + __instance.gameObject.name
                    + " id=" + id.ToString("X16"));
                return true;
            }

            PlaytestLog.Event("Pickup", "claim " + __instance.gameObject.name + " id=" + id.ToString("X16"));
            net.SendWorldPickupClaim(id, _pendingItem, CountOf(__instance));
            return false;
        }

        [HarmonyPostfix]
        public static void Postfix(ItemPickup __instance, bool __runOriginal)
        {
            if (!__runOriginal) return;
            if (__instance != null && ItemSystem.DroppedItemManager.IsDropped(__instance))
            {
                CommitDroppedIfTaken(__instance);
                return;
            }
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            if (Config.ModConfig.SyncWorldPickups?.Value != true) return;

            try { if (__instance != null && __instance.slave) return; } catch { }

            ulong id = 0;
            try { id = WorldId.FromGameObject(__instance.gameObject); } catch { }
            if (id == 0) id = _pendingId;
            if (id == 0) return;

            bool inBag = false;
            try { inBag = __instance._item != null && InventoryManager.hasItem(__instance._item); }
            catch { }
            if (!inBag && _pendingItem != Items.itemlist.None)
            {
                try { inBag = InventoryManager.hasItem(_pendingItem); } catch { }
            }

            if (IsInspect(__instance) && !inBag)
            {
                bool gone = false;
                try { gone = __instance == null; } catch { gone = true; }
                if (!gone) return;
            }

            bool triggered = false;
            try { triggered = __instance != null && __instance.triggered; } catch { }
            // Host may have Prefix-reserved; fall through so ReleaseClaimIf can run.
            if (!triggered && !inBag && __instance != null && net.Role != NetworkRole.Host)
                return;

            if (net.Role == NetworkRole.Host)
            {
                // Native refused (nospace / cancel) after Prefix reserved — free the WorldId.
                if (!triggered && !inBag)
                {
                    net.PickupSync.ReleaseClaimIf(id, net.LocalPlayerId);
                    return;
                }
                if (!net.PickupSync.TryClaimOnHost(id, net.LocalPlayerId, out _, out _, hideNow: true,
                    hintItem: _pendingItem))
                {
                    PlaytestLog.Event("Pickup", "host post deny id=" + id.ToString("X16"));
                    return;
                }
                // Native pickUp already Invoked onPickup — Note before Broadcast so
                // host EnsurePartyOnPickup does not double-fire (Dig H).
                net.PickupSync.NoteOnPickupFired(id);
                net.PickupSync.BroadcastTriggered(id, true);
                try
                {
                    if (__instance != null && __instance._item != null)
                    {
                        PartyKeyRing.Note(__instance._item);
                        PartyKeyRing.Broadcast();
                    }
                    else if (_pendingItem != Items.itemlist.None)
                    {
                        PartyKeyRing.Note(_pendingItem);
                        PartyKeyRing.Broadcast();
                    }
                }
                catch { }
                return;
            }

            if (!inBag && __instance != null) return;
            if (net.PickupSync.IsClaimed(id)) return;
            if (WorldClaimNeedsBagRoom(_pendingItem) && !BagHasRoomForWorld(_pendingItem))
            {
                PlaytestLog.Event("Pickup", "deny bag full after inspect item=" + _pendingItem);
                return;
            }
            PlaytestLog.Event("Pickup", "claim after inspect " + (__instance != null ? __instance.gameObject.name : "gone")
                + " id=" + id.ToString("X16"));
            net.SendWorldPickupClaim(id, _pendingItem, CountOf(__instance));
        }

        static void NoteTaken(ItemPickup p)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            if (Config.ModConfig.SyncWorldPickups?.Value != true) return;
            if (p == null && _pendingId == 0) return;
            try { if (p != null && p.slave) return; } catch { }

            ulong id = 0;
            try { if (p != null) id = WorldId.FromGameObject(p.gameObject); } catch { }
            if (id == 0) id = _pendingId;
            if (id == 0) return;

            var item = _pendingItem;
            try
            {
                if (p != null && p._item != null)
                    item = p._item._item;
                if (item == Items.itemlist.None)
                    item = WorldPickupSyncService.ResolveItem(p);
            }
            catch { }

            if (net.Role == NetworkRole.Host)
            {
                if (!net.PickupSync.TryClaimOnHost(id, net.LocalPlayerId, out _, out _, hideNow: true, hintItem: item))
                {
                    PlaytestLog.Event("Pickup", "host note deny id=" + id.ToString("X16"));
                    return;
                }
                // Inspect/confirm path: native already Invoked — Note before Broadcast (Dig H).
                net.PickupSync.NoteOnPickupFired(id);
                net.PickupSync.BroadcastTriggered(id, true);
                try
                {
                    if (item != Items.itemlist.None)
                    {
                        PartyKeyRing.Note(item);
                        PartyKeyRing.Broadcast();
                    }
                }
                catch { }
                return;
            }

            if (net.PickupSync.IsClaimed(id)) return;
            if (WorldClaimNeedsBagRoom(item) && !BagHasRoomForWorld(item))
            {
                PlaytestLog.Event("Pickup", "deny bag full confirm item=" + item);
                return;
            }
            PlaytestLog.Event("Pickup", "claim confirm id=" + id.ToString("X16") + " item=" + item);
            net.SendWorldPickupClaim(id, item, CountOf(p));
        }

        static int CountOf(ItemPickup p)
        {
            if (p == null) return 1;
            try { return p.count > 0 ? p.count : 1; } catch { return 1; }
        }

        /// <summary>
        /// Key/Object land on the party ring even when the 6-slot bag is full.
        /// Ammo/docs/tools need a free slot (or an existing stack) — same rule as drops.
        /// </summary>
        static bool WorldClaimNeedsBagRoom(Items.itemlist kind)
        {
            // Unresolved enum: do not gate (may still be Key/Object after host resolve).
            if (kind == Items.itemlist.None) return false;
            if (PartyKeyRing.IsKeyOrObject(kind)) return false;
            return true;
        }

        static bool BagHasRoomForWorld(Items.itemlist kind)
        {
            try
            {
                var item = InventoryManager.getItem(kind);
                if (item != null && PartyKeyRing.InLocalBag(item)) return true;
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

        internal static void NoteDroppedGrant(AnItem item)
        {
            if (_pendingDropKey < 0) return;
            FinishDroppedNative(null, true, ignoreCount: true);
        }

        static readonly System.Collections.Generic.HashSet<int> _claimedDrops
            = new System.Collections.Generic.HashSet<int>();

        struct AwaitingDrop
        {
            public Items.itemlist Item;
            public int Count;
        }

        // Host ack for client DroppedPickup — keyed so a second claim before the first
        // ack cannot overwrite / mis-revert under storage+drop soak (3–4 peers).
        static readonly System.Collections.Generic.Dictionary<int, AwaitingDrop> _awaitingDrops
            = new System.Collections.Generic.Dictionary<int, AwaitingDrop>();

        /// <summary>
        /// Clear local claim dedupe + awaiting ack. Required on StopNetwork because
        /// DroppedItemNetHandlers resets _nextItemIndex to 1 and player ids recycle —
        /// stale _claimedDrops entries then no-op FinishDroppedNative (floor vanishes, no wire).
        /// </summary>
        internal static void ResetDropClaims()
        {
            _claimedDrops.Clear();
            _awaitingDrops.Clear();
            _pendingDropKey = -1;
            _pendingDropItem = Items.itemlist.None;
            _pendingDropCount = 0;
        }

        static void FinishDroppedNative(ItemPickup p, bool confirmed, bool ignoreCount = false)
        {
            if (!confirmed) return;
            int key = _pendingDropKey;
            if (p != null)
            {
                int k;
                if (ItemSystem.DroppedItemManager.TryKeyOf(p, out k))
                    key = k;
            }
            if (key < 0) return;
            if (!ItemSystem.DroppedItemManager.TryGet(key, out _, out _)) return;

            if (!ignoreCount && !BagGained(p)) return;

            if (!_claimedDrops.Add(key))
            {
                ItemSystem.DroppedItemManager.DespawnWhenIdle(key);
                _pendingDropKey = -1;
                return;
            }

            var net = LanNetworkManager.Instance;
            ModRuntime.Log?.Msg("[Drop] claim key=" + key + " " + _pendingDropItem
                + " by=" + (net != null ? net.LocalPlayerId.ToString() : "?")
                + " host=" + (net != null && net.Role == NetworkRole.Host));
            if (net != null && net.IsConnected)
            {
                if (net.Role == NetworkRole.Host)
                {
                    // Host never stages awaiting-ack entries (client-only). Do not
                    // Clear() here — a mistaken wipe would drop FAIL-revert keys if
                    // any ever landed on this process (role recycle / StopNetwork race).
                    net.TryClaimDropped(key, net.LocalPlayerId, out _, skipLocalGrant: true);
                }
                else
                {
                    // WorldId carries drop key so FAIL ack can match (Int0 is also key).
                    _awaitingDrops[key] = new AwaitingDrop
                    {
                        Item = _pendingDropItem,
                        Count = _pendingDropCount > 0 ? _pendingDropCount : 1
                    };
                    net.SendInteractionRequest(unchecked((ulong)(uint)key), InteractionKind.DroppedPickup, key);
                }
            }
            else
                _awaitingDrops.Remove(key);
            ItemSystem.DroppedItemManager.DespawnWhenIdle(key);
            _pendingDropKey = -1;
        }

        internal static void NoteDropClaimAck(bool ok, long ackWorldId)
        {
            int ackKey = ackWorldId != 0 ? (int)ackWorldId : -1;
            if (ok)
            {
                if (ackKey >= 0)
                    _awaitingDrops.Remove(ackKey);
                else if (_awaitingDrops.Count == 1)
                {
                    // Legacy ack without WorldId — only safe when a single claim is in flight.
                    _awaitingDrops.Clear();
                }
                return;
            }
            RevertPendingNativeGrant(ackWorldId);
        }

        internal static void RevertPendingNativeGrant(long ackWorldId = 0)
        {
            int ackKey = ackWorldId != 0 ? (int)ackWorldId : -1;
            Items.itemlist itemEnum = Items.itemlist.None;
            int count = 1;
            int clearKey = -1;

            if (ackKey >= 0 && _awaitingDrops.TryGetValue(ackKey, out var pending))
            {
                itemEnum = pending.Item;
                count = pending.Count > 0 ? pending.Count : 1;
                clearKey = ackKey;
            }
            else if (ackKey < 0 && _awaitingDrops.Count == 1)
            {
                foreach (var kvp in _awaitingDrops)
                {
                    clearKey = kvp.Key;
                    itemEnum = kvp.Value.Item;
                    count = kvp.Value.Count > 0 ? kvp.Value.Count : 1;
                    break;
                }
            }
            else if (_pendingDropItem != Items.itemlist.None)
            {
                // Legacy / host-local path fallback.
                itemEnum = _pendingDropItem;
                count = _pendingDropCount > 0 ? _pendingDropCount : 1;
                clearKey = _pendingDropKey;
            }
            else
                return;

            try
            {
                var item = InventoryManager.getItem(itemEnum);
                if (item != null)
                    InventoryManager.RemoveItem(item, count);
            }
            catch { }
            // Allow a later respawn/dump of the same key to be claimed again.
            if (clearKey >= 0)
            {
                _claimedDrops.Remove(clearKey);
                _awaitingDrops.Remove(clearKey);
            }
            _pendingDropKey = -1;
            _pendingDropItem = Items.itemlist.None;
            _pendingDropCount = 0;
        }
    }

    [HarmonyPatch(typeof(ItemPickup), "dialoguerCallback")]
    public static class ItemPickupConfirmPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ItemPickup __instance)
        {
            try { ItemPickupPatches.NoteTakenFromCallback(__instance); }
            catch (System.Exception ex) { ModRuntime.Log?.Warning("[Drop] callback: " + ex.Message); }
            try { ItemSystem.DroppedItemManager.TickDeferred(); } catch { }
        }
    }

    [HarmonyPatch(typeof(ItemPickup), "release")]
    public static class ItemPickupReleasePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(ItemPickup __instance)
        {
            try
            {
                if (__instance != null && ItemSystem.DroppedItemManager.IsDropped(__instance))
                {
                    ItemPickupPatches.TakeDropped(__instance);
                    return false;
                }
            }
            catch (System.Exception ex) { ModRuntime.Log?.Warning("[Drop] release: " + ex.Message); }
            return true;
        }
    }

    [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.AddItem), typeof(AnItem), typeof(int))]
    public static class InventoryAddItemCountPatch
    {
        [HarmonyPostfix]
        public static void Postfix(AnItem item)
        {
            ItemPickupPatches.NoteDroppedGrant(item);
            ItemPickupPatches.NoteCraftedKey(item);
        }
    }

    [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.AddItem), typeof(AnItem))]
    public static class InventoryAddItemPatch
    {
        [HarmonyPostfix]
        public static void Postfix(AnItem item)
        {
            ItemPickupPatches.NoteDroppedGrant(item);
            ItemPickupPatches.NoteCraftedKey(item);
        }
    }

    /// <summary>
    /// CombineRecipes.combine success → PartyKeyRing.Remove ingredients (NoteCraftedKey
    /// only Offers the result). Mirrors ConsumeKey / DetachDroppedKey ring drops.
    /// </summary>
    [HarmonyPatch(typeof(CombineRecipes), nameof(CombineRecipes.combine))]
    public static class CombineRecipesCraftPatch
    {
        [HarmonyPostfix]
        public static void Postfix(AnItem itemA, AnItem itemB, AnItem __result)
        {
            try { ItemPickupPatches.NoteCraftConsumed(itemA, itemB, __result); }
            catch (System.Exception ex) { ModRuntime.Log?.Warning("[KeyRing] craft remove: " + ex.Message); }
        }
    }
}
