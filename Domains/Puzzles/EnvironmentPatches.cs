// Native solve edges for chapter machines (pump, writer, shutters, pipes, …).
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Patches
{
        static class EnvEmit
        {
            // Update-postfix edges (Magpie/Shutters/…) — one emit per WorldId until scene refresh.
            static readonly System.Collections.Generic.HashSet<string> _once
                = new System.Collections.Generic.HashSet<string>();

            public static void Progressed(PuzzleType type, Component c) => Send(type, c, true);
            public static void Read(PuzzleType type, Component c) => Send(type, c, false);
            public static void ReadOnce(PuzzleType type, Component c)
            {
                if (c == null || NetGate.IsApplying) return;
                var net = LanNetworkManager.Instance;
                if (net == null || !net.IsConnected) return;
                ulong id = WorldId.FromGameObject(c.gameObject);
                string key = ((byte)type) + "_" + id.ToString("X");
                if (_once.Contains(key)) return;
                Send(type, c, false);
                _once.Add(key);
            }

            public static void ClearOnce() => _once.Clear();

            public static void LabRings(LAB_Rings x)
            {
                if (x == null) return;
                if (x.solved) Progressed(PuzzleType.LAB_Rings, x);
                else Read(PuzzleType.LAB_Rings, x);
            }

            static void Send(PuzzleType type, Component c, bool progressed)
            {
                if (c == null || NetGate.IsApplying) return;
                var net = LanNetworkManager.Instance;
                if (net == null || !net.IsConnected) return;
                ulong id = WorldId.FromGameObject(c.gameObject);
                if (progressed) net.PuzzleSync.EmitProgressed(type, id);
                else net.PuzzleSync.Emit(type, id, c);
            }
        }

    [HarmonyPatch(typeof(MED_Pump), "checkSolved")]
    public static class MedPumpSolvedPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MED_Pump __instance)
        {
            if (__instance == null || !__instance.solved) return;
            EnvEmit.Progressed(PuzzleType.MED_Pump, __instance);
            try
            {
                if (__instance.flood != null)
                    EnvEmit.Progressed(PuzzleType.MED_FloodedBathroom, __instance.flood);
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(MED_FloodedBathroom), nameof(MED_FloodedBathroom.Drain))]
    public static class MedFloodDrainPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MED_FloodedBathroom __instance)
        {
            EnvEmit.Progressed(PuzzleType.MED_FloodedBathroom, __instance);
        }
    }

    [HarmonyPatch(typeof(MED_CardWriter), "Update")]
    public static class MedCardWriterPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MED_CardWriter __instance)
        {
            if (__instance == null || NetGate.IsApplying) return;
            try
            {
                if (__instance.solved)
                    EnvEmit.ReadOnce(PuzzleType.MED_CardWriter, __instance);
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(ROT_Pipes), nameof(ROT_Pipes.TurnValve))]
    public static class RotPipesPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ROT_Pipes __instance)
        {
            EnvEmit.Progressed(PuzzleType.ROT_Pipes, __instance);
        }
    }

    [HarmonyPatch(typeof(ROT_Magpie), "Update")]
    public static class RotMagpiePatch
    {
        [HarmonyPostfix]
        public static void Postfix(ROT_Magpie __instance)
        {
            if (__instance == null || NetGate.IsApplying) return;
            try
            {
                if (__instance.opened)
                    EnvEmit.ReadOnce(PuzzleType.ROT_Magpie, __instance);
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(PEN_Reaktor), "win")]
    public static class PenReaktorWinPatch
    {
        [HarmonyPostfix]
        public static void Postfix(PEN_Reaktor __instance)
        {
            EnvEmit.Progressed(PuzzleType.PEN_Reaktor, __instance);
        }
    }

    [HarmonyPatch(typeof(EXC_Seilbahn), nameof(EXC_Seilbahn.goDown))]
    public static class ExcSeilbahnPatch
    {
        [HarmonyPostfix]
        public static void Postfix(EXC_Seilbahn __instance)
        {
            EnvEmit.Progressed(PuzzleType.EXC_Seilbahn, __instance);
        }
    }

    [HarmonyPatch(typeof(EXC_Hatch), nameof(EXC_Hatch.OpenHatch))]
    public static class ExcHatchPatch
    {
        [HarmonyPostfix]
        public static void Postfix(EXC_Hatch __instance)
        {
            EnvEmit.Progressed(PuzzleType.EXC_Hatch, __instance);
        }
    }

    // LAB_Rings: emit partial finger pack on place/take (Int0), Progressed on solve.
    // checkSolution alone used to gate emit on solved — peers never saw mid-puzzle
    // finger states (Dig O). placeRing/takeRing cover the durable state edges;
    // checkSolution still Progressed when native latches solved.
    [HarmonyPatch(typeof(LAB_Rings), nameof(LAB_Rings.placeRing))]
    public static class LabRingsPlacePatch
    {
        [HarmonyPostfix]
        public static void Postfix(LAB_Rings __instance)
        {
            EnvEmit.LabRings(__instance);
        }
    }

    [HarmonyPatch(typeof(LAB_Rings), nameof(LAB_Rings.takeRing))]
    public static class LabRingsTakePatch
    {
        [HarmonyPostfix]
        public static void Postfix(LAB_Rings __instance)
        {
            EnvEmit.LabRings(__instance);
        }
    }

    [HarmonyPatch(typeof(LAB_Rings), nameof(LAB_Rings.checkSolution))]
    public static class LabRingsPatch
    {
        [HarmonyPostfix]
        public static void Postfix(LAB_Rings __instance)
        {
            if (__instance == null || !__instance.solved) return;
            EnvEmit.Progressed(PuzzleType.LAB_Rings, __instance);
        }
    }

    [HarmonyPatch(typeof(ROT_MeatBlocker), nameof(ROT_MeatBlocker.pickup))]
    public static class RotMeatPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ROT_MeatBlocker __instance)
        {
            // Hold Death-seal emit while NG+ KeyOfSacrifice is still obtainable —
            // mirrors ApplyMeatBlocker hold (wiki Artifact softlock).
            try
            {
                if (__instance != null && __instance.blocked)
                {
                    var id = __instance.ID;
                    if (id != null && string.Equals(id, "Death", System.StringComparison.Ordinal))
                    {
                        var net = LanNetworkManager.Instance;
                        if (net != null && net.IsConnected
                            && net.PickupSync.KeyOfSacrificeAvailableUnclaimed())
                        {
                            PlaytestLog.Event("Puzzle", "hold Death MeatBlocker emit until KeyOfSacrifice");
                            return;
                        }
                    }
                }
            }
            catch { }
            EnvEmit.Read(PuzzleType.ROT_MeatBlocker, __instance);
        }
    }

    [HarmonyPatch(typeof(RES_Shutters), "Update")]
    public static class ResShuttersPatch
    {
        [HarmonyPostfix]
        public static void Postfix(RES_Shutters __instance)
        {
            if (__instance == null || NetGate.IsApplying) return;
            try
            {
                if (__instance.unlocked)
                    EnvEmit.ReadOnce(PuzzleType.RES_Shutters, __instance);
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(BiodomeDoorLock), "Update")]
    public static class BiodomeLockPatch
    {
        [HarmonyPostfix]
        public static void Postfix(BiodomeDoorLock __instance)
        {
            if (__instance == null || NetGate.IsApplying) return;
            try
            {
                if (__instance.KeyLevel > 0 || !__instance.hasLock)
                    EnvEmit.ReadOnce(PuzzleType.BiodomeDoorLock, __instance);
            }
            catch { }
        }
    }

    // LAB_Waage: placeItem starts delayedMulti which SetActive(true) on MultiInteraction
    // after a yield — native gate flag is still false at postfix time. EmitProgressed
    // forces Bool0 so peers/late-join ApplyWaage activate the gate now (Dig S).
    [HarmonyPatch(typeof(LAB_Waage), nameof(LAB_Waage.placeItem))]
    public static class LabWaagePlacePatch
    {
        [HarmonyPostfix]
        public static void Postfix(LAB_Waage __instance)
        {
            EnvEmit.Progressed(PuzzleType.LAB_Waage, __instance);
        }
    }

    [HarmonyPatch(typeof(LAB_PatternLock), nameof(LAB_PatternLock.toggleButton))]
    public static class PatternLockTogglePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(LAB_PatternLock __instance)
        {
            if (__instance == null) return true;
            try
            {
                if (__instance.solved) return false;
            }
            catch { }
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(LAB_PatternLock __instance)
        {
            if (__instance == null || NetGate.IsApplying) return;
            try
            {
                if (__instance.solved)
                    EnvEmit.Progressed(PuzzleType.PatternLock, __instance);
            }
            catch { }
        }
    }
}
