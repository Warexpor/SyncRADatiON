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

    [HarmonyPatch(typeof(CryoDoorController), nameof(CryoDoorController.toggleDoors))]
    public static class CryoDoorTogglePatch
    {
        [HarmonyPostfix]
        public static void Postfix(CryoDoorController __instance)
        {
            if (__instance == null || NetGate.IsApplying) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            ulong id = WorldId.FromGameObject(__instance.gameObject);
            net.PuzzleSync.Emit(PuzzleType.CryoDoorController, id, __instance);
        }
    }

    [HarmonyPatch(typeof(CryoDoorLock), "solved")]
    public static class CryoDoorLockSolvedPatch
    {
        [HarmonyPostfix]
        public static void Postfix(CryoDoorLock __instance)
        {
            if (__instance == null || NetGate.IsApplying) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            ulong id = WorldId.FromGameObject(__instance.gameObject);
            net.PuzzleSync.EmitProgressed(PuzzleType.CryoDoorLock, id);
            try
            {
                if (__instance.puzzle != null)
                {
                    ulong pid = WorldId.FromGameObject(__instance.puzzle.gameObject);
                    net.PuzzleSync.EmitProgressed(PuzzleType.PEN_Codepad, pid);
                }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(PEN_Cryo), nameof(PEN_Cryo.Open))]
    public static class PenCryoOpenPatch
    {
        [HarmonyPostfix]
        public static void Postfix(PEN_Cryo __instance)
        {
            if (__instance == null || NetGate.IsApplying) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            ulong id = WorldId.FromGameObject(__instance.gameObject);
            net.PuzzleSync.EmitProgressed(PuzzleType.PEN_Cryo, id);
            try
            {
                var cryoLock = __instance.GetComponentInParent<CryoDoorLock>()
                    ?? __instance.GetComponentInChildren<CryoDoorLock>(true);
                if (cryoLock != null)
                {
                    ulong lid = WorldId.FromGameObject(cryoLock.gameObject);
                    if (lid != 0)
                        net.PuzzleSync.EmitProgressed(PuzzleType.CryoDoorLock, lid);
                    if (cryoLock.puzzle != null)
                    {
                        ulong pid = WorldId.FromGameObject(cryoLock.puzzle.gameObject);
                        if (pid != 0)
                            net.PuzzleSync.EmitProgressed(PuzzleType.PEN_Codepad, pid);
                    }
                }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(CryoDoorLock), "OnEnable")]
    public static class CryoLockEnablePatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            net.PuzzleSync.QueueReapply();
        }
    }

    [HarmonyPatch(typeof(PEN_Cryo), "OnEnable")]
    public static class PenCryoEnablePatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            net.PuzzleSync.QueueReapply();
        }
    }

    [HarmonyPatch(typeof(Room), nameof(Room.EnterRoom))]
    public static class RoomEnterPuzzlePatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            if (NetGate.IsApplying) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            net.PuzzleSync.QueueReapply();
        }
    }

    [HarmonyPatch(typeof(Room), nameof(Room.SetChunkStatus))]
    public static class RoomChunkPuzzlePatch
    {
        [HarmonyPostfix]
        public static void Postfix(bool value)
        {
            if (!value || NetGate.IsApplying) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            net.PuzzleSync.QueueReapply();
        }
    }
}
