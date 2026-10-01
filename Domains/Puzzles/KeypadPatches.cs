// Live keypads (KeypadLive): each local press is emitted at the end of the frame it happened in, with the code
// as native Update left it. Native Update polls Keys[i].triggered and does everything inline: pushButton(key)
// first, then the digit / clear / verify (Ghidra Keypad3D.c, ROT_Keypad.c), so pushButton names the key and the
// Update postfix sees the result. openDoor / CheckSolution have no native caller (Update inlines them), which is
// why the 0.5.64 Harmony patches on them never fired.
using System.Collections.Generic;
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Patches
{
    static class KeypadPress
    {
        // Instance id → key pressed this frame (local cache key only, never sent).
        static readonly Dictionary<int, int> _pending = new Dictionary<int, int>();

        public static bool Any => _pending.Count > 0;

        public static void Clear() => _pending.Clear();

        public static void Note(Component pad, Interaction[] keys, Transform key)
        {
            if (pad == null || key == null || NetGate.IsApplying || !NetGate.Party) return;
            int idx = -1;
            try
            {
                if (keys != null)
                    for (int i = 0; i < keys.Length; i++)
                        if (keys[i] != null && keys[i].transform == key) { idx = i; break; }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            if (idx >= 0) _pending[pad.GetInstanceID()] = idx;
        }

        public static void NoteOp(Component pad, int op)
        {
            if (pad == null || op == 0 || NetGate.IsApplying || !NetGate.Party) return;
            _pending[pad.GetInstanceID()] = op;
        }

        public static bool Take(Component pad, out int key)
        {
            key = -1;
            if (pad == null || _pending.Count == 0) return false;
            int iid = pad.GetInstanceID();
            if (!_pending.TryGetValue(iid, out key)) return false;
            _pending.Remove(iid);
            return true;
        }

        public static void Emit(PuzzleType type, Component pad, int key, bool solved, bool wrong)
        {
            ulong id = WorldId.FromGameObject(pad.gameObject);
            if (id == 0) return;
            KeypadLive.NotePress(id, key, wrong);
            if (solved) EnvEmit.Progressed(type, pad);
            else EnvEmit.Read(type, pad);
        }
    }

    [HarmonyPatch(typeof(Keypad3D), "pushButton")]
    public static class Keypad3DPushPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Keypad3D __instance, Transform __0)
        {
            try { KeypadPress.Note(__instance, __instance != null ? __instance.Keys : null, __0); }
            catch (System.Exception e) { Guard.Swallow(e); }
        }
    }

    [HarmonyPatch(typeof(Keypad3D), "Update")]
    public static class Keypad3DUpdatePatch
    {
        [HarmonyPostfix]
        public static void Postfix(Keypad3D __instance)
        {
            if (!KeypadPress.Any) return;
            int key;
            if (!KeypadPress.Take(__instance, out key)) return;
            try
            {
                bool solved = __instance.solved;
                KeypadPress.Emit(PuzzleType.Keypad3D, __instance, key, solved, key == 11 && !solved);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }
    }

    [HarmonyPatch(typeof(Keypad3D), "OnEnable")]
    public static class Keypad3DEnablePatch
    {
        [HarmonyPostfix]
        public static void Postfix(Keypad3D __instance)
        {
            if (__instance == null) return;
            try
            {
                if (__instance.solved) return;
                KeypadLive.RestoreCode(__instance, () => __instance.code, v => __instance.code = v);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }
    }

    [HarmonyPatch(typeof(ROT_Keypad), "pushButton")]
    public static class RotKeypadPushPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ROT_Keypad __instance, Transform __0)
        {
            try { KeypadPress.Note(__instance, __instance != null ? __instance.Keys : null, __0); }
            catch (System.Exception e) { Guard.Swallow(e); }
        }
    }

    [HarmonyPatch(typeof(ROT_Keypad), "Update")]
    public static class RotKeypadUpdatePatch
    {
        [HarmonyPostfix]
        public static void Postfix(ROT_Keypad __instance)
        {
            if (!KeypadPress.Any) return;
            int key;
            if (!KeypadPress.Take(__instance, out key)) return;
            try
            {
                bool solved = __instance.solved;
                KeypadPress.Emit(PuzzleType.ROT_Keypad, __instance, key, solved, key == 11 && !solved);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }
    }

    // PEN_Codepad: Update starts UpdateDigitDiplay(i, up) for a wheel (i 0..5), reset (6) and submit (7).
    [HarmonyPatch(typeof(PEN_Codepad), "UpdateDigitDiplay")]
    public static class PenCodepadDigitPatch
    {
        [HarmonyPostfix]
        public static void Postfix(PEN_Codepad __instance, int __0, bool __1)
        {
            try { KeypadPress.NoteOp(__instance, KeypadLive.CodepadOp(__0, __1)); }
            catch (System.Exception e) { Guard.Swallow(e); }
        }
    }

    [HarmonyPatch(typeof(PEN_Codepad), "Update")]
    public static class PenCodepadUpdatePatch
    {
        [HarmonyPostfix]
        public static void Postfix(PEN_Codepad __instance)
        {
            if (!KeypadPress.Any) return;
            int op;
            if (!KeypadPress.Take(__instance, out op)) return;
            try { KeypadPress.Emit(PuzzleType.PEN_Codepad, __instance, op, __instance.solved, false); }
            catch (System.Exception e) { Guard.Swallow(e); }
        }
    }

    [HarmonyPatch(typeof(PEN_Codepad), "OnEnable")]
    public static class PenCodepadEnableLivePatch
    {
        [HarmonyPostfix]
        public static void Postfix(PEN_Codepad __instance)
        {
            if (__instance == null) return;
            try
            {
                if (__instance.solved) return;
                KeypadLive.RestoreWheels(__instance);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }
    }
}
