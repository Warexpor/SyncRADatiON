// Edge-triggered door visuals. Polling at 0.3s misses a client walking through.
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Patches
{
    [HarmonyPatch(typeof(Doorway_Double), "openDoors")]
    public static class DoubleDoorOpenPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Doorway_Double __instance)
        {
            DoorSyncService.NotifyDoubleDoor(__instance, true);
        }
    }

    [HarmonyPatch(typeof(Doorway_Double), "closeDoors")]
    public static class DoubleDoorClosePatch
    {
        [HarmonyPostfix]
        public static void Postfix(Doorway_Double __instance)
        {
            DoorSyncService.NotifyDoubleDoor(__instance, false);
        }
    }

    [HarmonyPatch(typeof(EventSlidingDoor), "cycle")]
    public static class SlidingDoorCyclePatch
    {
        [HarmonyPostfix]
        public static void Postfix(EventSlidingDoor __instance)
        {
            DoorSyncService.NotifySlidingDoor(__instance);
        }
    }
}
