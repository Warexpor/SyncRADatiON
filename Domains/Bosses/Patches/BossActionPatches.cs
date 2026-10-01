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
            var net = LanNetworkManager.Instance;
            if (b == null || net == null) return false;
            wid = unchecked((long)net.BossSync.BossId(b));
            return wid != 0;
        }

        /// <summary>
        /// Chimera / Mynah startFightSequence starts the Bossfight coroutine (Ghidra LAB_ChimeraBoss.c,
        /// MED_MynahBoss.c). The fight is host-only: DisableLocalAI halts the client copy once at the first boss
        /// snapshot, but a cutscene / UnityEvent replay that calls startFightSequence later would run the whole
        /// fight again locally (double shots, local phase edges). A client in the host's scene never starts it.
        /// </summary>
        static bool ClientInHostScene()
        {
            if (!NetGate.Client) return false;
            var net = LanNetworkManager.Instance;
            return net != null && !net.SceneMismatch;
        }

        [HarmonyPatch(typeof(LAB_ChimeraBoss), nameof(LAB_ChimeraBoss.startFightSequence))]
        public static class ChimeraStartFightPatch
        {
            [HarmonyPrefix]
            public static bool Prefix()
            {
                if (!ClientInHostScene()) return true;
                PlaytestLog.Event("Boss", "chimera startFightSequence skipped (host runs the fight)");
                return false;
            }
        }

        [HarmonyPatch(typeof(MED_MynahBoss), nameof(MED_MynahBoss.startFightSequence))]
        public static class MynahStartFightPatch
        {
            [HarmonyPrefix]
            public static bool Prefix()
            {
                if (!ClientInHostScene()) return true;
                PlaytestLog.Event("Boss", "mynah startFightSequence skipped (host runs the fight)");
                return false;
            }
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

            /// <summary>
            /// Native Stabbed removes SpearItem from the HOST bag only (first MoveNext, Ghidra END_Boss.c). The
            /// spear usually lives on the party ring / a client bag, so a local host press must also retire it
            /// there (ring drop + bag copy strip + fan-out). Client-requested stabs do the same in
            /// BossSyncService.ApplyHitOnHost, outside the apply gate.
            /// </summary>
            [HarmonyPostfix]
            public static void Postfix(END_Boss __instance)
            {
                if (!NetGate.Host || NetGate.IsApplying) return;
                try
                {
                    var item = __instance != null ? __instance.SpearItem : null;
                    if (item != null) PartyKeyRing.RevokeConsumed(item._item);
                }
                catch (System.Exception ex) { Guard.Swallow(ex); }
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
                // Native takeSpear decrements ammo on EVERY call (Ghidra END_Boss.c), so a repeat for a
                // spear that is already gone would corrupt the count the snapshot mirrors.
                if (NetGate.Host)
                {
                    try { if (spear == null || !spear.activeSelf) return false; }
                    catch (System.Exception e) { Guard.Swallow(e); }
                }
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
                var net = LanNetworkManager.Instance;
                net.BossSync.NoteSpearTaker(idx, net.LocalPlayerId); // no-op when a client request already recorded it
                net.BossHandlers.BroadcastBossEvent(wid, BossHitKind.TakeSpear, idx);
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
                if (!NetGate.Host || !NetGate.Party) return;
                try
                {
                    if (!__instance.gunShot) return;
                    var net = LanNetworkManager.Instance;
                    long wid = unchecked((long)net.BossSync.BossId(__instance));
                    if (wid != 0)
                        net.BossHandlers.BroadcastBossEvent(wid, BossHitKind.ChimeraShot, 0);
                }
                catch (System.Exception ex)
                {
                    ModRuntime.Log?.Warning("[BossSync] chimera shot relay: " + ex.Message);
                }
            }
        }
    }
}
