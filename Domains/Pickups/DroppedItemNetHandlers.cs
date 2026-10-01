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

        // Host: old key -> new key of a departed peer's rehomed drop. A claim that left a client before it
        // saw the DropRekey still names the old key; the host maps it instead of denying a drop that exists.
        private struct RekeyedTo { public int NewKey; public float Until; }
        private readonly System.Collections.Generic.Dictionary<int, RekeyedTo> _rekeyed
            = new System.Collections.Generic.Dictionary<int, RekeyedTo>();
        private const float RekeyMapSeconds = 60f;

        internal DroppedItemNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new ArgumentNullException(nameof(net));
        }

        internal void Reset()
        {
            _nextItemIndex = 1;
            _rekeyed.Clear();
            // Player ids + local indices recycle; stale FinishDroppedNative dedupe soft-locks take.
            try { SyncRADation.Patches.ItemPickupPatches.ResetDropClaims(); } catch (Exception e) { Guard.Swallow(e); }
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

        /// <summary>
        /// Next free local index for this peer's key namespace. Skips 0 and any index whose key is still
        /// on the floor, so a wrap-around (or a host that adopted a departed peer's drops) cannot collide.
        /// </summary>
        internal ushort AllocateItemIndex()
        {
            for (int guard = 0; guard < 0x10000; guard++)
            {
                ushort idx = _nextItemIndex++;
                if (idx == 0) continue;
                int key = (_net.LocalPlayerId << 16) | idx;
                if (!DroppedItemManager.TryGet(key, out _, out _))
                    return idx;
            }
            return _nextItemIndex++;
        }

        /// <summary>
        /// Host: a peer left. Its drops keep the key (peerId &lt;&lt; 16 | index), but that id is recycled
        /// for the next joiner whose index counter restarts at 1 — a new drop would collide with (and
        /// silently reuse) the old floor item. Re-key every orphaned drop into the host namespace.
        /// </summary>
        internal void RehomeDropsOf(int playerId)
        {
            if (_net.Role != NetworkRole.Host || playerId < 1) return;
            var old = new System.Collections.Generic.List<int>(4);
            foreach (var d in DroppedItemManager.All())
            {
                if (((d.Key >> 16) & 0xFF) == playerId) old.Add(d.Key);
            }
            // The id is about to be recycled with an index counter back at 1: claim records of its key space
            // (host side) are stale whether or not it left drops behind.
            SyncRADation.Patches.ItemPickupPatches.PurgeDropClaimsOwnedBy(playerId);
            for (int i = 0; i < old.Count; i++)
            {
                int oldKey = old[i];
                ushort idx = AllocateItemIndex();
                int newKey = (_net.LocalPlayerId << 16) | idx;
                if (!DroppedItemManager.Rekey(oldKey, newKey)) continue;
                SyncRADation.Patches.ItemPickupPatches.ForgetDropClaim(newKey);
                _rekeyed[oldKey] = new RekeyedTo { NewKey = newKey, Until = Time.unscaledTime + RekeyMapSeconds };
                SendDropRekey(new DropRekeyMessage
                {
                    OldOwner = (byte)((oldKey >> 16) & 0xFF),
                    OldIndex = (ushort)(oldKey & 0xFFFF),
                    NewOwner = (byte)_net.LocalPlayerId,
                    NewIndex = idx
                });
                PlaytestLog.Event("Pickup", "rehomed drop " + oldKey + " -> " + newKey + " (p" + playerId + " left)");
            }
        }

        internal void SendDropRekey(DropRekeyMessage msg)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.DropRekey);
            msg.Serialize(writer);
            _net.BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        internal void HandleDropRekey(DropRekeyMessage msg)
        {
            // Host-authored only (dispatch drops client-originated copies).
            int oldKey = (msg.OldOwner << 16) | msg.OldIndex;
            int newKey = (msg.NewOwner << 16) | msg.NewIndex;
            // Owner is gone and its id will be recycled: drop every stale claim record of that key space,
            // plus any under the new key (index re-use), so the next drop with that key is claimable.
            SyncRADation.Patches.ItemPickupPatches.PurgeDropClaimsOwnedBy(msg.OldOwner);
            SyncRADation.Patches.ItemPickupPatches.ForgetDropClaim(newKey);
            DroppedItemManager.Rekey(oldKey, newKey);
        }

        /// <summary>Host: the key a claim should hit, following a recent rekey of the drop it names.</summary>
        int ResolveRekeyed(int key)
        {
            if (_rekeyed.Count == 0) return key;
            RekeyedTo to;
            if (!_rekeyed.TryGetValue(key, out to)) return key;
            if (Time.unscaledTime > to.Until)
            {
                _rekeyed.Remove(key);
                return key;
            }
            return to.NewKey;
        }

        /// <summary>
        /// Host gate for a client-originated ItemPickedUp: the host is the only legitimate author of a
        /// floor-item claim, so a client may only retire a drop it owns, never grant, never someone else's.
        /// </summary>
        internal bool AcceptClientPickedUp(ref ItemPickedUpMessage msg, int senderId)
        {
            // ClaimerPlayerId is host-authored (TryClaimDropped stamps it; relayed client copies are
            // re-stamped here). A client that names a different claimer is lying about who took it.
            if (msg.ClaimerPlayerId != 0 && msg.ClaimerPlayerId != senderId) return false;
            msg.ClaimerPlayerId = (byte)senderId;
            if (msg.GrantToReceiver) return false;
            return msg.SenderID == senderId;
        }

        /// <summary>
        /// Part of a taken floor stack the bag could not hold (spilled only after the claim is confirmed): drop it
        /// at the local player's feet as a normal dropped item so the remainder is neither lost nor duplicated.
        /// </summary>
        internal bool DropOverflow(Items.itemlist item, int count)
        {
            if (item == Items.itemlist.None || count <= 0) return false;
            var localPlayer = _net.GetLocalPlayer();
            if (localPlayer == null) localPlayer = PlayerState.player;
            if (localPlayer == null) return false;
            var pos = DroppedItemManager.FloorDropPos(localPlayer.transform);
            int n = DroppedItemManager.SanitizeStack(count, PartyKeyRing.IsKeyOrObject(item));
            ushort idx = AllocateItemIndex();
            int key = (_net.LocalPlayerId << 16) | idx;
            GameObject spawned = null;
            try { spawned = DroppedItemManager.SpawnLocalItem(item, n, key, pos); }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[Drop] overflow spawn crashed: " + ex.Message);
                return false;
            }
            if (spawned == null) return false;
            SendDropItem(new DropItemSpawnMessage
            {
                SenderID = (byte)_net.LocalPlayerId,
                LocalIndex = idx,
                ItemEnum = (ushort)item,
                Count = n,
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z
            });
            ModRuntime.Log?.Msg("[Drop] overflow " + item + " x" + n + " key=" + key);
            return true;
        }

        internal void DumpDroppedItems()
        {
            string scene = "";
            try { scene = SceneManager.GetActiveScene().name ?? ""; } catch (Exception e) { Guard.Swallow(e); }
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
            try { if (anItem != null) itemToDrop = anItem._item; } catch (Exception e) { Guard.Swallow(e); }

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

            ushort idx = AllocateItemIndex();
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
            {
                // The claim was sent under a key that the host has since rekeyed (departed owner's drop):
                // follow it to the drop's new key and grant, instead of denying an item that is on the floor.
                int mapped = ResolveRekeyed(itemKey);
                if (mapped == itemKey || !DroppedItemManager.TryGet(mapped, out itemEnum, out storedCount))
                    return false;
                PlaytestLog.Event("Pickup", "claim " + itemKey + " followed rekey -> " + mapped);
                itemKey = mapped;
            }

            var item = InventoryManager.getItem(itemEnum);
            if (item == null) return false;
            int count = DroppedItemManager.SanitizeStack(storedCount, PartyKeyRing.IsKeyOrObject(itemEnum));
            // skipLocalGrant: the local take already added (and measured) what the bag holds.
            bool needGrant = claimerId == _net.LocalPlayerId && !skipLocalGrant;
            if (needGrant && !DroppedItemManager.BagHasRoom(itemEnum))
            {
                reason = "bag full";
                return false;
            }

            int senderID = (itemKey >> 16) & 0xFF;
            ushort localIdx = (ushort)(itemKey & 0xFFFF);

            // Only the claimer gets the item. Key/Object reach everyone else through the party key ring
            // (Note + Broadcast below), never as a bag copy on every peer.
            SendItemPickedUp(new ItemPickedUpMessage
            {
                SenderID = (byte)senderID,
                LocalIndex = localIdx,
                ItemEnum = (ushort)itemEnum,
                Count = count,
                GrantToReceiver = false,
                ClaimerPlayerId = (byte)claimerId
            });

            if (claimerId == _net.LocalPlayerId)
                DroppedItemManager.DespawnWhenIdle(itemKey);
            else
                DroppedItemManager.DespawnItem(itemKey);

            PartyKeyRing.Note(item);
            PartyKeyRing.Broadcast();

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

        internal void HandleDropItemSpawn(DropItemSpawnMessage msg)
        {
            int key = (msg.SenderID << 16) | msg.LocalIndex;
            if (DroppedItemManager.GetItem(key) != null)
            {
                Items.itemlist have; int haveCount;
                if (DroppedItemManager.TryGet(key, out have, out haveCount) && have != (Items.itemlist)msg.ItemEnum)
                    ModRuntime.Log?.Warning("[Drop] key collision " + key + " existing=" + have
                        + " incoming=" + (Items.itemlist)msg.ItemEnum + " — dropped duplicate spawn");
                DetachDroppedKey((Items.itemlist)msg.ItemEnum);
                return;
            }
            var pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            SyncRADation.Patches.ItemPickupPatches.ForgetDropClaim(key);
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
            // ClaimerPlayerId (host-authored) is informational on receivers: the claimer already took the
            // item natively / via ack, everyone else only retires the floor object. Kept for the log.
            PlaytestLog.Verbose("Pickup", "drop " + key + " taken by p" + msg.ClaimerPlayerId);
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
                try { InventoryManager.RemoveItem(held, n); } catch (Exception e) { Guard.Swallow(e); }
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
                        try { InventoryManager.RemoveItem(extra[i], n); } catch (Exception e) { Guard.Swallow(e); }
                    }
                }
            }
            catch (Exception e) { Guard.Swallow(e); }
            try
            {
                if (InventoryManager.CurrentItem != null && InventoryManager.CurrentItem._item == itemToDrop)
                    InventoryManager.CurrentItem = null;
            }
            catch (Exception e) { Guard.Swallow(e); }
        }

        static AnItem ResolveSelectedItem()
        {
            PlayerState.gameStates gs = PlayerState.gameStates.play;
            try { gs = PlayerState.gameState; } catch (Exception e) { Guard.Swallow(e); }
            InventoryBase inv = FindInventory();
            bool inBagUi = gs == PlayerState.gameStates.inventory;
            try { if (inv != null && inv.inventoryOpen) inBagUi = true; } catch (Exception e) { Guard.Swallow(e); }

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
                    catch (Exception e) { Guard.Swallow(e); }
                }
                picked = ItemFromList(list, slot);
            }
            catch (Exception ex) { ModRuntime.Log?.Warning("[Drop] currentItems: " + ex.Message); }
            if (picked != null) return picked;

            try { picked = Droppable(InventoryManager.CurrentItem); } catch (Exception e) { Guard.Swallow(e); }
            if (picked != null) return picked;
            try { picked = Droppable(InventoryManager.EquippedTool); } catch (Exception e) { Guard.Swallow(e); }
            if (picked != null) return picked;
            try
            {
                var w = InventoryManager.EquippedWeapon;
                if (w != null) picked = Droppable(w.parentItem);
            }
            catch (Exception e) { Guard.Swallow(e); }
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
            catch (Exception e) { Guard.Swallow(e); }
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
                catch (Exception e) { Guard.Swallow(e); }
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
                    catch (Exception e) { Guard.Swallow(e); }
                }
            }
            catch (Exception e) { Guard.Swallow(e); }
        }
    }
}
