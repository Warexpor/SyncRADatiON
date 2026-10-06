// Client-side EnemyController side effects that must run on the host sim, plus the host hit window hook.
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;

namespace SyncRADation.Patches
{
    /// <summary>
    /// Puppeted client enemies have their AI disabled and mirror the host snapshot, so a stomp Kill,
    /// push/knockback, burndown or flashlight wake triggered locally would only diverge (or be
    /// overwritten). Forward them to the host, which runs the native method and broadcasts the result.
    /// Host: EnemyController.Hit Prefix feeds the attack-window damage check for remote proxies.
    /// </summary>
    [HarmonyPatch(typeof(EnemyController))]
    public static class EnemyActionPatches
    {
        static bool Forward(EnemyController enemy, EnemyActionKind kind)
        {
            if (enemy == null) return true;
            if (!NetGate.Client || NetGate.IsApplying) return true;
            var net = LanNetworkManager.Instance;
            if (net == null || net.SceneMismatch) return true;
            ulong id;
            if (!WorldRegistry.TryGetEnemyId(enemy, out id) || id == 0) return true;
            net.EnemyHandlers.SendEnemyAction(id, kind);
            return false;
        }

        [HarmonyPrefix, HarmonyPatch(nameof(EnemyController.Kill))]
        public static bool KillPrefix(EnemyController __instance) => Forward(__instance, EnemyActionKind.Kill);

        [HarmonyPrefix, HarmonyPatch(nameof(EnemyController.KillSilent))]
        public static bool KillSilentPrefix(EnemyController __instance) => Forward(__instance, EnemyActionKind.KillSilent);

        [HarmonyPrefix, HarmonyPatch(nameof(EnemyController.Knockback))]
        public static bool KnockbackPrefix(EnemyController __instance) => Forward(__instance, EnemyActionKind.Knockback);

        [HarmonyPrefix, HarmonyPatch(nameof(EnemyController.GetPushed))]
        public static bool GetPushedPrefix(EnemyController __instance) => Forward(__instance, EnemyActionKind.GetPushed);

        [HarmonyPrefix, HarmonyPatch(nameof(EnemyController.burndown))]
        public static bool BurndownPrefix(EnemyController __instance) => Forward(__instance, EnemyActionKind.Burndown);

        [HarmonyPrefix, HarmonyPatch(nameof(EnemyController.WakeUpFlashlight))]
        public static bool WakeFlashlightPrefix(EnemyController __instance) => Forward(__instance, EnemyActionKind.WakeUp);

        /// <summary>
        /// Native Hit (an animation event, outside the EnemyTargetPatch swap) measures and hurts only the host's own
        /// Elster (playerPos.position = PlayerState.player.position, HurtElster); a remote player in reach is hurt
        /// through OnEnemyHit. playerPos itself is never a player root (native code only writes its position).
        /// </summary>
        [HarmonyPrefix, HarmonyPatch(nameof(EnemyController.Hit))]
        public static void HitPrefix(EnemyController __instance)
        {
            if (!NetGate.Host) return;
            ClientDamageService.OnEnemyHit(__instance);
        }
    }
}
