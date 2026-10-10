// A player-dropped floor item's ItemPickup.Awake, without the UniqueId the drop clone strips.
using HarmonyLib;
using SyncRADation.ItemSystem;
using SyncRADation.Sync;

namespace SyncRADation.Patches
{
    /// <summary>
    /// Native Awake (Ghidra ItemPickup.c) reads GetComponent&lt;UniqueId&gt;().slot for its SProgress "exists: " /
    /// "count: " keys with no null check. A drop clone loses its UniqueId (StripUniqueId: the copied guid would steal
    /// the original prop's SProgress state), so every drop threw a NullReferenceException on every peer as it woke
    /// (pilot soak, RES_Residential: a downed client's ShotgunAmmo) and never got past the throw. For a drop, run the
    /// part that does not need the id: a drop has no saved state, only the Interaction binding (slave false).
    /// </summary>
    [HarmonyPatch(typeof(ItemPickup), "Awake")]
    public static class DroppedItemPickupAwakePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(ItemPickup __instance)
        {
            try
            {
                if (__instance == null || !DroppedItemRegistry.IsDropped(__instance)) return true;
                if (__instance.GetComponent<UniqueId>() != null) return true;
                if (!__instance.slave) __instance.inter = __instance.GetComponent<Interaction>();
                return false;
            }
            catch (System.Exception e)
            {
                Guard.Swallow(e);
                return true;
            }
        }
    }
}
