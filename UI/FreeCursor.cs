// Opt-in free OS pointer for dual-box testing (port of DarkwoodMP CursorConfineFocusGuard)
using HarmonyLib;
using SyncRADation.Config;
using UnityEngine;

namespace SyncRADation.UI
{
    /// <summary>
    /// Vanilla SIGNALIS sets <see cref="CursorLockMode.Confined"/> (or Locked) every PlayerState update.
    /// Under Proton that becomes Win32 ClipCursor, which traps the pointer in one window on a dual-box
    /// desktop. With <c>FreeCursor</c> true every Confined/Locked write is rewritten to None; aiming still
    /// reads the in-window mouse position. Off = vanilla.
    /// </summary>
    public static class FreeCursor
    {
        private static int _tick;

        public static bool Enabled => ModConfig.FreeCursor?.Value == true;

        /// <summary>Rare re-assert from ModRuntime.OnUpdate; per-frame ClipCursor thrash can freeze Wine.</summary>
        public static void Tick()
        {
            if (!Enabled || (++_tick & 31) != 0) return;
            if (UnityEngine.Cursor.lockState != CursorLockMode.None)
                UnityEngine.Cursor.lockState = CursorLockMode.None;
        }
    }

    [HarmonyPatch(typeof(UnityEngine.Cursor), "set_lockState")]
    public static class CursorLockStatePatch
    {
        private static void Prefix(ref CursorLockMode value)
        {
            if (value != CursorLockMode.None && FreeCursor.Enabled)
                value = CursorLockMode.None;
        }
    }
}
