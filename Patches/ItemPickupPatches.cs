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

        internal static void NoteTakenFromCallback(ItemPickup p) => NoteTaken(p);

        internal static void NoteCraftedKey(AnItem item)
        {
            if (NetGate.IsApplying || !NetGate.Live) return;
            if (!PartyKeyRing.IsKeyOrObject(item)) return;
            PartyKeyRing.OfferToHost(item);
        }

        static bool IsInspect(ItemPickup p)
        {
            if (p == null) return false;
            try { if (p.showItemView) return true; } catch { }
            try { if (p.focusCamera) return true; } catch { }
            try { if (p.pauseGame) return true; } catch { }
            return false;
        }

        [HarmonyPrefix]
        public static bool Prefix(ItemPickup __instance)
        {
            if (__instance == null) return true;
            if (NetGate.IsApplying) return true;

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
            }
            catch { }

            if (net.PickupSync.IsClaimed(id) || net.PickupSync.IsClaimedPickup(__instance))
            {
                PlaytestLog.Event("Pickup", "skip claimed " + __instance.gameObject.name
                    + " id=" + id.ToString("X16"));
                try { net.PickupSync.HidePickup(__instance); } catch { }
                return false;
            }

            // Inspect cards: native pickUp shows yes/no. Claim only after the item is in the bag.
            if (IsInspect(__instance))
                return true;

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
            if (!triggered && !inBag && __instance != null) return;

            if (net.Role == NetworkRole.Host)
            {
                net.PickupSync.TryClaimOnHost(id, net.LocalPlayerId, out _, out _, hideNow: true,
                    hintItem: _pendingItem);
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
            }
            catch { }

            if (net.Role == NetworkRole.Host)
            {
                net.PickupSync.TryClaimOnHost(id, net.LocalPlayerId, out _, out _, hideNow: true, hintItem: item);
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
            PlaytestLog.Event("Pickup", "claim confirm id=" + id.ToString("X16") + " item=" + item);
            net.SendWorldPickupClaim(id, item, CountOf(p));
        }

        static int CountOf(ItemPickup p)
        {
            if (p == null) return 1;
            try { return p.count > 0 ? p.count : 1; } catch { return 1; }
        }
    }

    [HarmonyPatch(typeof(ItemPickup), "dialoguerCallback")]
    public static class ItemPickupConfirmPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ItemPickup __instance)
        {
            ItemPickupPatches.NoteTakenFromCallback(__instance);
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
}
