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
        [HarmonyPrefix]
        public static bool Prefix(Doorway_Double __instance, ref bool __state)
        {
            __state = true;
            if (__instance == null || NetGate.IsApplying) return true;
            try
            {
                if (!__instance.locked) return true;
            }
            catch { return true; }
            __state = false;
            PlaytestLog.Event("Door", "block locked open " + __instance.gameObject.name);
            return false;
        }

        [HarmonyPostfix]
        public static void Postfix(Doorway_Double __instance, bool __state)
        {
            if (!__state) return;
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
            catch (System.Exception e) { Guard.Swallow(e); }
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
                CryoDoorLock cryoLock = null;
                try { cryoLock = __instance.GetComponentInChildren<CryoDoorLock>(true); } catch (System.Exception e) { Guard.Swallow(e); }
                if (cryoLock == null)
                {
                    var t = __instance.transform;
                    while (t != null)
                    {
                        try { cryoLock = t.GetComponent<CryoDoorLock>(); } catch (System.Exception e) { Guard.Swallow(e); }
                        if (cryoLock != null) break;
                        t = t.parent;
                    }
                }
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
            catch (System.Exception e) { Guard.Swallow(e); }
        }
    }

    [HarmonyPatch(typeof(CryoDoorLock), "OnEnable")]
    public static class CryoLockEnablePatch
    {
        [HarmonyPostfix]
        public static void Postfix(CryoDoorLock __instance)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            net.PuzzleSync.HandleCryoLockEnabled(__instance);
            net.PuzzleSync.QueueReapply();
        }
    }

    [HarmonyPatch(typeof(PEN_Cryo), "OnEnable")]
    public static class PenCryoEnablePatch
    {
        [HarmonyPostfix]
        public static void Postfix(PEN_Cryo __instance)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            net.PuzzleSync.HandlePenCryoEnabled(__instance);
            try { net.PickupSync.HideClaimed(null); } catch (System.Exception e) { Guard.Swallow(e); }
            net.PuzzleSync.QueueReapply();
        }
    }

    [HarmonyPatch(typeof(PEN_Codepad), "OnEnable")]
    public static class PenCodepadEnablePatch
    {
        [HarmonyPostfix]
        public static void Postfix(PEN_Codepad __instance)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            net.PuzzleSync.HandleCodepadEnabled(__instance);
            net.PuzzleSync.QueueReapply();
        }
    }

    [HarmonyPatch(typeof(Lab_PatternLockControl), "OnEnable")]
    public static class PatternLockControlEnablePatch
    {
        internal static void KillIfSpent(Lab_PatternLockControl ctrl)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected || ctrl == null) return;
            LAB_PatternLock pad = null;
            try { pad = ctrl._lock; } catch (System.Exception e) { Guard.Swallow(e); }
            if (pad == null)
            {
                try { pad = ctrl.GetComponent<LAB_PatternLock>(); } catch (System.Exception e) { Guard.Swallow(e); }
            }
            if (pad == null)
            {
                try { pad = ctrl.GetComponentInChildren<LAB_PatternLock>(true); } catch (System.Exception e) { Guard.Swallow(e); }
            }
            net.PuzzleSync.HandlePatternLockEnabled(pad);
            net.PuzzleSync.QueueReapply();
        }

        [HarmonyPostfix]
        public static void Postfix(Lab_PatternLockControl __instance)
        {
            KillIfSpent(__instance);
        }
    }

    [HarmonyPatch(typeof(Interaction), nameof(Interaction.reset))]
    public static class InteractionResetSpentPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Interaction __instance)
        {
            // RVA 0x60E4D0 is shared with InteractionItem.reset, UnityDefaultGui.onEndedHandler, CheckIfInsideBeam.FixedUpdate (docs/RVA_FOLDING.md).
            if (!Il2CppRealType.Is<Interaction>(__instance)) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected || __instance == null) return;
            if (!net.PuzzleSync.ShouldKillOverlay(__instance)) return;
            try { __instance.triggered = true; } catch (System.Exception e) { Guard.Swallow(e); }
            try { __instance.enabled = false; } catch (System.Exception e) { Guard.Swallow(e); }
        }
    }

    [HarmonyPatch(typeof(Interaction), nameof(Interaction.trigger))]
    public static class InteractionTriggerSpentPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Interaction __instance)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected || __instance == null) return true;
            if (!net.PuzzleSync.ShouldKillOverlay(__instance)) return true;
            try { PlaytestLog.Event("Puzzle", "overlay kill trigger " + __instance.gameObject.name + " (already solved/held)"); }
            catch (System.Exception e) { Guard.Swallow(e); }
            try { __instance.triggered = true; } catch (System.Exception e) { Guard.Swallow(e); }
            try { __instance.enabled = false; } catch (System.Exception e) { Guard.Swallow(e); }
            return false;
        }
    }

    [HarmonyPatch(typeof(Interaction), nameof(Interaction.setInRange))]
    public static class InteractionRangeSpentPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Interaction __instance, bool _inRange)
        {
            // RVA 0x2AA2B0 is shared with ten unrelated bool setters (docs/RVA_FOLDING.md).
            if (!Il2CppRealType.Is<Interaction>(__instance)) return true;
            if (!_inRange || __instance == null) return true;
            var net = LanNetworkManager.Instance;
            if (net != null && net.IsConnected && net.PuzzleSync.ShouldKillOverlay(__instance))
            {
                try { __instance.triggered = true; } catch (System.Exception e) { Guard.Swallow(e); }
                try { __instance.inRange = false; } catch (System.Exception e) { Guard.Swallow(e); }
                try { __instance.enabled = false; } catch (System.Exception e) { Guard.Swallow(e); }
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(EventScreenInteraction), nameof(EventScreenInteraction.startEvent))]
    public static class EventScreenSpentPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(EventScreenInteraction __instance)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected || __instance == null) return true;
            Interaction inter = null;
            try { inter = __instance.inter; } catch (System.Exception e) { Guard.Swallow(e); }
            if (inter == null || !net.PuzzleSync.ShouldKillOverlay(inter)) return true;
            try { __instance.enabled = false; } catch (System.Exception e) { Guard.Swallow(e); }
            return false;
        }
    }

    [HarmonyPatch(typeof(EventScreenInteraction), nameof(EventScreenInteraction.startEventInstant))]
    public static class EventScreenSpentInstantPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(EventScreenInteraction __instance)
        {
            return EventScreenSpentPatch.Prefix(__instance);
        }
    }

    [HarmonyPatch(typeof(Room), nameof(Room.EnterRoom))]
    public static class RoomEnterPuzzlePatch
    {
        [HarmonyPostfix]
        public static void Postfix(Room __instance)
        {
            try { FlickerTrace.RoomEnter(__instance); } catch (System.Exception e) { Guard.Swallow(e); }
            if (NetGate.IsApplying) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            try { net.PickupSync.HideClaimed(null); } catch (System.Exception e) { Guard.Swallow(e); }
            net.PuzzleSync.QueueReapply();
        }
    }

    [HarmonyPatch(typeof(Room), nameof(Room.SetChunkStatus))]
    public static class RoomChunkPuzzlePatch
    {
        [HarmonyPostfix]
        public static void Postfix(Room __instance, bool value)
        {
            try { FlickerTrace.RoomChunk(__instance, value); } catch (System.Exception e) { Guard.Swallow(e); }
            if (!value || NetGate.IsApplying) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            try { net.PickupSync.HideClaimed(null); } catch (System.Exception e) { Guard.Swallow(e); }
            net.PuzzleSync.QueueReapply();
        }
    }
}
