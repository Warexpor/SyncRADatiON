// Route combat through native EnemyController.TakeDamage; client hits → host.
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Patches
{
    /// <summary>
    /// Host: allow game TakeDamage as usual (AI + local shots).
    /// Client: suppress local HP mutation on puppets; report chances to host.
    /// </summary>
    [HarmonyPatch(typeof(EnemyController))]
    public static class EnemyTakeDamagePatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(EnemyController.TakeDamage), new[] { typeof(float), typeof(float), typeof(float), typeof(bool) })]
        public static bool PrefixChanced(EnemyController __instance, float _fireChance, float _criticalChance, float _hurtChance, bool noSneak)
        {
            return Handle(__instance, _fireChance, _criticalChance, _hurtChance, noSneak);
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(EnemyController.TakeDamage), new System.Type[0])]
        public static bool PrefixSimple(EnemyController __instance)
        {
            float fire = 0f, crit = 0f, hurt = 0f;
            try
            {
                fire = PlayerAttack.fireChance;
                crit = PlayerAttack.criticalChance;
                hurt = PlayerAttack.hurtChance;
            }
            catch { }
            return Handle(__instance, fire, crit, hurt, false);
        }

        private static bool Handle(EnemyController enemy, float fire, float crit, float hurt, bool noSneak)
        {
            if (enemy == null) return true;

            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected)
                return true;

            // Host applies real damage locally (this is the authoritative sim).
            if (net.Role == NetworkRole.Host)
                return true;

            ulong id = WorldId.FromGameObject(enemy.gameObject);
            if (id == 0)
            {
                ModRuntime.Log?.Warning("[Damage] Client hit with WorldId 0: " + enemy.gameObject.name);
                return false;
            }

            net.SendNativeEnemyHit(id, fire, crit, hurt, noSneak);
            return false;
        }
    }
}
