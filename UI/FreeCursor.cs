// Opt-in free OS pointer for dual-box testing (port of DarkwoodMP CursorConfineFocusGuard)
using HarmonyLib;
using SyncRADation.Config;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.UI
{
    /// <summary>
    /// Vanilla SIGNALIS sets <see cref="CursorLockMode.Confined"/> (or Locked) every PlayerState update.
    /// Under Proton that becomes Win32 ClipCursor, which traps the pointer in one window on a dual-box
    /// desktop. With <c>FreeCursor</c> true every Confined write is rewritten to None; aiming still
    /// reads the in-window mouse position. Locked is kept while the window has focus: first-person
    /// scenes (EventWalking.Update, Ghidra EventWalking.c: Locked unless gameState 7) read mouse deltas,
    /// so a free pointer runs into the window edge and looking stops. F9 frees it until the window
    /// loses and regains focus; leaving the window (Alt+Tab / compositor focus bind) frees it too.
    /// Off = vanilla.
    /// </summary>
    public static class FreeCursor
    {
        // persistent: frame counter for the cursor poll
        private static int _tick;
        // persistent: F9 release, cleared when the window regains focus (process-local, not session state)
        private static bool _released;
        // persistent: last seen Application.isFocused (process-local)
        private static bool _wasFocused = true;

        public static bool Enabled => ModConfig.FreeCursor?.Value == true;

        /// <summary>The value the game may actually get for a lockState write.</summary>
        internal static CursorLockMode Filter(CursorLockMode value)
        {
            if (value == CursorLockMode.None || !Enabled) return value;
            if (value == CursorLockMode.Locked && !_released && Application.isFocused) return value;
            return CursorLockMode.None;
        }

        /// <summary>Every frame from ModRuntime.OnUpdate: F9 and focus edges. The lockState
        /// re-assert stays rare; per-frame ClipCursor thrash can freeze Wine.</summary>
        public static void Tick()
        {
            if (!Enabled) return;
            bool focused = Application.isFocused;
            if (focused && !_wasFocused) _released = false;
            _wasFocused = focused;

            bool force = false;
            if (focused && Input.GetKeyDown(KeyCode.F9))
            {
                _released = !_released;
                force = true;
                PlaytestLog.Event("Cursor", _released ? "F9 freed the mouse" : "F9 mouse back to the game");
            }

            if (!force && (++_tick & 31) != 0) return;
            var cur = Cursor.lockState;
            var want = Filter(cur);
            if (want != cur) Cursor.lockState = want;
        }
    }

    [HarmonyPatch(typeof(UnityEngine.Cursor), "set_lockState")]
    public static class CursorLockStatePatch
    {
        private static void Prefix(ref CursorLockMode value) => value = FreeCursor.Filter(value);
    }
}
