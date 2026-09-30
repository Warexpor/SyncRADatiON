// Keypad3D / ROT_Keypad / PEN_Codepad — client submit emit + NoteSolved.
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Patches
{
    [HarmonyPatch(typeof(Keypad3D), "openDoor")]
    public static class Keypad3DOpenPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Keypad3D __instance) => ClientKeypad.Submit(__instance);
    }

    static class ClientKeypad
    {
        private static readonly System.Collections.Generic.HashSet<ulong> _sent
            = new System.Collections.Generic.HashSet<ulong>();

        public static void OnSceneChanged() => _sent.Clear();

        public static bool Submit(Component inst)
        {
            if (NetGate.IsApplying || !NetGate.Live) return true;
            if (inst == null) return true;
            if (NetGate.Host) return true;
            ulong id = WorldId.FromGameObject(inst.gameObject);
            if (id == 0 || !_sent.Add(id)) return false;
            PlaytestLog.Event("Interact", "KeypadSubmit " + inst.GetType().Name + " id=" + id.ToString("X16"));
            LanNetworkManager.Instance.SendInteractionRequest(id, InteractionKind.KeypadSubmit);
            return false;
        }

        public static void NoteSolved(Component inst)
        {
            if (NetGate.IsApplying || !NetGate.Live) return;
            if (inst == null) return;
            ulong id = WorldId.FromGameObject(inst.gameObject);
            if (id == 0 || !_sent.Add(id)) return;
            if (NetGate.Client)
            {
                PlaytestLog.Event("Interact", "KeypadSubmit " + inst.GetType().Name + " id=" + id.ToString("X16"));
                LanNetworkManager.Instance.SendInteractionRequest(id, InteractionKind.KeypadSubmit);
            }
            var pad = inst as PEN_Codepad;
            if (pad != null)
                LanNetworkManager.Instance.PuzzleSync.Emit(PuzzleType.PEN_Codepad, id, pad);
        }
    }

    [HarmonyPatch(typeof(ROT_Keypad), "verify")]
    public static class RotKeypadVerifyPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ROT_Keypad __instance)
        {
            if (__instance == null || NetGate.IsApplying || !NetGate.Live) return;
            try
            {
                if (!__instance.solved && !__instance.opening) return;
                ClientKeypad.NoteSolved(__instance);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }
    }

    [HarmonyPatch(typeof(PEN_Codepad), "CheckSolution")]
    public static class PenCodepadSolvePatch
    {
        [HarmonyPostfix]
        public static void Postfix(PEN_Codepad __instance)
        {
            if (__instance == null || NetGate.IsApplying || !NetGate.Live) return;
            try
            {
                if (!__instance.solved) return;
                ClientKeypad.NoteSolved(__instance);
                ulong id = WorldId.FromGameObject(__instance.gameObject);
                LanNetworkManager.Instance.PuzzleSync.EmitProgressed(PuzzleType.PEN_Codepad, id);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }
    }
}
