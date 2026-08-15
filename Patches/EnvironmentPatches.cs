// Native solve edges for chapter machines (pump, writer, shutters, pipes, …).
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Patches
{
    static class EnvEmit
    {
        public static void Progressed(PuzzleType type, Component c)
        {
            if (c == null || NetGate.IsApplying) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            ulong id = WorldId.FromGameObject(c.gameObject);
            net.PuzzleSync.EmitProgressed(type, id);
        }

        public static void Read(PuzzleType type, Component c)
        {
            if (c == null || NetGate.IsApplying) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            ulong id = WorldId.FromGameObject(c.gameObject);
            net.PuzzleSync.Emit(type, id, c);
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
                    EnvEmit.Read(PuzzleType.MED_CardWriter, __instance);
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
                    EnvEmit.Read(PuzzleType.ROT_Magpie, __instance);
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
                    EnvEmit.Read(PuzzleType.RES_Shutters, __instance);
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
                    EnvEmit.Read(PuzzleType.BiodomeDoorLock, __instance);
            }
            catch { }
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
