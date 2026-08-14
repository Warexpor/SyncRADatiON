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

            bool inspectView = false;
            try { inspectView = __instance.showItemView || __instance.focusCamera || __instance.pauseGame; }
            catch { }

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
                    PlaytestLog.Event("Pickup", "skip triggered " + __instance.gameObject.name);
                    return false;
                }
            }
            catch { }

            ulong id = WorldId.FromGameObject(__instance.gameObject);

            // 3D item inspect (photo card etc.) calls pickUp twice: open view, then take.
            // Claiming on the first call blocks the second.
            if (inspectView)
            {
                PlaytestLog.Event("Pickup", "inspect native " + __instance.gameObject.name
                    + " id=" + id.ToString("X16"));
                return true;
            }

            if (id == 0) return true;

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
            if (net.Role != NetworkRole.Host) return;
            if (Config.ModConfig.SyncWorldPickups?.Value != true) return;
            if (__instance == null) return;

            try
            {
                if (__instance.slave) return;
                if (!__instance.triggered) return;
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
