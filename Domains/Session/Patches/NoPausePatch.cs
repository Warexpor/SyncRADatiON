// Co-op never pauses the world. Native pause points (PauseMenu, InventoryBase, ItemPickup item view, Dialogue,
// BookScreen, EideticModule, NewMainMenu, MakeReport) write Time.timeScale = 0. On the host that froze every
// enemy and boss for the whole party while a client kept moving; on a client it froze its puppets/FX while the
// host's world (and its damage) kept going. While a session has a connected peer, a 0 write is dropped and the
// current scale (normally 1, or a slow-mo value) stays. Menus/inventory still block the local player's input
// through gameState. Solo / lone host = vanilla.
using HarmonyLib;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Patches
{
    [HarmonyPatch(typeof(Time), "set_timeScale")]
    public static class NoPausePatch
    {
        static bool _logged;

        /// <summary>
        /// The local game asked to pause and we kept it running. Vanilla "paused" means nothing can touch Elster, so
        /// while this is set the local player takes no damage (DeathPatches): item-pickup prompts and story dialogue
        /// are gameState dialogue, where native HurtElster does hurt — safe in vanilla only because time was frozen.
        /// Cleared by the matching unpause (any non-zero write).
        /// </summary>
        public static bool Held { get; private set; }

        [HarmonyPrefix]
        public static bool Prefix(float value)
        {
            if (value > 0.0001f)
            {
                Held = false;
                return true;
            }
            if (!NetGate.Party) return true;
            Held = true;
            if (!_logged)
            {
                _logged = true;
                PlaytestLog.Event("Session", "pause ignored (co-op world keeps running)");
            }
            return false;
        }

        internal static void ResetSession()
        {
            _logged = false;
            Held = false;
            _playSince = -1f;
        }

        static float _playSince = -1f;

        /// <summary>
        /// Safety net (ModRuntime.OnUpdate): a pause owner that never writes the unpause (scene swap, destroyed menu)
        /// must not leave the player invulnerable. Back in plain play for a second = not paused.
        /// </summary>
        public static void Tick()
        {
            if (!Held) { _playSince = -1f; return; }
            bool play;
            try { play = PlayerState.gameState == PlayerState.gameStates.play; }
            catch { return; }
            float now = Time.unscaledTime;
            if (!play) { _playSince = -1f; return; }
            if (_playSince < 0f) { _playSince = now; return; }
            if (now - _playSince < 1f) return;
            Held = false;
            _playSince = -1f;
            PlaytestLog.Event("Session", "pause hold cleared (back in play without an unpause)");
        }
    }
}
