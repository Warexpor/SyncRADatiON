// Host: an enemy's native AI chases its nearest player, not only the host.
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Patches
{
    /// <summary>
    /// Native EnemyController knows one player: PlayerState.player. Update copies its position into playerPos, chases it
    /// (targetPosition) and measures attack range against it (Ghidra EnemyController.c UpdateDataBlock / Update /
    /// CheckStateChange). The coroutines it starts read it again on their own frames, after Update returned:
    /// TrackAndAttack calls LOS (Linecast to PlayerState.player) every tracking frame and before StartCoroutine(Attack);
    /// Attack Linecasts to it before charging and measures the 6.4 m range against it; Hurt / Stagger / Critical / Fire
    /// face their reaction away from it (playerPos = PlayerState.player.position → anim_X/anim_Y). So an enemy only
    /// ever woke, chased, attacked or reacted depending on where the host stood. While this enemy's chase target is a
    /// remote player (EnemySyncService.ChaseTargetOf), PlayerState.player is that player's proxy root for the length of
    /// its Update and of each of those coroutine steps. Those reads only take the transform position; the finalizer
    /// restores the host even when the native code throws.
    /// </summary>
    public static class EnemyTargetPatch
    {
        // persistent: the host Elster parked for one enemy Update / coroutine step, restored by that same call's finalizer
        static GameObject _parked;

        /// <summary>Inside a swapped enemy Update or coroutine step (PlayerState.player is a proxy root).</summary>
        public static bool Swapped => _parked != null;

        /// <summary>Swap PlayerState.player to this enemy's chase target; true when the caller must call End.</summary>
        internal static bool Begin(EnemyController e)
        {
            // Nested: Update / TrackAndAttack starting a coroutine runs its first step inside the outer swap.
            if (!NetGate.Host || _parked != null || e == null) return false;
            try
            {
                var target = EnemySyncService.ChaseTargetOf(e);
                if (target == null) return false;
                var host = PlayerState.player;
                if (host == null || host == target) return false;
                _parked = host;
                PlayerState.player = target;
                return true;
            }
            catch (System.Exception ex) { Guard.Swallow(ex); return false; }
        }

        internal static void End(bool began)
        {
            if (!began || _parked == null) return;
            try { PlayerState.player = _parked; }
            catch (System.Exception e) { Guard.Swallow(e); }
            _parked = null;
        }

        [HarmonyPatch(typeof(EnemyController), "Update")]
        public static class UpdatePatch
        {
            [HarmonyPrefix]
            public static void Prefix(EnemyController __instance, out bool __state) => __state = Begin(__instance);

            [HarmonyFinalizer]
            public static System.Exception Finalizer(bool __state, System.Exception __exception)
            {
                End(__state);
                return __exception;
            }
        }

        [HarmonyPatch(typeof(EnemyController._TrackAndAttack_d__151), nameof(EnemyController._TrackAndAttack_d__151.MoveNext))]
        public static class TrackAndAttackPatch
        {
            [HarmonyPrefix]
            public static void Prefix(EnemyController._TrackAndAttack_d__151 __instance, out bool __state) =>
                __state = Begin(__instance.__4__this);

            [HarmonyFinalizer]
            public static System.Exception Finalizer(bool __state, System.Exception __exception)
            {
                End(__state);
                return __exception;
            }
        }

        [HarmonyPatch(typeof(EnemyController._Attack_d__172), nameof(EnemyController._Attack_d__172.MoveNext))]
        public static class AttackPatch
        {
            [HarmonyPrefix]
            public static void Prefix(EnemyController._Attack_d__172 __instance, out bool __state) =>
                __state = Begin(__instance.__4__this);

            [HarmonyFinalizer]
            public static System.Exception Finalizer(bool __state, System.Exception __exception)
            {
                End(__state);
                return __exception;
            }
        }

        [HarmonyPatch(typeof(EnemyController._Hurt_d__164), nameof(EnemyController._Hurt_d__164.MoveNext))]
        public static class HurtPatch
        {
            [HarmonyPrefix]
            public static void Prefix(EnemyController._Hurt_d__164 __instance, out bool __state) =>
                __state = Begin(__instance.__4__this);

            [HarmonyFinalizer]
            public static System.Exception Finalizer(bool __state, System.Exception __exception)
            {
                End(__state);
                return __exception;
            }
        }

        [HarmonyPatch(typeof(EnemyController._Stagger_d__165), nameof(EnemyController._Stagger_d__165.MoveNext))]
        public static class StaggerPatch
        {
            [HarmonyPrefix]
            public static void Prefix(EnemyController._Stagger_d__165 __instance, out bool __state) =>
                __state = Begin(__instance.__4__this);

            [HarmonyFinalizer]
            public static System.Exception Finalizer(bool __state, System.Exception __exception)
            {
                End(__state);
                return __exception;
            }
        }

        [HarmonyPatch(typeof(EnemyController._Critical_d__166), nameof(EnemyController._Critical_d__166.MoveNext))]
        public static class CriticalPatch
        {
            [HarmonyPrefix]
            public static void Prefix(EnemyController._Critical_d__166 __instance, out bool __state) =>
                __state = Begin(__instance.__4__this);

            [HarmonyFinalizer]
            public static System.Exception Finalizer(bool __state, System.Exception __exception)
            {
                End(__state);
                return __exception;
            }
        }

        [HarmonyPatch(typeof(EnemyController._Fire_d__169), nameof(EnemyController._Fire_d__169.MoveNext))]
        public static class FirePatch
        {
            [HarmonyPrefix]
            public static void Prefix(EnemyController._Fire_d__169 __instance, out bool __state) =>
                __state = Begin(__instance.__4__this);

            [HarmonyFinalizer]
            public static System.Exception Finalizer(bool __state, System.Exception __exception)
            {
                End(__state);
                return __exception;
            }
        }
    }
}
