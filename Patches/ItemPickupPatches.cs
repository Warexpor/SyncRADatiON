// Host-authoritative world ItemPickup: claim → grant to claimer, hide for everyone.
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;

namespace SyncRADation.Patches
{
    [HarmonyPatch(typeof(ItemPickup), nameof(ItemPickup.pickUp))]
    public static class ItemPickupPatches
    {
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
                if (__instance.triggered)
                {
                    ulong stuckId = WorldId.FromGameObject(__instance.gameObject);
                    if (stuckId != 0 && net.PickupSync.IsClaimed(stuckId))
                    {
                        PlaytestLog.Event("Pickup", "skip triggered " + __instance.gameObject.name);
                        return false;
                    }
                    try { __instance.triggered = false; } catch { }
                    PlaytestLog.Event("Pickup", "unstick " + __instance.gameObject.name
                        + " id=" + stuckId.ToString("X16"));
                }
            }
            catch { }

            ulong id = WorldId.FromGameObject(__instance.gameObject);
            if (id == 0) return true;

            if (net.PickupSync.IsClaimed(id))
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
                if (!net.PickupSync.TryClaimOnHost(id, net.LocalPlayerId, out _, out _, hideNow: false))
                {
                    PlaytestLog.Event("Pickup", "host deny " + __instance.gameObject.name
                        + " id=" + id.ToString("X16"));
                    return false;
                }
                PlaytestLog.Event("Pickup", "host take " + __instance.gameObject.name
                    + " id=" + id.ToString("X16"));
                return true;
            }

            PlaytestLog.Event("Pickup", "claim " + __instance.gameObject.name + " id=" + id.ToString("X16"));
            net.SendWorldPickupClaim(id);
            return false;
        }

        [HarmonyPostfix]
        public static void Postfix(ItemPickup __instance, bool __runOriginal)
        {
            if (!__runOriginal) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            if (Config.ModConfig.SyncWorldPickups?.Value != true) return;
            if (__instance == null) return;

            try { if (__instance.slave) return; } catch { }

            ulong id = WorldId.FromGameObject(__instance.gameObject);
            if (id == 0) return;

            bool inBag = false;
            try { inBag = __instance._item != null && InventoryManager.hasItem(__instance._item); }
            catch { }

            if (IsInspect(__instance) && !inBag)
                return;

            try
            {
                if (!__instance.triggered && !inBag) return;
            }
            catch { }

            if (net.Role == NetworkRole.Host)
            {
                net.PickupSync.TryClaimOnHost(id, net.LocalPlayerId, out _, out _, hideNow: true);
                net.PickupSync.BroadcastTriggered(id, true);
                try
                {
                    if (__instance._item != null)
                    {
                        PartyKeyRing.Note(__instance._item);
                        PartyKeyRing.Broadcast();
                    }
                }
                catch { }
                return;
            }

            if (!inBag) return;
            if (net.PickupSync.IsClaimed(id)) return;
            PlaytestLog.Event("Pickup", "claim after inspect " + __instance.gameObject.name
                + " id=" + id.ToString("X16"));
            net.SendWorldPickupClaim(id);
        }
    }
}
