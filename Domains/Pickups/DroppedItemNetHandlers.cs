using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using SyncRADation.ItemSystem;
using SyncRADation.Patches;
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
        private readonly Dictionary<int, RekeyedTo> _rekeyed = new Dictionary<int, RekeyedTo>();
        private const float RekeyMapSeconds = 60f;
        private readonly List<ItemBag.Stack> _bagScratch = new List<ItemBag.Stack>(8);

        internal DroppedItemNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new ArgumentNullException(nameof(net));
        }

        /// <summary>StopNetwork. Floor-claim dedupe and the storage transaction are SessionReset clears ("DropClaims", "StorageTxn").</summary>
        internal void Reset()
        {
            _nextItemIndex = 1;
            _rekeyed.Clear();
            // Drop claims (SessionReset "DropClaims") and StorageTxn ("StorageTxn") are their own registry steps.
        }

        internal void SendDropItem(DropItemSpawnMessage msg)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)NetMessageType.DropItemSpawn);
            msg.Serialize(writer);
            _net.BroadcastRaw(writer, DeliveryMethod.ReliableOrdered);
        }

        void SendItemPickedUp(ItemPickedUpMessage msg)
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
                if (!DroppedItemRegistry.TryGet(key, out _, out _))
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
            if (!NetGate.HostRole || playerId < 1) return;
            var old = new List<int>(4);
            foreach (var d in DroppedItemRegistry.All())
            {
                if (((d.Key >> 16) & 0xFF) == playerId) old.Add(d.Key);
            }
            // The id is about to be recycled with an index counter back at 1: claim records of its key space
            // (host side) are stale whether or not it left drops behind.
            ItemPickupPatches.PurgeDropClaimsOwnedBy(playerId);
            for (int i = 0; i < old.Count; i++)
            {
                int oldKey = old[i];
                ushort idx = AllocateItemIndex();
                int newKey = (_net.LocalPlayerId << 16) | idx;
                if (!DroppedItemRegistry.Rekey(oldKey, newKey)) continue;
                ItemPickupPatches.ForgetDropClaim(newKey);
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

        void SendDropRekey(DropRekeyMessage msg)
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
            ItemPickupPatches.PurgeDropClaimsOwnedBy(msg.OldOwner);
            ItemPickupPatches.ForgetDropClaim(newKey);
            DroppedItemRegistry.Rekey(oldKey, newKey);
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
        /// Spawn a new floor item in this peer's key space at pos and tell every peer. Null when the spawn failed
        /// (nothing was sent).
        /// </summary>
        GameObject SpawnOwnDrop(Items.itemlist item, int count, Vector3 pos, string what)
        {
            ushort idx = AllocateItemIndex();
            int key = (_net.LocalPlayerId << 16) | idx;
            GameObject spawned;
            try { spawned = DroppedItemSpawner.SpawnLocalItem(item, count, key, pos); }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[Drop] " + what + " spawn crashed: " + ex.Message);
                return null;
            }
            if (spawned == null)
            {
                ModRuntime.Log?.Warning("[Drop] " + what + " spawn failed " + item);
                return null;
            }
            SendDropItem(new DropItemSpawnMessage
            {
                SenderID = (byte)_net.LocalPlayerId,
                LocalIndex = idx,
                ItemEnum = (ushort)item,
                Count = count,
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z
            });
            return spawned;
        }

        Transform LocalPlayerTransform()
        {
            var localPlayer = _net.GetLocalPlayer();
            if (localPlayer == null) localPlayer = PlayerState.player;
            return localPlayer != null ? localPlayer.transform : null;
        }

        /// <summary>
        /// Part of a taken floor stack the bag could not hold (spilled only after the claim is confirmed): drop it
        /// at the local player's feet as a normal dropped item so the remainder is neither lost nor duplicated.
        /// </summary>
        internal bool DropOverflow(Items.itemlist item, int count)
        {
            if (item == Items.itemlist.None || count <= 0) return false;
            var player = LocalPlayerTransform();
            if (player == null) return false;
            var pos = DroppedItemRegistry.FloorDropPos(player);
            int n = DroppedItemRegistry.SanitizeStack(count, PartyKeyRing.IsKeyOrObject(item));
            if (SpawnOwnDrop(item, n, pos, "overflow") == null) return false;
            ModRuntime.Log?.Msg("[Drop] overflow " + item + " x" + n);
            return true;
        }

        internal void DumpDroppedItems()
        {
            string scene = "";
            try { scene = SceneManager.GetActiveScene().name ?? ""; } catch (Exception e) { Guard.Swallow(e); }
            foreach (var drop in DroppedItemRegistry.All())
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
            var pos = DroppedItemRegistry.FloorDropPos(localPlayer.transform);

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
                int have = ItemBag.CountInBag(itemToDrop);
                if (have > 0)
                    fromBag = true;
                else if (PartyKeyRing.Has(itemToDrop))
                    have = 1;
                else
                {
                    ModRuntime.Log?.Msg("[Drop] count 0 for " + itemToDrop + " (not in bag or key ring)");
                    return false;
                }
                count = DroppedItemRegistry.SanitizeStack(have, PartyKeyRing.IsKeyOrObject(itemToDrop));
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[Drop] resolve count failed: " + ex.Message);
                return false;
            }

            // Spawn + announce before consuming: a failed spawn must leave the item in the bag.
            if (SpawnOwnDrop(itemToDrop, count, pos, "drop") == null) return false;
            try { ConsumeDropped(itemToDrop, bagItem, anItem, count); }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[Drop] consume failed: " + ex.Message);
            }
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
            if (!DroppedItemRegistry.TryGet(itemKey, out itemEnum, out storedCount))
            {
                // The claim was sent under a key that the host has since rekeyed (departed owner's drop):
                // follow it to the drop's new key and grant, instead of denying an item that is on the floor.
                int mapped = ResolveRekeyed(itemKey);
                if (mapped == itemKey || !DroppedItemRegistry.TryGet(mapped, out itemEnum, out storedCount))
                    return false;
                PlaytestLog.Event("Pickup", "claim " + itemKey + " followed rekey -> " + mapped);
                itemKey = mapped;
            }

            var item = InventoryManager.getItem(itemEnum);
            if (item == null) return false;
            int count = DroppedItemRegistry.SanitizeStack(storedCount, PartyKeyRing.IsKeyOrObject(itemEnum));
            // skipLocalGrant: the local take already added (and measured) what the bag holds.
            bool needGrant = claimerId == _net.LocalPlayerId && !skipLocalGrant;
            if (needGrant && !ItemBag.BagHasRoom(itemEnum))
            {
                reason = "bag full";
                return false;
            }

            // Only the claimer gets the item. Key/Object reach everyone else through the party key ring
            // (Note + Broadcast below), never as a bag copy on every peer.
            SendItemPickedUp(new ItemPickedUpMessage
            {
                SenderID = (byte)((itemKey >> 16) & 0xFF),
                LocalIndex = (ushort)(itemKey & 0xFFFF),
                ItemEnum = (ushort)itemEnum,
                Count = count,
                GrantToReceiver = false,
                ClaimerPlayerId = (byte)claimerId
            });

            if (claimerId == _net.LocalPlayerId)
                DroppedItemRegistry.DespawnWhenIdle(itemKey);
            else
                DroppedItemRegistry.DespawnItem(itemKey);

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

            ModRuntime.Log?.Msg("[Pickup] Claimed " + itemEnum + " x" + count + " by " + claimerId);
            return true;
        }

        internal void HandleDropItemSpawn(DropItemSpawnMessage msg)
        {
            int key = (msg.SenderID << 16) | msg.LocalIndex;
            var kind = (Items.itemlist)msg.ItemEnum;
            if (DroppedItemRegistry.GetItem(key) != null)
            {
                Items.itemlist have;
                if (DroppedItemRegistry.TryGet(key, out have, out _) && have != kind)
                    ModRuntime.Log?.Warning("[Drop] key collision " + key + " existing=" + have
                        + " incoming=" + kind + " — dropped duplicate spawn");
                DetachDroppedKey(kind);
                return;
            }
            var pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            try
            {
                // SpawnLocalItem forgets any stale claim record of this key (ids / indices recycle).
                if (DroppedItemSpawner.SpawnLocalItem(kind, msg.Count, key, pos) == null)
                {
                    ModRuntime.Log?.Warning("[Drop] remote spawn null " + kind);
                    return;
                }
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[Drop] remote spawn crashed: " + ex.Message);
                return;
            }
            DetachDroppedKey(kind);
            ModRuntime.Log?.Msg("[Drop] Remote dropped " + kind
                + " at " + pos.x.ToString("F1") + "," + pos.y.ToString("F1") + "," + pos.z.ToString("F1"));
        }

        internal void HandleItemPickedUp(ItemPickedUpMessage msg)
        {
            int key = (msg.SenderID << 16) | msg.LocalIndex;
            DroppedItemRegistry.DespawnWhenIdle(key);
            // ClaimerPlayerId (host-authored) is informational on receivers: the claimer already took the
            // item natively / via ack, everyone else only retires the floor object. Kept for the log.
            PlaytestLog.Verbose("Pickup", "drop " + key + " taken by p" + msg.ClaimerPlayerId);
        }

        /// <summary>
        /// Unique left the bag for the floor (G-drop / remote DropItemSpawn / death floor).
        /// Ring Remove alone leaves bag copies on other peers (InLocalBag) —
        /// same class as craft/UseItem pre-0.5.14. Strip bag copies on every peer that sees
        /// the spawn; DropItemSpawn BroadcastRaw is the fan-out (protocol 10, no new msg).
        /// </summary>
        void DetachDroppedKey(Items.itemlist item)
        {
            if (!PartyKeyRing.IsKeyOrObject(item)) return;
            PartyKeyRing.Remove(item);
            PartyKeyRing.StripBagMirrors(item);
            if (NetGate.HostRole)
                PartyKeyRing.Broadcast();
        }

        void ConsumeDropped(Items.itemlist itemToDrop, AnItem bagItem, AnItem anItem, int count)
        {
            PartyKeyRing.Remove(itemToDrop);
            int n = count > 0 ? count : 1;
            AnItem held = PartyKeyRing.FindInBag(anItem) ?? bagItem;
            if (held != null)
            {
                try { InventoryManager.RemoveItem(held, n); } catch (Exception e) { Guard.Swallow(e); }
            }
            // Other bag instances of the same kind (a second grant) are separate entries: take them out too.
            var bag = ItemBag.Bag(_bagScratch);
            for (int i = 0; i < bag.Count; i++)
            {
                var other = bag[i].Item;
                if (other == null || bag[i].Count <= 0) continue;
                try { if (other._item == itemToDrop) InventoryManager.RemoveItem(other, n); }
                catch (Exception e) { Guard.Swallow(e); }
            }
            try
            {
                if (InventoryManager.CurrentItem != null && InventoryManager.CurrentItem._item == itemToDrop)
                    InventoryManager.CurrentItem = null;
            }
            catch (Exception e) { Guard.Swallow(e); }
        }

        AnItem ResolveSelectedItem()
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
                    var igc = inv.igc;
                    if (igc != null && (slot < 0 || list == null || slot >= list.Count))
                        slot = igc.currentSlot;
                }
                picked = ItemFromList(list, slot);
            }
            catch (Exception ex) { ModRuntime.Log?.Warning("[Drop] currentItems: " + ex.Message); }
            if (picked != null) return picked;

            try
            {
                picked = Droppable(InventoryManager.CurrentItem) ?? Droppable(InventoryManager.EquippedTool);
                if (picked == null)
                {
                    var w = InventoryManager.EquippedWeapon;
                    if (w != null) picked = Droppable(w.parentItem);
                }
            }
            catch (Exception e) { Guard.Swallow(e); }
            if (picked != null) return picked;

            string bagLog = "";
            int bagN = 0;
            var bag = ItemBag.Bag(_bagScratch);
            for (int i = 0; i < bag.Count; i++)
            {
                if (bag[i].Item == null || bag[i].Count <= 0) continue;
                picked = Droppable(bag[i].Item);
                if (picked != null) return picked;
                bagN++;
                try { bagLog += " " + bag[i].Item._item + "x" + bag[i].Count; } catch (Exception e) { Guard.Swallow(e); }
            }
            ModRuntime.Log?.Msg("[Drop] resolve fail gs=" + gs
                + " inv=" + (inv != null)
                + " bagUi=" + inBagUi
                + " bagN=" + bagN
                + bagLog);
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
                    if (fallback == null) fallback = inv;
                }
                catch (Exception e) { Guard.Swallow(e); }
            }
            return fallback;
        }

        static void RefreshInventoryAfterDrop()
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
    }
}
