// Native host death (SaveManager.Load) must wipe the client too.
using HarmonyLib;
using SyncRADation.Players;
using SyncRADation.Sync;

namespace SyncRADation.Patches
{
    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.Load))]
    public static class SaveManagerLoadPatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            if (NetGate.IsApplying || !NetGate.Host) return;
            if (!NetworkDamageSystem.HostDying()) return;
            if (!NetworkDamageSystem.TrySendHostWipe()) return;
            PlaytestLog.Event("Damage", "host SaveManager.Load — wipe peers");
        }
    }
}
