// Falke (END_Boss) Stab / takeSpear and LAB Chimera rifle presentation across peers.
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Patches
{
    /// <summary>
    /// StabInteraction and the spear pickups are plain Interaction.trigger -> END_Boss.Stab / takeSpear
    /// UnityEvent calls (Ghidra END_Boss.c). The client's Falke controller is halted, so a local press
    /// would do nothing for anyone: forward it to the host, which runs the native method; the resulting
    /// stage/ammo/state arrive through the boss snapshot. Host takeSpear also relays the pickup hide
    /// (PickupSpears active flags are not in the snapshot) so each spear exists once for everybody.
    /// </summary>
    public static class BossActionPatches
    {
        static bool TryBossId(END_Boss b, out long wid)
        {
            wid = 0;
            if (b == null) return false;
            try
            {
                wid = unchecked((long)WorldId.FromGameObject(b.gameObject));
                return wid != 0;
            }
            catch { return false; }
        }

        static bool ClientForward(END_Boss b)
        {
            if (!NetGate.Client || NetGate.IsApplying) return false;
            var net = LanNetworkManager.Instance;
            return net != null && !net.SceneMismatch;
        }

        [HarmonyPatch(typeof(END_Boss), nameof(END_Boss.Stab))]
        public static class StabPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(END_Boss __instance)
            {
                if (!ClientForward(__instance)) return true;
                long wid;
                if (!TryBossId(__instance, out wid)) return true;
                LanNetworkManager.Instance.BossHandlers.SendBossHitToHost(wid, BossHitKind.Stab, 0);
                return false;
            }
        }

        [HarmonyPatch(typeof(END_Boss), nameof(END_Boss.takeSpear))]
        public static class TakeSpearPatch
        {
            static int IndexOf(END_Boss b, GameObject spear)
            {
                try
                {
                    var arr = b.PickupSpears;
                    if (arr == null || spear == null) return -1;
                    for (int i = 0; i < arr.Length; i++)
                        if (arr[i] != null && arr[i] == spear) return i;
                }
                catch { return -1; }
                return -1;
            }

            [HarmonyPrefix]
            public static bool Prefix(END_Boss __instance, GameObject spear)
            {
                if (!ClientForward(__instance)) return true;
                long wid;
                int idx = IndexOf(__instance, spear);
                if (idx < 0 || !TryBossId(__instance, out wid)) return true;
                LanNetworkManager.Instance.BossHandlers.SendBossHitToHost(wid, BossHitKind.TakeSpear, idx);
                return false;
            }

            [HarmonyPostfix]
            public static void Postfix(END_Boss __instance, GameObject spear)
            {
                if (!NetGate.Host) return;
                long wid;
                int idx = IndexOf(__instance, spear);
                if (idx < 0 || !TryBossId(__instance, out wid)) return;
                LanNetworkManager.Instance.BossHandlers.BroadcastBossEvent(wid, BossHitKind.TakeSpear, idx);
            }
        }

        /// <summary>
        /// Host Bossfight sets gunShot=true then LateUpdate consumes it the same frame, so the 15 Hz boss
        /// snapshot can never carry it. Relay the edge; client LateUpdate (kept enabled) plays the effects.
        /// </summary>
        [HarmonyPatch(typeof(LAB_ChimeraBoss), "LateUpdate")]
        public static class ChimeraLateUpdatePatch
        {
            [HarmonyPrefix]
            public static void Prefix(LAB_ChimeraBoss __instance)
            {
                if (!NetGate.Host) return;
                try
                {
                    if (!__instance.gunShot) return;
                    long wid = unchecked((long)WorldId.FromGameObject(__instance.gameObject));
                    if (wid != 0)
                        LanNetworkManager.Instance.BossHandlers.BroadcastBossEvent(wid, BossHitKind.ChimeraShot, 0);
                }
                catch (System.Exception ex)
                {
                    ModRuntime.Log?.Warning("[BossSync] chimera shot relay: " + ex.Message);
                }
            }
        }
    }
}
