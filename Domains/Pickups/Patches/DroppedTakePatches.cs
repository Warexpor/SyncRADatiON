// Player-dropped floor item take: native TAKE inspect (yes/no) → bag add → host claim → floor despawn.
// The world-prop half of ItemPickupPatches is in ItemPickupPatches.cs.
using System.Collections.Generic;
using SyncRADation.ItemSystem;
using SyncRADation.Networking;
using SyncRADation.Sync;

namespace SyncRADation.Patches
{
    public static partial class ItemPickupPatches
    {
        static int _pendingDropKey = -1;
        static Items.itemlist _pendingDropItem;
        static int _pendingDropCount;
        // Units the confirmed take really added to the bag (what a failed claim takes back).
        static int _pendingDropAdded;

        static readonly HashSet<int> _claimedDrops = new HashSet<int>();
        static readonly List<int> _purgeScratch = new List<int>(4);

        struct AwaitingDrop
        {
            public Items.itemlist Item;
            /// <summary>Units the take really added (a FAIL ack removes exactly these).</summary>
            public int Count;
            /// <summary>Part of the stack the bag could not hold: spawned on the floor once the claim is confirmed.</summary>
            public int Spill;
        }

        // Host ack for client DroppedPickup — keyed so a second claim before the first
        // ack cannot overwrite / mis-revert under storage+drop soak (3–4 peers).
        static readonly Dictionary<int, AwaitingDrop> _awaitingDrops = new Dictionary<int, AwaitingDrop>();

        /// <summary>Forget the armed floor item (declined / session end / take finished).</summary>
        internal static void ClearPendingDrop()
        {
            _pendingDropKey = -1;
            _pendingDropItem = Items.itemlist.None;
            _pendingDropCount = 0;
            _pendingDropAdded = 0;
        }

        /// <summary>
        /// SessionReset ("DropClaims"): clear local claim dedupe + awaiting ack. Player ids recycle and the drop index
        /// counter restarts at 1, so stale _claimedDrops entries would no-op FinishDroppedNative (floor vanishes, no wire).
        /// </summary>
        internal static void ResetDropClaims()
        {
            _claimedDrops.Clear();
            _awaitingDrops.Clear();
            ClearPendingDrop();
        }

        /// <summary>A drop spawned (local / remote / respawn) under this key: an old claim of the same key is stale.</summary>
        internal static void ForgetDropClaim(int key)
        {
            _claimedDrops.Remove(key);
        }

        /// <summary>Owner left (ids recycle, index counter restarts): every claim record under its key space is stale.</summary>
        internal static void PurgeDropClaimsOwnedBy(int ownerId)
        {
            if (_claimedDrops.Count == 0) return;
            _purgeScratch.Clear();
            foreach (var k in _claimedDrops)
                if (((k >> 16) & 0xFF) == ownerId) _purgeScratch.Add(k);
            for (int i = 0; i < _purgeScratch.Count; i++) _claimedDrops.Remove(_purgeScratch[i]);
        }

        /// <summary>
        /// Confirmed "yes" on a floor item (dialoguerCallback, then again from the release prefix): add what
        /// the bag really takes, claim the drop, and spill the part that did not fit as a new floor item.
        /// The only path that claims a drop, so an unrelated AddItem can never retire one.
        /// </summary>
        internal static bool TakeDropped(ItemPickup p)
        {
            ClearPendingDrop();
            ArmDropped(p);
            int key = _pendingDropKey;
            if (key < 0 || !DroppedItemRegistry.TryGet(key, out _, out _))
            {
                ClearPendingDrop();
                return false;
            }
            // Second call of the same take (release prefix after the callback): already granted + claimed.
            if (!_claimedDrops.Contains(key))
            {
                int added = GrantDropped();
                if (added <= 0)
                {
                    // Stack at max / no free slot: nothing moved, the floor item stays for everyone.
                    PlaytestLog.Event("Drop", "take " + _pendingDropItem + " x" + _pendingDropCount
                        + " key=" + key + " — bag took nothing, drop stays");
                    ClearPendingDrop();
                    DroppedItemRegistry.RestorePlay();
                    return true;
                }
                _pendingDropAdded = added;
            }
            FinishDroppedNative(p);
            DroppedItemRegistry.RestorePlay();
            return true;
        }

        /// <summary>AddItem the armed stack and return how many units the bag really took (AddItem caps at maxNumber).</summary>
        static int GrantDropped()
        {
            var id = _pendingDropItem;
            if (id == Items.itemlist.None) return 0;
            int want = _pendingDropCount > 0 ? _pendingDropCount : 1;
            int before = ItemBag.CountInBag(id);
            // Native AddItem ignores maxSlots: a new stack needs a free slot (native pickUp shows _nospaceDialogue).
            if (before <= 0 && !ItemBag.BagHasRoom(id)) return 0;
            try
            {
                var item = InventoryManager.getItem(id);
                if (item == null) return 0;
                InventoryManager.AddItem(item, want);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[Drop] grant: " + ex.Message);
            }
            int added = ItemBag.CountInBag(id) - before;
            return added > 0 ? added : 0;
        }

        static void RemoveAdded(Items.itemlist itemEnum, int n)
        {
            if (itemEnum == Items.itemlist.None || n <= 0) return;
            try
            {
                var item = InventoryManager.getItem(itemEnum);
                if (item != null)
                    InventoryManager.RemoveItem(PartyKeyRing.FindInBag(item) ?? item, n);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        static void SpillOverflow(Items.itemlist itemEnum, int n)
        {
            if (itemEnum == Items.itemlist.None || n <= 0) return;
            var net = LanNetworkManager.Instance;
            if (net == null) return;
            PlaytestLog.Event("Drop", "overflow " + itemEnum + " x" + n + " -> floor");
            try { net.DroppedItemHandlers.DropOverflow(itemEnum, n); }
            catch (System.Exception ex) { ModRuntime.Log?.Warning("[Drop] overflow: " + ex.Message); }
        }

        static void ArmDropped(ItemPickup p)
        {
            int key;
            if (p == null || !DroppedItemRegistry.TryKeyOf(p, out key)) return;
            _pendingDropKey = key;
            _pendingDropItem = Items.itemlist.None;
            _pendingDropCount = 1;
            try
            {
                if (p._item != null) _pendingDropItem = p._item._item;
                _pendingDropCount = p.count > 0 ? p.count : 1;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            // The registry holds the count that was dropped; the prop's count can be rewritten natively.
            Items.itemlist regItem; int c;
            if (DroppedItemRegistry.TryGet(key, out regItem, out c))
            {
                if (_pendingDropItem == Items.itemlist.None) _pendingDropItem = regItem;
                if (c > 0) _pendingDropCount = c;
            }
        }

        /// <summary>Claim the armed drop after TakeDropped granted <c>_pendingDropAdded</c> units.</summary>
        static void FinishDroppedNative(ItemPickup p)
        {
            int key = _pendingDropKey;
            int k;
            if (p != null && DroppedItemRegistry.TryKeyOf(p, out k))
                key = k;
            if (key < 0 || !DroppedItemRegistry.TryGet(key, out _, out _)) return;

            if (!_claimedDrops.Add(key))
            {
                DroppedItemRegistry.DespawnWhenIdle(key);
                ClearPendingDrop();
                return;
            }

            var item = _pendingDropItem;
            int added = _pendingDropAdded;
            int spill = (_pendingDropCount > 0 ? _pendingDropCount : 1) - added;
            if (spill < 0) spill = 0;
            var net = LanNetworkManager.Instance;
            ModRuntime.Log?.Msg("[Drop] claim key=" + key + " " + item + " added=" + added + " spill=" + spill
                + " by=" + (net != null ? net.LocalPlayerId.ToString() : "?")
                + " host=" + NetGate.HostRole);
            if (net != null && net.IsConnected)
            {
                if (NetGate.HostRole)
                {
                    // The take already granted locally (skipLocalGrant). A refused claim gives the items back
                    // and leaves the floor item where it is.
                    string reason;
                    if (!net.DroppedItemHandlers.TryClaimDropped(key, net.LocalPlayerId, out reason, skipLocalGrant: true))
                    {
                        PlaytestLog.Event("Drop", "host claim refused key=" + key + " " + reason + " — undo x" + added);
                        RemoveAdded(item, added);
                        _claimedDrops.Remove(key);
                        ClearPendingDrop();
                        return;
                    }
                    SpillOverflow(item, spill);
                }
                else
                {
                    // WorldId carries drop key so the ack matches this claim (Int0 is also key).
                    _awaitingDrops[key] = new AwaitingDrop { Item = item, Count = added, Spill = spill };
                    net.InteractionHandlers.SendInteractionRequest(unchecked((ulong)(uint)key), InteractionKind.DroppedPickup, key);
                }
            }
            else
            {
                _awaitingDrops.Remove(key);
                SpillOverflow(item, spill);
            }
            DroppedItemRegistry.DespawnWhenIdle(key);
            ClearPendingDrop();
        }

        /// <summary>Host answer to a DroppedPickup claim. The ack always echoes the drop key (request WorldId).</summary>
        internal static void NoteDropClaimAck(bool ok, long ackWorldId)
        {
            int ackKey = (int)ackWorldId;
            AwaitingDrop pending;
            if (!_awaitingDrops.TryGetValue(ackKey, out pending)) return;
            _awaitingDrops.Remove(ackKey);
            if (ok)
            {
                // Confirmed: only now can the overflow reach the floor (a FAIL must never leave a copy).
                SpillOverflow(pending.Item, pending.Spill);
                return;
            }
            RemoveAdded(pending.Item, pending.Count);
            // Allow a later respawn/dump of the same key to be claimed again.
            _claimedDrops.Remove(ackKey);
        }
    }
}
