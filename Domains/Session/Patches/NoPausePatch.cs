// Co-op never pauses the world. Native pause points (PauseMenu, InventoryBase, ItemPickup item view, Dialogue,
// BookScreen, EideticModule, NewMainMenu, MakeReport) write Time.timeScale = 0. On the host that froze every
// enemy and boss for the whole party while a client kept moving; on a client it froze its puppets/FX while the
// host's world (and its damage) kept going. While a session has a connected peer, a 0 write is dropped and the
// current scale (normally 1, or a slow-mo value) stays. Menus still block the local player's input through
// gameState, and they are not a safe room: a hit closes the open screen and then lands (Players/MenuHit).
// Solo / lone host = vanilla.
using HarmonyLib;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Patches
{
    [HarmonyPatch(typeof(Time), "set_timeScale")]
    public static class NoPausePatch
    {
        static bool _logged;

        [HarmonyPrefix]
        public static bool Prefix(float value)
        {
            if (value > 0.0001f) return true;
            if (!NetGate.Party) return true;
            if (!_logged)
            {
                _logged = true;
                PlaytestLog.Event("Session", "pause ignored (co-op world keeps running)");
            }
            return false;
        }

        internal static void ResetSession() => _logged = false;
    }
}
