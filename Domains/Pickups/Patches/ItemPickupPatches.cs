// Host-authoritative world ItemPickup: claim → grant to claimer, hide for everyone.
// The player-dropped floor item half of ItemPickupPatches is in DroppedTakePatches.cs.
using HarmonyLib;
using SyncRADation.ItemSystem;
using SyncRADation.Networking;
using SyncRADation.Sync;

namespace SyncRADation.Patches
{
    [HarmonyPatch(typeof(ItemPickup), nameof(ItemPickup.pickUp))]
    public static partial class ItemPickupPatches
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

        /// <summary>World sync is on for this pickup: connected, enabled in config, not a slave prop.</summary>
        static bool Synced(ItemPickup p, out LanNetworkManager net)
        {
            net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected || !Config.ModConfig.WorldPickupsEnabled) return false;
            try { return p == null || !p.slave; }
            catch (System.Exception e) { Guard.Swallow(e); return true; }
        }

        static ulong PropId(ItemPickup p)
        {
            ulong id = 0;
            try { if (p != null) id = WorldId.FromGameObject(p.gameObject); } catch (System.Exception e) { Guard.Swallow(e); }
            return id != 0 ? id : _pendingId;
        }

        internal static void NoteTakenFromCallback(ItemPickup p)
        {
            if (p != null && DroppedItemRegistry.IsDropped(p))
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
            var net = LanNetworkManager.Instance;
            if (p != null && net != null && net.IsConnected)
                net.PickupSync.MarkReleasePending(p, true);
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

        static bool IsInspect(ItemPickup p)
        {
            if (p == null) return false;
            try { return p.showItemView || p.focusCamera || p.pauseGame; }
            catch (System.Exception e) { Guard.Swallow(e); return false; }
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

        /// <summary>
        /// Prefix vetoed native pickUp without sending a claim. ItemPickup.Update set <c>triggered</c> before
        /// calling pickUp (Ghidra ItemPickup.c Update) and only release clears it: left set, the host's tick
        /// reads it as an ownerless claim (hidden for everyone) and Update never lets this peer try again.
        /// </summary>
        static void UntriggerVeto(ItemPickup p)
        {
            if (p == null) return;
            try
            {
                p.triggered = false;
                if (p.inter != null) p.inter.triggered = false;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        static int CountOf(ItemPickup p)
        {
            if (p == null) return 1;
            try { return p.count > 0 ? p.count : 1; } catch { return 1; }
        }

        /// <summary>
        /// Key/Object land on the party key ring even when the 6-slot bag is full; ammo/docs/tools need a free slot
        /// (or an existing stack) — same rule as drops. An unresolved enum does not gate (may still be Key/Object).
        /// </summary>
        static bool BagFullFor(Items.itemlist kind)
        {
            if (kind == Items.itemlist.None || PartyKeyRing.IsKeyOrObject(kind)) return false;
            return !ItemBag.BagHasRoom(kind);
        }

        [HarmonyPrefix]
        public static bool Prefix(ItemPickup __instance)
        {
            if (__instance == null) return true;
            BindPickupName(__instance);

            // Floor items: the claim runs on the confirmed answer (TakeDropped), never here.
            if (DroppedItemRegistry.IsDropped(__instance)) return true;
            if (NetGate.IsApplying) return true;

            // Death tarot MeatBlocker seals the NG+ KeyOfSacrifice wing (wiki softlock).
            // Hold take while that key is still a live unclaimed world unique.
            var netHold = LanNetworkManager.Instance;
            if (netHold != null && netHold.IsConnected
                && netHold.PickupSync.HoldTarotDeathForSacrifice(WorldPickupSyncService.ResolveItem(__instance)))
            {
                PlaytestLog.Event("Pickup", "hold TarotDeath until KeyOfSacrifice");
                UntriggerVeto(__instance);
                return false;
            }

            LanNetworkManager net;
            if (!Synced(__instance, out net)) return true;

            ulong id = WorldId.FromGameObject(__instance.gameObject);
            if (id == 0) return true;
            _pendingId = id;
            // BindPickupName already put the catalog item on _item.
            _pendingItem = WorldPickupSyncService.ResolveItem(__instance);
            var sync = net.PickupSync;

            if (sync.IsClaimed(id) || sync.IsClaimedPickup(__instance))
            {
                PlaytestLog.Verbose("Pickup", "skip claimed " + __instance.gameObject.name + " id=" + id.ToString("X16"));
                sync.HidePickup(__instance);
                return false;
            }

            // Inspect cards: native pickUp shows yes/no. Claim only after the item is in the bag.
            if (IsInspect(__instance))
            {
                sync.MarkReleasePending(__instance, false);
                return true;
            }

            if (BagFullFor(_pendingItem))
            {
                // Host: let native pickUp show _nospaceDialogue (no reservation).
                // Client: block native (would dual-grant) and skip claim wire.
                PlaytestLog.Event("Pickup", "deny bag full " + __instance.gameObject.name + " item=" + _pendingItem);
                if (!NetGate.HostRole)
                {
                    UntriggerVeto(__instance);
                    return false;
                }
                sync.MarkReleasePending(__instance, false);
                return true;
            }

            if (NetGate.HostRole)
            {
                // Reserve only: the claim is published in the Postfix once native pickUp really took it.
                if (!sync.TryClaimOnHost(id, net.LocalPlayerId, out _, out _, hideNow: false, hintItem: _pendingItem))
                {
                    PlaytestLog.Event("Pickup", "host deny " + __instance.gameObject.name + " id=" + id.ToString("X16"));
                    sync.HidePickup(__instance);
                    return false;
                }
                PlaytestLog.Event("Pickup", "host take " + __instance.gameObject.name + " id=" + id.ToString("X16"));
                sync.MarkReleasePending(__instance, false);
                return true;
            }

            PlaytestLog.Event("Pickup", "claim " + __instance.gameObject.name + " id=" + id.ToString("X16"));
            net.WorldPickupHandlers.SendWorldPickupClaim(id, _pendingItem, CountOf(__instance));
            return false;
        }

        [HarmonyPostfix]
        public static void Postfix(ItemPickup __instance, bool __runOriginal)
        {
            if (!__runOriginal) return;
            if (__instance != null && DroppedItemRegistry.IsDropped(__instance)) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected || !Config.ModConfig.WorldPickupsEnabled) return;
            var sync = net.PickupSync;

            // pickUp ran but opened no dialogue (refused / aborted): no release is coming, so the
            // release wait (and any hide held back behind it) must not linger.
            if (__instance != null && !WorldPickupSyncService.DialogueOpenNow())
                sync.ClearReleasePending(__instance);

            if (!Synced(__instance, out net)) return;
            ulong id = PropId(__instance);
            if (id == 0) return;

            bool inBag = false;
            try
            {
                inBag = __instance._item != null && InventoryManager.hasItem(__instance._item);
                if (!inBag && _pendingItem != Items.itemlist.None)
                    inBag = InventoryManager.hasItem(_pendingItem);
            }
            catch (System.Exception e) { Guard.Swallow(e); }

            bool inspect = IsInspect(__instance);
            // Inspect pickups claim only on the yes answer (NoteTaken), host included: a claim here
            // would hide the prop for everyone while the host's yes/no is still open.
            if (inspect && __instance != null && (!inBag || NetGate.HostRole)) return;

            bool triggered = false;
            try { triggered = __instance != null && __instance.triggered; } catch (System.Exception e) { Guard.Swallow(e); }

            if (NetGate.HostRole)
            {
                // Native refused (nospace / cancel) after Prefix reserved — free the WorldId.
                if (!triggered && !inBag)
                {
                    sync.ReleaseClaimIf(id, net.LocalPlayerId);
                    return;
                }
                if (!sync.HostClaim(id, net.LocalPlayerId, _pendingItem, 0, hostNative: true))
                    PlaytestLog.Event("Pickup", "host post deny id=" + id.ToString("X16"));
                return;
            }

            if (!inBag && __instance != null) return;
            // Inspect yes/no is still open here (native release runs after the answer). Claim on
            // confirm (NoteTaken) so declining never hides the prop for everyone.
            if (inspect || sync.IsClaimed(id)) return;
            if (BagFullFor(_pendingItem))
            {
                PlaytestLog.Event("Pickup", "deny bag full after inspect item=" + _pendingItem);
                return;
            }
            PlaytestLog.Event("Pickup", "claim after inspect " + (__instance != null ? __instance.gameObject.name : "gone")
                + " id=" + id.ToString("X16"));
            int count = CountOf(__instance);
            sync.ExpectNativeAdd(id, __instance, _pendingItem, count, awaitVerdict: true);
            net.WorldPickupHandlers.SendWorldPickupClaim(id, _pendingItem, count);
        }

        /// <summary>The yes/no of a world pickup was answered (dialoguerCallback): claim on "yes", give back on "no".</summary>
        static void NoteTaken(ItemPickup p)
        {
            if (p == null && _pendingId == 0) return;
            LanNetworkManager net;
            if (!Synced(p, out net)) return;
            ulong id = PropId(p);
            if (id == 0) return;

            var item = _pendingItem;
            try
            {
                if (p != null && p._item != null) item = p._item._item;
                if (item == Items.itemlist.None) item = WorldPickupSyncService.ResolveItem(p);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            var sync = net.PickupSync;

            // Native release only adds when the yes/no answer (Dialoguer global bool 1) was yes
            // (ItemPickup.release, Ghidra ItemPickup.c: GetGlobalBoolean(1) gate before AddItemToMax).
            if (!AnsweredYes())
            {
                PlaytestLog.Verbose("Pickup", "declined id=" + id.ToString("X16"));
                // Host pre-claimed in Prefix/Postfix before the answer: a "no" gives the prop back.
                if (NetGate.HostRole)
                    sync.ReleaseAndBroadcast(id, net.LocalPlayerId);
                return;
            }
            int takeCount = CountOf(p);

            if (NetGate.HostRole)
            {
                if (!sync.HostClaim(id, net.LocalPlayerId, item, 0, hostNative: true))
                {
                    PlaytestLog.Event("Pickup", "host note deny id=" + id.ToString("X16"));
                    // Another peer claimed while our yes/no was open; native release still adds.
                    sync.RevertNativeGrantNow(id, p, item, takeCount);
                    return;
                }
                // Native release (0.1 s away) does the add: measure it so a partial AddItemToMax gives the rest back.
                sync.ExpectNativeAdd(id, p, item, takeCount, awaitVerdict: false);
                return;
            }

            if (sync.IsClaimed(id))
            {
                // Claimed by another peer while our yes/no was open; native release still adds.
                PlaytestLog.Event("Pickup", "confirm lost id=" + id.ToString("X16") + " item=" + item);
                sync.RevertNativeGrantNow(id, p, item, takeCount);
                return;
            }
            if (BagFullFor(item) && !WorldPickupSyncService.WouldMagFill(item))
            {
                PlaytestLog.Event("Pickup", "deny bag full confirm item=" + item);
                return;
            }
            PlaytestLog.Event("Pickup", "claim confirm id=" + id.ToString("X16") + " item=" + item);
            sync.ExpectNativeAdd(id, p, item, takeCount, awaitVerdict: true);
            net.WorldPickupHandlers.SendWorldPickupClaim(id, item, takeCount);
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
            DroppedItemRegistry.TickDeferred();
        }
    }

    [HarmonyPatch(typeof(ItemPickup), "release")]
    public static class ItemPickupReleasePatch
    {
        /// <summary>
        /// gameState when release began. pickUp parks gameState at 6 and release puts prevState back (Ghidra
        /// ItemPickup.c). A cutscene that started in between (the party key ring makes hasItem true before release:
        /// PEN_CodeRoomEnd fires on the King in Yellow book) set it to cutscene, and release reverted that to the event
        /// screen: no skip bar, and Esc opened the pause menu instead of holding to skip.
        /// </summary>
        // persistent: one release call's prefix -> postfix handoff
        static PlayerState.gameStates _stateAtRelease;

        [HarmonyPrefix]
        public static bool Prefix(ItemPickup __instance)
        {
            try { _stateAtRelease = PlayerState.gameState; } catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                if (__instance == null) return true;
                if (!DroppedItemRegistry.IsDropped(__instance))
                {
                    // Release may destroy the prop: keep a template so this player's later drop of it clones right.
                    DroppedItemTemplateCache.Stash(__instance);
                    return true;
                }
                // "no": native release skips the add and just restores play state.
                if (!ItemPickupPatches.AnsweredYes())
                {
                    ItemPickupPatches.ClearPendingDrop();
                    return true;
                }
                ItemPickupPatches.TakeDropped(__instance);
                // Native release is skipped: its play-state part (clears the cutscene flag pickUp set) still runs.
                DroppedItemRegistry.ReleaseHead(__instance);
                return false;
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[Drop] release: " + ex.Message);
                return true;
            }
        }

        [HarmonyPostfix]
        public static void Postfix(ItemPickup __instance)
        {
            try
            {
                if (_stateAtRelease == PlayerState.gameStates.cutscene
                    && PlayerState.gameState != PlayerState.gameStates.cutscene)
                    PlayerState.gameState = PlayerState.gameStates.cutscene;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                var net = LanNetworkManager.Instance;
                if (__instance == null || net == null || !net.IsConnected) return;
                if (DroppedItemRegistry.IsDropped(__instance)) return;
                // Native release ran: settle the gain (a partial take gives the rest back) and run the held hide.
                net.PickupSync.OnNativeRelease(__instance);
            }
            catch (System.Exception ex) { ModRuntime.Log?.Warning("[Pickup] release post: " + ex.Message); }
        }
    }
}
