// Host: a grenade (GrenadeExplosion prefab, the only runtime-instantiated Hurtbox prefab) came alive.
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;

namespace SyncRADation.Patches
{
    /// <summary>
    /// Grenade.OnEnable starts the Die coroutine that opens MainHurtbox then Hurtbox for two fixed steps each
    /// (Ghidra Grenade.c). ClientDamageService only knows hurtboxes from its last scan, so it rescans now and the
    /// blast can reach remote players. Unique RVA (0x600960, rva_fold_scan).
    /// </summary>
    [HarmonyPatch(typeof(Grenade), nameof(Grenade.OnEnable))]
    public static class GrenadeSpawnPatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            if (!NetGate.Host) return;
            ClientDamageService.NoteSpawn();
        }
    }
}
