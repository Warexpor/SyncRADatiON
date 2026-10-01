// Route combat through native EnemyController.TakeDamage; client hits → host.
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Patches
{
    /// <summary>
    /// Host: allow game TakeDamage as usual (AI + local shots).
    /// Client: roll back the local HP drop on puppets; report the damage + chances to the host.
    /// </summary>
    [HarmonyPatch(typeof(EnemyController))]
    public static class EnemyTakeDamagePatches
    {
        // Warn-once set (persistent on purpose): one warning per enemy instance instead of one per shot.
        private static readonly System.Collections.Generic.HashSet<int> _warnedUnmapped
            = new System.Collections.Generic.HashSet<int>();

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
            catch (System.Exception e) { Guard.Swallow(e); }
            // Native 0-arg TakeDamage always forwards noSneak=true (Ghidra EnemyController.c TakeDamage()).
            return Handle(__instance, fire, crit, hurt, true);
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

            // Personal scene (wreck/hole, airlock): this client's enemies are not host puppets, so
            // the host cannot resolve their WorldIds. Run native TakeDamage or they are invulnerable.
            if (net.SceneMismatch)
                return true;

            // Registry reverse map first (the id the host knows this enemy by), else the WorldId pinned at scene load.
            ulong id;
            if (!WorldRegistry.TryGetEnemyId(enemy, out id))
            {
                id = WorldId.FromGameObject(enemy.gameObject);
                if (id == 0 && _warnedUnmapped.Add(enemy.GetInstanceID()))
                    ModRuntime.Log?.Warning("[Damage] Client hit with WorldId 0: " + enemy.gameObject.name);
            }
            if (id == 0)
                return false;

            // The local hit already lowered the puppet's Hitbox.HP (PlayerAttack / Kolibri feedback do it before
            // TakeDamage): take that amount back so only the host's snapshot moves HP, and send it along.
            int damage = net.EnemySync.TakeLocalHitDelta(id, enemy);

            // Client KolibriManager.Update runs on the host's held radioIntensity, so its feedback branch hurts the
            // Kolibri here too. The host's own Update already does that hit: drop the copy.
            if (KolibriAdlerAuthPatches.InClientKolibriUpdate)
                return false;

            net.SendNativeEnemyHit(id, damage, fire, crit, hurt, noSneak);
            return false;
        }
    }
}
