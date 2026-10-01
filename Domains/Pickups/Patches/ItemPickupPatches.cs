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

        /// <summary>SessionReset: the in-flight world-pickup claim and the armed floor take belong to the session that just ended.</summary>
        internal static void ResetSession()
        {
            _pendingId = 0;
            _pendingItem = Items.itemlist.None;
            ClearPendingDrop();
        }

        /// <summary>Forget the armed floor item (declined / session end / take finished).</summary>
        internal static void ClearPendingDrop()
        {
            _pendingDropKey = -1;
            _pendingDropItem = Items.itemlist.None;
            _pendingDropCount = 0;
            _pendingDropAdded = 0;
        }

        internal static void NoteTakenFromCallback(ItemPickup p)
        {
            if (p != null && ItemSystem.DroppedItemManager.IsDropped(p))
            {
                // Native release only adds on a yes answer (Dialoguer global bool 1); a "no" must not
                // grant or claim the floor item (release Prefix then lets native release run).
                if (!AnsweredYes())
                {
                    PlaytestLog.Verbose("Drop", "take declined");
                    ClearPendingDrop();
                    return;
                }
                TakeDropped(p);
                return;
            }
            // Native release is now ~0.1 s away: keep the prop active until it has run.
            try
            {
                var net = LanNetworkManager.Instance;
                if (p != null && net != null && net.IsConnected)
                    net.PickupSync.MarkReleasePending(p, true);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            NoteTaken(p);
        }

        internal static bool AnsweredYes()
        {
            try { return Dialoguer.GetGlobalBoolean(1); }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[Pickup] yes/no answer unreadable: " + ex.Message);
                return true;
            }
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
            if (key < 0 || !ItemSystem.DroppedItemManager.TryGet(key, out _, out _))
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
                    try { ItemSystem.DroppedItemManager.RestorePlay(); } catch (System.Exception e) { Guard.Swallow(e); }
                    return true;
                }
                _pendingDropAdded = added;
            }
            FinishDroppedNative(p);
            try { ItemSystem.DroppedItemManager.RestorePlay(); } catch (System.Exception e) { Guard.Swallow(e); }
            return true;
        }

        /// <summary>AddItem the armed stack and return how many units the bag really took (AddItem caps at maxNumber).</summary>
        static int GrantDropped()
        {
            var id = _pendingDropItem;
            if (id == Items.itemlist.None) return 0;
            int want = _pendingDropCount > 0 ? _pendingDropCount : 1;
            int before = ItemSystem.DroppedItemManager.CountInBag(id);
            // Native AddItem ignores maxSlots: a new stack needs a free slot (native pickUp shows _nospaceDialogue).
            if (before <= 0 && !ItemSystem.DroppedItemManager.BagHasRoom(id)) return 0;
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
            int added = ItemSystem.DroppedItemManager.CountInBag(id) - before;
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
            try { net.DropOverflow(itemEnum, n); }
            catch (System.Exception ex) { ModRuntime.Log?.Warning("[Drop] overflow: " + ex.Message); }
        }

        static void ArmDropped(ItemPickup p)
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
            catch (System.Exception e) { Guard.Swallow(e); }
            // The registry holds the count that was dropped; the prop's count can be rewritten natively.
            if (_pendingDropKey >= 0)
            {
                Items.itemlist regItem; int c;
                if (ItemSystem.DroppedItemManager.TryGet(_pendingDropKey, out regItem, out c))
                {
                    if (_pendingDropItem == Items.itemlist.None) _pendingDropItem = regItem;
                    if (c > 0) _pendingDropCount = c;
                }
            }
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
            try { if (p.showItemView) return true; } catch (System.Exception e) { Guard.Swallow(e); }
            try { if (p.focusCamera) return true; } catch (System.Exception e) { Guard.Swallow(e); }
            try { if (p.pauseGame) return true; } catch (System.Exception e) { Guard.Swallow(e); }
            return false;
        }

        static void BindPickupName(ItemPickup p)
        {
            if (p == null || !NetGate.Live) return;
            try
            {
                var kind = WorldPickupSyncService.ResolveItem(p);
                if (kind == Items.itemlist.None) return;
                var cat = InventoryManager.getItem(kind);
                if (cat == null) return;
                p._item = cat;
                PartyKeyRing.BindUseDialogue(cat);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        static int _pendingDropKey = -1;
        static Items.itemlist _pendingDropItem;
        static int _pendingDropCount;
        // Units the confirmed take really added to the bag (what a failed claim takes back).
        static int _pendingDropAdded;

        /// <summary>
        /// Prefix vetoed native pickUp without sending a claim. ItemPickup.Update set <c>triggered</c> before
        /// calling pickUp (Ghidra ItemPickup.c Update) and only release clears it: left set, the host's TickHost
        /// reads it as an ownerless claim (hidden for everyone) and Update never lets this peer try again.
        /// </summary>
        static void UntriggerVeto(ItemPickup p)
        {
            if (p == null) return;
            try { p.triggered = false; } catch (System.Exception e) { Guard.Swallow(e); }
            try { if (p.inter != null) p.inter.triggered = false; } catch (System.Exception e) { Guard.Swallow(e); }
        }

        [HarmonyPrefix]
        public static bool Prefix(ItemPickup __instance)
        {
            if (__instance == null) return true;
            BindPickupName(__instance);

            // Floor items: the claim runs on the confirmed answer (TakeDropped), never here.
            if (ItemSystem.DroppedItemManager.IsDropped(__instance))
                return true;

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
                    UntriggerVeto(__instance);
                    return false;
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }

            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected)
                return true;

            if (Config.ModConfig.SyncWorldPickups?.Value != true)
                return true;

            try
            {
                if (__instance.slave) return true;
            }
            catch (System.Exception e) { Guard.Swallow(e); }

            ulong id = WorldId.FromGameObject(__instance.gameObject);
            if (id == 0) return true;
            _pendingId = id;
            try
            {
                if (__instance._item != null)
                    _pendingItem = __instance._item._item;
                if (_pendingItem == Items.itemlist.None)
                    _pendingItem = WorldPickupSyncService.ResolveItem(__instance);
            }
            catch (System.Exception e) { Guard.Swallow(e); }

            if (net.PickupSync.IsClaimed(id) || net.PickupSync.IsClaimedPickup(__instance))
            {
                PlaytestLog.Verbose("Pickup", "skip claimed " + __instance.gameObject.name
                    + " id=" + id.ToString("X16"));
                try { net.PickupSync.HidePickup(__instance); } catch (System.Exception e) { Guard.Swallow(e); }
                return false;
            }

            // Inspect cards: native pickUp shows yes/no. Claim only after the item is in the bag.
            if (IsInspect(__instance))
            {
                net.PickupSync.MarkReleasePending(__instance, false);
                return true;
            }

            if (WorldClaimNeedsBagRoom(_pendingItem) && !ItemSystem.DroppedItemManager.BagHasRoom(_pendingItem))
            {
                // Host: let native pickUp show _nospaceDialogue (no reservation).
                // Client: block native (would dual-grant) and skip claim wire.
                PlaytestLog.Event("Pickup", "deny bag full " + __instance.gameObject.name
                    + " item=" + _pendingItem);
                if (net.Role != NetworkRole.Host)
                {
                    UntriggerVeto(__instance);
                    return false;
                }
                net.PickupSync.MarkReleasePending(__instance, false);
                return true;
            }

            if (net.Role == NetworkRole.Host)
            {
                if (!net.PickupSync.TryClaimOnHost(id, net.LocalPlayerId, out _, out _,
                    hideNow: false, hintItem: _pendingItem))
                {
                    PlaytestLog.Event("Pickup", "host deny " + __instance.gameObject.name
                        + " id=" + id.ToString("X16"));
                    try { net.PickupSync.HidePickup(__instance); } catch (System.Exception e) { Guard.Swallow(e); }
                    return false;
                }
                PlaytestLog.Event("Pickup", "host take " + __instance.gameObject.name
                    + " id=" + id.ToString("X16"));
                net.PickupSync.MarkReleasePending(__instance, false);
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
            if (__instance != null && ItemSystem.DroppedItemManager.IsDropped(__instance)) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            if (Config.ModConfig.SyncWorldPickups?.Value != true) return;

            // pickUp ran but opened no dialogue (refused / aborted): no release is coming, so the
            // release-pending mark (and any hide held back behind it) must not linger.
            try
            {
                if (__instance != null && !WorldPickupSyncService.DialogueOpenNow())
                    net.PickupSync.ClearReleasePending(__instance);
            }
            catch (System.Exception e) { Guard.Swallow(e); }

            try { if (__instance != null && __instance.slave) return; } catch (System.Exception e) { Guard.Swallow(e); }

            ulong id = 0;
            try { id = WorldId.FromGameObject(__instance.gameObject); } catch (System.Exception e) { Guard.Swallow(e); }
            if (id == 0) id = _pendingId;
            if (id == 0) return;

            bool inBag = false;
            try { inBag = __instance._item != null && InventoryManager.hasItem(__instance._item); }
            catch (System.Exception e) { Guard.Swallow(e); }
            if (!inBag && _pendingItem != Items.itemlist.None)
            {
                try { inBag = InventoryManager.hasItem(_pendingItem); } catch (System.Exception e) { Guard.Swallow(e); }
            }

            if (IsInspect(__instance))
            {
                bool gone = false;
                try { gone = __instance == null; } catch { gone = true; }
                // Inspect pickups claim only on the yes answer (NoteTaken), host included: a claim here
                // would hide the prop for everyone while the host's yes/no is still open.
                if (!gone && (!inBag || net.Role == NetworkRole.Host)) return;
            }

            bool triggered = false;
            try { triggered = __instance != null && __instance.triggered; } catch (System.Exception e) { Guard.Swallow(e); }
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
                catch (System.Exception e) { Guard.Swallow(e); }
                return;
            }

            if (!inBag && __instance != null) return;
            // Inspect yes/no is still open here (native release runs after the answer). Claim on
            // confirm (NoteTaken) so declining never hides the prop for everyone.
            if (__instance != null && IsInspect(__instance)) return;
            if (net.PickupSync.IsClaimed(id)) return;
            if (WorldClaimNeedsBagRoom(_pendingItem) && !ItemSystem.DroppedItemManager.BagHasRoom(_pendingItem))
            {
                PlaytestLog.Event("Pickup", "deny bag full after inspect item=" + _pendingItem);
                return;
            }
            PlaytestLog.Event("Pickup", "claim after inspect " + (__instance != null ? __instance.gameObject.name : "gone")
                + " id=" + id.ToString("X16"));
            net.PickupSync.NoteNativeGrantExpected(id, _pendingItem, CountOf(__instance));
            net.SendWorldPickupClaim(id, _pendingItem, CountOf(__instance));
        }

        static void NoteTaken(ItemPickup p)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            if (Config.ModConfig.SyncWorldPickups?.Value != true) return;
            if (p == null && _pendingId == 0) return;
            try { if (p != null && p.slave) return; } catch (System.Exception e) { Guard.Swallow(e); }

            ulong id = 0;
            try { if (p != null) id = WorldId.FromGameObject(p.gameObject); } catch (System.Exception e) { Guard.Swallow(e); }
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
            catch (System.Exception e) { Guard.Swallow(e); }

            // Native release only adds when the yes/no answer (Dialoguer global bool 1) was yes
            // (ItemPickup.release, Ghidra ItemPickup.c: GetGlobalBoolean(1) gate before AddItemToMax).
            if (!AnsweredYes())
            {
                PlaytestLog.Verbose("Pickup", "declined id=" + id.ToString("X16"));
                // Host pre-claimed in Prefix/Postfix before the answer: a "no" gives the prop back.
                if (net.Role == NetworkRole.Host)
                    net.PickupSync.ReleaseAndBroadcast(id, net.LocalPlayerId);
                return;
            }
            int takeCount = CountOf(p);

            if (net.Role == NetworkRole.Host)
            {
                if (!net.PickupSync.TryClaimOnHost(id, net.LocalPlayerId, out _, out _, hideNow: true, hintItem: item))
                {
                    PlaytestLog.Event("Pickup", "host note deny id=" + id.ToString("X16"));
                    // Another peer claimed while our yes/no was open; native release still adds.
                    net.PickupSync.RevertNativeGrantNow(item, takeCount, p);
                    return;
                }
                // Native release (0.1 s away) does the add: measure it so a partial AddItemToMax
                // (remainder left on the prop natively) is settled in CheckGain.
                net.PickupSync.ExpectGain(id, item, takeCount);
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
                catch (System.Exception e) { Guard.Swallow(e); }
                return;
            }

            if (net.PickupSync.IsClaimed(id))
            {
                // Claimed by another peer while our yes/no was open; native release still adds.
                PlaytestLog.Event("Pickup", "confirm lost id=" + id.ToString("X16") + " item=" + item);
                net.PickupSync.RevertNativeGrantNow(item, takeCount, p);
                return;
            }
            if (WorldClaimNeedsBagRoom(item) && !ItemSystem.DroppedItemManager.BagHasRoom(item)
                && !WorldPickupSyncService.WouldMagFill(item))
            {
                PlaytestLog.Event("Pickup", "deny bag full confirm item=" + item);
                return;
            }
            PlaytestLog.Event("Pickup", "claim confirm id=" + id.ToString("X16") + " item=" + item);
            net.PickupSync.NoteNativeGrantExpected(id, item, takeCount);
            net.SendWorldPickupClaim(id, item, takeCount);
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
        static readonly System.Collections.Generic.List<int> _purgeScratch = new System.Collections.Generic.List<int>(4);

        static readonly System.Collections.Generic.HashSet<int> _claimedDrops
            = new System.Collections.Generic.HashSet<int>();

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
            ClearPendingDrop();
        }

        /// <summary>Claim the armed drop after TakeDropped granted <c>_pendingDropAdded</c> units.</summary>
        static void FinishDroppedNative(ItemPickup p)
        {
            int key = _pendingDropKey;
            if (p != null)
            {
                int k;
                if (ItemSystem.DroppedItemManager.TryKeyOf(p, out k))
                    key = k;
            }
            if (key < 0) return;
            if (!ItemSystem.DroppedItemManager.TryGet(key, out _, out _)) return;

            if (!_claimedDrops.Add(key))
            {
                ItemSystem.DroppedItemManager.DespawnWhenIdle(key);
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
                + " host=" + (net != null && net.Role == NetworkRole.Host));
            if (net != null && net.IsConnected)
            {
                if (net.Role == NetworkRole.Host)
                {
                    // The take already granted locally (skipLocalGrant). A refused claim gives the items back
                    // and leaves the floor item where it is.
                    string reason;
                    if (!net.TryClaimDropped(key, net.LocalPlayerId, out reason, skipLocalGrant: true))
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
                    net.SendInteractionRequest(unchecked((ulong)(uint)key), InteractionKind.DroppedPickup, key);
                }
            }
            else
            {
                _awaitingDrops.Remove(key);
                SpillOverflow(item, spill);
            }
            ItemSystem.DroppedItemManager.DespawnWhenIdle(key);
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

    [HarmonyPatch(typeof(ItemPickup), "dialoguerCallback")]
    public static class ItemPickupConfirmPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ItemPickup __instance)
        {
            try { ItemPickupPatches.NoteTakenFromCallback(__instance); }
            catch (System.Exception ex) { ModRuntime.Log?.Warning("[Drop] callback: " + ex.Message); }
            try { ItemSystem.DroppedItemManager.TickDeferred(); } catch (System.Exception e) { Guard.Swallow(e); }
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
                    // "no": native release skips the add and just restores play state.
                    if (!ItemPickupPatches.AnsweredYes())
                    {
                        ItemPickupPatches.ClearPendingDrop();
                        return true;
                    }
                    ItemPickupPatches.TakeDropped(__instance);
                    return false;
                }
            }
            catch (System.Exception ex) { ModRuntime.Log?.Warning("[Drop] release: " + ex.Message); }
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(ItemPickup __instance)
        {
            try
            {
                var net = LanNetworkManager.Instance;
                if (__instance == null || net == null || !net.IsConnected) return;
                if (ItemSystem.DroppedItemManager.IsDropped(__instance)) return;
                // Native release ran: settle the gain and run the held-back hide.
                net.PickupSync.OnNativeRelease(__instance);
            }
            catch (System.Exception ex) { ModRuntime.Log?.Warning("[Pickup] release post: " + ex.Message); }
        }
    }

    [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.AddItem), typeof(AnItem), typeof(int))]
    public static class InventoryAddItemCountPatch
    {
        [HarmonyPostfix]
        public static void Postfix(AnItem item)
        {
            ItemPickupPatches.NoteCraftedKey(item);
        }
    }

    [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.AddItem), typeof(AnItem))]
    public static class InventoryAddItemPatch
    {
        [HarmonyPostfix]
        public static void Postfix(AnItem item)
        {
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
