// Edge-triggered door visuals. Polling at 0.3s misses a client walking through.
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Patches
{
    // Doorway_Double.Update calls openDoors / closeDoors EVERY frame (they lerp the leaves toward `open`, Ghidra
    // Doorway_Double.c Update); the real state is the `open` flag. Both hooks stay per-frame cheap.
    [HarmonyPatch(typeof(Doorway_Double), "openDoors")]
    public static class DoubleDoorOpenPatch
    {
        static readonly System.Collections.Generic.HashSet<int> _blockLogged = new System.Collections.Generic.HashSet<int>();

        /// <summary>Scene change: the per-door "blocked" log may fire again.</summary>
        internal static void ResetScene() => _blockLogged.Clear();

        [HarmonyPrefix]
        public static bool Prefix(Doorway_Double __instance, ref bool __state)
        {
            __state = true;
            // Solo / lone host: vanilla. ConnectedDoors traversal sets open=true without checking locked
            // (Ghidra ConnectedDoors.c traverseAB), so blocking here would freeze that door.
            if (__instance == null || NetGate.IsApplying || !NetGate.Party) return true;
            try
            {
                if (!__instance.locked) return true;
            }
            catch { return true; }
            __state = false;
            int key = 0;
            try { key = __instance.GetInstanceID(); } catch (System.Exception e) { Guard.Swallow(e); }
            if (_blockLogged.Add(key))
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

    // A peer's solve replays onSolved / onSuccess / onUnlocked here, and those UnityEvents call exitEvent on a screen
    // this player never opened. Native Callback then sets PlayerState.suspendInput, waits 0.6 s and indexes
    // eventCamera.path[0]: the list is only filled while the screen is open, so the coroutine throws before it clears
    // suspendInput and the player can never move again. Only exit a screen that is actually open here.
    [HarmonyPatch(typeof(EventScreenInteraction), nameof(EventScreenInteraction.exitEvent))]
    public static class EventScreenExitNotOpenPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(EventScreenInteraction __instance)
        {
            if (__instance == null || !NetGate.Party) return true;
            bool open = true;
            try { open = __instance.Eventing; } catch (System.Exception e) { Guard.Swallow(e); }
            if (open) return true;
            string name = "?";
            try { name = __instance.gameObject.name; } catch (System.Exception e) { Guard.Swallow(e); }
            PlaytestLog.Event("Puzzle", "exitEvent skipped (screen not open here) " + name);
            return false;
        }
    }

    // Native OobChecker (Ghidra OobChecker.c): each frame one SphereCast from the player along +y / -y / +x / -x
    // (round robin on `tick`, mask = walls). A miss counts as out of bounds; after 1 s the player is teleported to
    // the room's gotoSpawn / camera centre and every door is reset. Alone, a door is only open while this player
    // traverses it (gameState 5, not checked). In co-op a peer opens the door next to you: the cast leaves through
    // the doorway into the next room's unloaded chunk, misses, and you are "rescued" to the middle of your room.
    // A miss whose ray runs through an open (or still closing) door is a doorway, not out of bounds.
    [HarmonyPatch(typeof(OobChecker), nameof(OobChecker.Check))]
    public static class OobThroughOpenDoorPatch
    {
        const float PerpMax = 4f;
        const float AheadMax = 80f;
        static float _lastLog = -99f;

        [HarmonyPostfix]
        public static void Postfix(OobChecker __instance, ref bool __result)
        {
            if (__result || __instance == null || !NetGate.Party) return;
            try
            {
                var t = __instance.trans;
                if (t == null) return;
                Vector2 p = t.position;
                Vector2 dir;
                switch (__instance.tick) // a miss leaves tick on the direction that missed
                {
                    case 0: dir = Vector2.up; break;
                    case 1: dir = Vector2.down; break;
                    case 2: dir = Vector2.right; break;
                    default: dir = Vector2.left; break;
                }
                string door = DoorOnRay(p, dir);
                if (door == null) return;
                __result = true;
                float now = Time.unscaledTime;
                if (now - _lastLog > 5f)
                {
                    _lastLog = now;
                    PlaytestLog.Event("Door", "oob rescue held: cast " + dir + " leaves through open door " + door);
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        static string DoorOnRay(Vector2 p, Vector2 dir)
        {
            foreach (var kvp in WorldRegistry.AllDoubleDoors())
            {
                var d = kvp.Value;
                if (d == null || !d.isActiveAndEnabled || !DoubleOpenish(d)) continue;
                if (OnRay(p, dir, d.transform.position)) return d.gameObject.name;
            }
            foreach (var kvp in WorldRegistry.AllSlidingDoors())
            {
                var s = kvp.Value;
                if (s == null || !s.isActiveAndEnabled || !(s.opened || s.moving)) continue;
                if (OnRay(p, dir, s.transform.position)) return s.gameObject.name;
            }
            return null;
        }

        // Open, or closing: closeDoors lerps the leaves' local x (NS) / z back to 0 (Ghidra Doorway_Double.c).
        static bool DoubleOpenish(Doorway_Double d)
        {
            if (d.open) return true;
            return LeafOpen(d.Left, d.NS) || LeafOpen(d.Right, d.NS);
        }

        static bool LeafOpen(Transform leaf, bool ns)
        {
            if (leaf == null) return false;
            var lp = leaf.localPosition;
            return Mathf.Abs(ns ? lp.x : lp.z) > 0.05f;
        }

        static bool OnRay(Vector2 p, Vector2 dir, Vector3 doorPos)
        {
            Vector2 d = (Vector2)doorPos - p;
            float ahead = Vector2.Dot(d, dir);
            if (ahead < -1f || ahead > AheadMax) return false;
            float perp = Mathf.Abs(d.x * dir.y - d.y * dir.x);
            return perp <= PerpMax;
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
