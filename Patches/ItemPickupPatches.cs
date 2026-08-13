// Host-authoritative world ItemPickup: claim → grant to claimer, hide for everyone.
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;

namespace SyncRADation.Patches
{
    [HarmonyPatch(typeof(ItemPickup), nameof(ItemPickup.pickUp))]
    public static class ItemPickupPatches
    {
        [HarmonyPrefix]
        public static bool Prefix(ItemPickup __instance)
        {
            if (__instance == null) return true;

            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected)
                return true;

            if (Config.ModConfig.SyncWorldPickups?.Value != true)
                return true;

            try
            {
                if (__instance.slave) return true; // linked props follow master
                if (__instance.triggered) return false;
            }
            catch { }

            ulong id = WorldId.FromGameObject(__instance.gameObject);
            if (id == 0) return true;

            if (net.Role == NetworkRole.Host)
            {
                // Reserve without hiding — native pickUp still needs the object alive.
                if (!net.PickupSync.TryClaimOnHost(id, net.LocalPlayerId, out _, out _, hideNow: false))
                    return false;
                return true;
            }

            // Client: request claim; host grants inventory + broadcasts hide.
            net.SendWorldPickupClaim(id);
            return false;
        }

        [HarmonyPostfix]
        public static void Postfix(ItemPickup __instance)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            if (net.Role != NetworkRole.Host) return;
            if (Config.ModConfig.SyncWorldPickups?.Value != true) return;
            if (__instance == null) return;

            try
            {
                if (__instance.slave) return;
            }
            catch { }

            ulong id = WorldId.FromGameObject(__instance.gameObject);
            if (id == 0) return;

            // After successful host pickUp, force hide for peers.
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
        }
    }
}
