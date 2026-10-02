// Host: an enemy's native AI chases its nearest player, not only the host.
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Patches
{
    /// <summary>
    /// Native EnemyController.Update knows one player: it copies PlayerState.player's position into playerPos every
    /// frame, chases PlayerState.player (targetPosition), Linecasts to it for LOS and measures attack range against it
    /// (Ghidra EnemyController.c UpdateDataBlock / Update / LOS / CheckStateChange). So an enemy only ever woke, chased,
    /// attacked or gave up depending on where the host stood. While this enemy's chase target is a remote player
    /// (EnemySyncService.ChaseTargetOf), PlayerState.player is that player's proxy root for the length of its Update.
    /// Those reads only take the transform position; the finalizer restores the host even when Update throws.
    /// </summary>
    [HarmonyPatch(typeof(EnemyController), "Update")]
    public static class EnemyTargetPatch
    {
        // persistent: the host Elster parked for one enemy Update, restored by the finalizer of that same call
        static GameObject _parked;

        /// <summary>Inside a swapped enemy Update (PlayerState.player is a proxy root).</summary>
        public static bool Swapped => _parked != null;

        [HarmonyPrefix]
        public static void Prefix(EnemyController __instance, out bool __state)
        {
            __state = false;
            if (!NetGate.Host || _parked != null) return;
            try
            {
                var target = EnemySyncService.ChaseTargetOf(__instance);
                if (target == null) return;
                var host = PlayerState.player;
                if (host == null || host == target) return;
                _parked = host;
                PlayerState.player = target;
                __state = true;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        [HarmonyFinalizer]
        public static System.Exception Finalizer(bool __state, System.Exception __exception)
        {
            if (__state && _parked != null)
            {
                try { PlayerState.player = _parked; }
                catch (System.Exception e) { Guard.Swallow(e); }
                _parked = null;
            }
            return __exception;
        }
    }
}
