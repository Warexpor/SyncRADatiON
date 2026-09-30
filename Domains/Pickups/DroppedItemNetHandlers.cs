using System;
using LiteNetLib;
using LiteNetLib.Utils;
using SyncRADation.ItemSystem;
using SyncRADation.Sync;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SyncRADation.Networking
{
    /// <summary>Player-dropped items: spawn wire, claim, join dump.</summary>
    internal sealed class DroppedItemNetHandlers
    {
        private readonly LanNetworkManager _net;
        private ushort _nextItemIndex = 1;

        internal DroppedItemNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new ArgumentNullException(nameof(net));
        }

        internal void Reset()
        {
            _nextItemIndex = 1;
            // Player ids + local indices recycle; stale FinishDroppedNative dedupe soft-locks take.
            try { SyncRADation.Patches.ItemPickupPatches.ResetDropClaims(); } catch { }
            // Session end: an in-flight storage ack will never arrive.
            StorageTxn.Reset();
        }

        internal void SendDropItem(DropItemSpawnMessage msg)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.DropItemSpawn);
            msg.Serialize(writer);
            _net.BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        internal void SendItemPickedUp(ItemPickedUpMessage msg)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.ItemPickedUp);
            msg.Serialize(writer);
            _net.BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        internal ushort AllocateItemIndex()
        {
            return _nextItemIndex++;
        }

        internal void DumpDroppedItems()
        {
            string scene = "";
            try { scene = SceneManager.GetActiveScene().name ?? ""; } catch { }
            foreach (var drop in DroppedItemManager.All())
            {
                if (drop.Count <= 0) continue;
                if (!string.IsNullOrEmpty(drop.Scene) && !string.IsNullOrEmpty(scene)
                    && drop.Scene != scene)
                    continue;
                SendDropItem(new DropItemSpawnMessage
                {
                    SenderID = (byte)((drop.Key >> 16) & 0xFF),
                    LocalIndex = (ushort)(drop.Key & 0xFFFF),
                    ItemEnum = (ushort)drop.Item,
                    Count = drop.Count,
                    PosX = drop.Pos.x,
                    PosY = drop.Pos.y,
                    PosZ = drop.Pos.z
                });
            }
        }

        internal bool TryDropCurrentItem()
        {
            return TryDropItem(ResolveSelectedItem());
        }

        internal bool TryDropItem(AnItem anItem)
        {
            if (anItem == null)
                anItem = ResolveSelectedItem();
            PlayerState.gameStates gs;
            try { gs = PlayerState.gameState; }
            catch { return false; }
            if (gs != PlayerState.gameStates.play && gs != PlayerState.gameStates.inventory)
            {
                ModRuntime.Log?.Msg("[Drop] skip state=" + gs);
                return false;
            }

            var localPlayer = _net.GetLocalPlayer();
            if (localPlayer == null) localPlayer = PlayerState.player;
            if (localPlayer == null)
            {
                ModRuntime.Log?.Msg("[Drop] No local player");
                return false;
            }
            _net.SetLocalPlayer(localPlayer);
            var pos = DroppedItemManager.FloorDropPos(localPlayer.transform);

            Items.itemlist itemToDrop = Items.itemlist.None;
            try { if (anItem != null) itemToDrop = anItem._item; } catch { }

            if (itemToDrop == Items.itemlist.None || itemToDrop == Items.itemlist.Injector)
            {
                ModRuntime.Log?.Msg("[Drop] No item to drop (select a slot, then G / DROP)");
                return false;
            }

            int count;
            bool fromBag = false;
            AnItem bagItem = null;
            try
            {
                bagItem = PartyKeyRing.FindInBag(anItem);
                if (bagItem == null)
                    bagItem = InventoryManager.getItem(itemToDrop);
                int have = DroppedItemManager.CountInBag(itemToDrop);
                if (have > 0)
                    fromBag = true;
                else if (PartyKeyRing.Has(itemToDrop))
                    have = 1;
                else
                {
                    ModRuntime.Log?.Msg("[Drop] count 0 for " + itemToDrop + " (not in bag or key ring)");
                    return false;
                }
                count = DroppedItemManager.SanitizeStack(have, PartyKeyRing.IsKeyOrObject(itemToDrop));
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[Drop] resolve count failed: " + ex.Message);
                return false;
            }

            ushort idx = _nextItemIndex++;
            int key = (_net.LocalPlayerId << 16) | idx;
            GameObject spawned = null;
            try { spawned = DroppedItemManager.SpawnLocalItem(itemToDrop, count, key, pos); }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[Drop] spawn crashed: " + ex.Message);
                return false;
            }
            if (spawned == null)
            {
                ModRuntime.Log?.Warning("[Drop] spawn failed " + itemToDrop);
                return false;
            }

            try { ConsumeDropped(itemToDrop, bagItem, anItem, count); }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[Drop] consume failed: " + ex.Message);
            }

            SendDropItem(new DropItemSpawnMessage
            {
                SenderID = (byte)_net.LocalPlayerId,
                LocalIndex = idx,
                ItemEnum = (ushort)itemToDrop,
                Count = count,
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z
            });

            RefreshInventoryAfterDrop();
            DetachDroppedKey(itemToDrop);
            ModRuntime.Log?.Msg("[Drop] Dropped " + itemToDrop + " x" + count
                + (fromBag ? " from bag" : " from key ring")
                + " at " + pos.x.ToString("F2") + "," + pos.y.ToString("F2") + "," + pos.z.ToString("F2"));
            return true;
        }

        internal bool TryClaimDropped(int itemKey, int claimerId, out string reason, bool skipLocalGrant = false)
        {
            reason = "";
            Items.itemlist itemEnum;
            int storedCount;
            if (!DroppedItemManager.TryGet(itemKey, out itemEnum, out storedCount))
                return false;

            var item = InventoryManager.getItem(itemEnum);
            if (item == null) return false;
            int count = DroppedItemManager.SanitizeStack(storedCount, PartyKeyRing.IsKeyOrObject(itemEnum));
            bool shared = IsSharedItem(itemEnum);
            if (!shared && claimerId == _net.LocalPlayerId && !BagHasRoom(itemEnum))
            {
                reason = "bag full";
                return false;
            }

            int senderID = (itemKey >> 16) & 0xFF;
            ushort localIdx = (ushort)(itemKey & 0xFFFF);

            SendItemPickedUp(new ItemPickedUpMessage
            {
                SenderID = (byte)senderID,
                LocalIndex = localIdx,
                ItemEnum = (ushort)itemEnum,
                Count = count,
                GrantToReceiver = shared
            });

            if (claimerId == _net.LocalPlayerId)
                DroppedItemManager.DespawnWhenIdle(itemKey);
            else
                DroppedItemManager.DespawnItem(itemKey);

            PartyKeyRing.Note(item);
            PartyKeyRing.Broadcast();

            if (shared)
                return true;

            bool needGrant = claimerId == _net.LocalPlayerId
                && (!skipLocalGrant || DroppedItemManager.CountInBag(itemEnum) <= 0);
            if (needGrant)
            {
                try { InventoryManager.AddItem(item, count); }
                catch (Exception ex)
                {
                    ModRuntime.Log?.Warning("[Pickup] AddItem failed: " + ex.Message);
                    return false;
                }
            }
            else if (claimerId != _net.LocalPlayerId)
                reason = "";

            ModRuntime.Log?.Msg("[Pickup] Claimed " + itemEnum + " x" + count + " by " + claimerId);
            return true;
        }

        internal bool HasBagRoom(Items.itemlist itemEnum) => BagHasRoom(itemEnum);

        internal void HandleDropItemSpawn(DropItemSpawnMessage msg)
        {
            int key = (msg.SenderID << 16) | msg.LocalIndex;
            if (DroppedItemManager.GetItem(key) != null)
            {
                DetachDroppedKey((Items.itemlist)msg.ItemEnum);
                return;
            }
            var pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            try
            {
                var go = DroppedItemManager.SpawnLocalItem((Items.itemlist)msg.ItemEnum, msg.Count, key, pos);
                if (go == null)
                {
                    ModRuntime.Log?.Warning("[Drop] remote spawn null " + (Items.itemlist)msg.ItemEnum);
                    return;
                }
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[Drop] remote spawn crashed: " + ex.Message);
                return;
            }
            DetachDroppedKey((Items.itemlist)msg.ItemEnum);
            ModRuntime.Log?.Msg("[Drop] Remote dropped " + (Items.itemlist)msg.ItemEnum
                + " at " + pos.x.ToString("F1") + "," + pos.y.ToString("F1") + "," + pos.z.ToString("F1"));
        }

        internal void HandleItemPickedUp(ItemPickedUpMessage msg)
        {
            int key = (msg.SenderID << 16) | msg.LocalIndex;
            DroppedItemManager.DespawnWhenIdle(key);

            if (msg.GrantToReceiver)
            {
                try
                {
                    var item = InventoryManager.getItem((Items.itemlist)msg.ItemEnum);
                    int have = 0;
                    try { if (item != null) have = DroppedItemManager.CountInBag((Items.itemlist)msg.ItemEnum); } catch { }
                    if (item != null && have <= 0)
                    {
                        int grant = DroppedItemManager.SanitizeStack(msg.Count,
                            PartyKeyRing.IsKeyOrObject((Items.itemlist)msg.ItemEnum));
                        InventoryManager.AddItem(item, grant);
                        PartyKeyRing.OfferToHost(item);
                        ModRuntime.Log?.Msg("[Drop] Shared item granted: " + (Items.itemlist)msg.ItemEnum);
                    }
                }
                catch (Exception ex)
                {
                    ModRuntime.Log?.Warning("[Drop] Grant shared item failed: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Unique left the bag for the floor (G-drop / remote DropItemSpawn / death floor).
        /// Ring Remove alone leaves EnsureInBag bag ghosts on other peers (InLocalBag) —
        /// same class as craft/UseItem pre-0.5.14. Strip mirrors on every peer that sees
        /// the spawn; DropItemSpawn BroadcastRaw is the fan-out (protocol 10, no new msg).
        /// </summary>
        void DetachDroppedKey(Items.itemlist item)
        {
            if (!PartyKeyRing.IsKeyOrObject(item)) return;
            PartyKeyRing.Remove(item);
            PartyKeyRing.StripBagMirrors(item);
            if (_net.Role == NetworkRole.Host)
                PartyKeyRing.Broadcast();
        }

        static void ConsumeDropped(Items.itemlist itemToDrop, AnItem bagItem, AnItem anItem, int count)
        {
            PartyKeyRing.Remove(itemToDrop);
            int n = count > 0 ? count : 1;
            AnItem held = PartyKeyRing.FindInBag(anItem);
            if (held == null) held = bagItem;
            if (held != null)
            {
                try { InventoryManager.RemoveItem(held, n); } catch { }
            }
            try
            {
                var dict = InventoryManager.elsterItems;
                if (dict != null)
                {
                    var extra = new System.Collections.Generic.List<AnItem>();
                    var en = dict.GetEnumerator();
                    while (en.MoveNext())
                    {
                        var key = en.Current.key;
                        if (key != null && key._item == itemToDrop && en.Current.value > 0)
                            extra.Add(key);
                    }
                    en.Dispose();
                    for (int i = 0; i < extra.Count; i++)
                    {
                        try { InventoryManager.RemoveItem(extra[i], n); } catch { }
                    }
                }
            }
            catch { }
            try
            {
                if (InventoryManager.CurrentItem != null && InventoryManager.CurrentItem._item == itemToDrop)
                    InventoryManager.CurrentItem = null;
            }
            catch { }
        }

        static bool IsSharedItem(Items.itemlist itemEnum)
        {
            try
            {
                var itemData = InventoryManager.getItem(itemEnum);
                if (itemData == null) return false;
                return itemData.type == AnItem.AnItemType.Object;
            }
            catch { return false; }
        }

        static bool BagHasRoom(Items.itemlist itemEnum)
        {
            try
            {
                var item = InventoryManager.getItem(itemEnum);
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

        static AnItem ResolveSelectedItem()
        {
            PlayerState.gameStates gs = PlayerState.gameStates.play;
            try { gs = PlayerState.gameState; } catch { }
            InventoryBase inv = FindInventory();
            bool inBagUi = gs == PlayerState.gameStates.inventory;
            try { if (inv != null && inv.inventoryOpen) inBagUi = true; } catch { }

            AnItem picked = null;
            try
            {
                if (inv != null && inv.intMenuOn)
                    picked = Droppable(inv.intItem);
            }
            catch (Exception ex) { ModRuntime.Log?.Warning("[Drop] intItem: " + ex.Message); }
            if (picked != null) return picked;

            try
            {
                if (inv != null)
                    picked = Droppable(inv.lastItem);
            }
            catch (Exception ex) { ModRuntime.Log?.Warning("[Drop] lastItem: " + ex.Message); }
            if (picked != null && inBagUi) return picked;

            try
            {
                var list = InventoryBase.currentItems;
                int slot = -1;
                if (inv != null)
                {
                    slot = inv.currentSlot;
                    if (slot < 0)
                        slot = inv.selectedSlot;
                    try
                    {
                        var igc = inv.igc;
                        if (igc != null && (slot < 0 || list == null || slot >= list.Count))
                            slot = igc.currentSlot;
                    }
                    catch { }
                }
                picked = ItemFromList(list, slot);
            }
            catch (Exception ex) { ModRuntime.Log?.Warning("[Drop] currentItems: " + ex.Message); }
            if (picked != null) return picked;

            try { picked = Droppable(InventoryManager.CurrentItem); } catch { }
            if (picked != null) return picked;
            try { picked = Droppable(InventoryManager.EquippedTool); } catch { }
            if (picked != null) return picked;
            try
            {
                var w = InventoryManager.EquippedWeapon;
                if (w != null) picked = Droppable(w.parentItem);
            }
            catch { }
            if (picked != null) return picked;

            picked = FirstInBag();
            if (picked != null) return picked;

            string bag = "";
            int bagN = 0;
            try
            {
                var dict = InventoryManager.elsterItems;
                if (dict != null)
                {
                    var en = dict.GetEnumerator();
                    while (en.MoveNext())
                    {
                        var it = en.Current.key;
                        int n = en.Current.value;
                        if (it == null || n <= 0) continue;
                        bagN++;
                        bag += " " + it._item + "x" + n;
                    }
                    en.Dispose();
                }
            }
            catch { }
            ModRuntime.Log?.Msg("[Drop] resolve fail gs=" + gs
                + " inv=" + (inv != null)
                + " bagUi=" + inBagUi
                + " bagN=" + bagN
                + bag);
            return null;
        }

        static AnItem Droppable(AnItem item)
        {
            if (item == null) return null;
            try
            {
                var id = item._item;
                if (id == Items.itemlist.None || id == Items.itemlist.Injector)
                    return null;
                return item;
            }
            catch { return null; }
        }

        static AnItem ItemFromList(Il2CppSystem.Collections.Generic.List<AnItem> list, int slot)
        {
            if (list == null) return null;
            int n = list.Count;
            if (slot >= 0 && slot < n)
            {
                var at = Droppable(list[slot]);
                if (at != null) return at;
            }
            for (int i = 0; i < n; i++)
            {
                var at = Droppable(list[i]);
                if (at != null) return at;
            }
            return null;
        }

        static AnItem FirstInBag()
        {
            try
            {
                var dict = InventoryManager.elsterItems;
                if (dict == null) return null;
                var en = dict.GetEnumerator();
                AnItem found = null;
                while (en.MoveNext())
                {
                    var at = Droppable(en.Current.key);
                    if (at == null || en.Current.value <= 0) continue;
                    found = at;
                    break;
                }
                en.Dispose();
                return found;
            }
            catch { return null; }
        }

        static InventoryBase FindInventory()
        {
            var all = WorldLookup.All<InventoryBase>();
            if (all == null) return null;
            InventoryBase fallback = null;
            for (int i = 0; i < all.Length; i++)
            {
                var inv = all[i];
                if (inv == null) continue;
                try
                {
                    if (inv.gameObject == null || !inv.gameObject.scene.IsValid()) continue;
                    if (inv.inventoryOpen) return inv;
                    if (fallback == null && inv.gameObject.activeInHierarchy)
                        fallback = inv;
                    else if (fallback == null)
                        fallback = inv;
                }
                catch { }
            }
            return fallback;
        }

        static void RefreshInventoryAfterDrop()
        {
            try
            {
                var all = WorldLookup.All<InventoryBase>();
                if (all == null) return;
                for (int i = 0; i < all.Length; i++)
                {
                    var inv = all[i];
                    if (inv == null) continue;
                    try
                    {
                        if (inv.gameObject == null || !inv.gameObject.scene.IsValid()) continue;
                        if (inv.intMenuOn) inv.ToggleInteractMenu();
                        inv.updateItems();
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
