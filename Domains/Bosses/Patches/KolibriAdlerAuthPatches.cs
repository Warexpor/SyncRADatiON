// Host-authoritative Kolibri/Adler phase fields. Client Update must not clobber snaps.
using HarmonyLib;
using SyncRADation.Sync;

namespace SyncRADation.Patches
{
    /// <summary>
    /// KolibriManager / BOS_Adler stay enabled on clients so glitch/SFX presentation runs,
    /// but their Update recomputes intensity/progress (and Kolibri frequency) from local
    /// Elster/radio. Re-apply the last host PuzzleState snap each frame on clients.
    /// END/Chimera/Mynah are HaltBossController-disabled (StopAllCoroutines + enabled=false) instead (BossSyncService.DisableLocalAI).
    /// </summary>
    public static class KolibriAdlerAuthPatches
    {
        static bool _kolibriHeld;
        static bool _kolibriDead;
        static int _kolibriFreq;
        static float _kolibriIntensity;
        static float _kolibriRadio;

        static bool _adlerHeld;
        static float _adlerIntensity;
        static float _adlerProgress;

        public static void Clear()
        {
            _kolibriHeld = false;
            _adlerHeld = false;
        }

        public static void HoldKolibri(bool dead, int frequency, float intensity, float radioIntensity)
        {
            if (!NetGate.Live || NetGate.Host) return;
            _kolibriHeld = true;
            _kolibriDead = dead;
            _kolibriFreq = frequency;
            _kolibriIntensity = intensity;
            _kolibriRadio = radioIntensity;
        }

        public static void HoldAdler(float intensity, float progress)
        {
            if (!NetGate.Live || NetGate.Host) return;
            _adlerHeld = true;
            _adlerIntensity = intensity;
            _adlerProgress = progress;
        }

        [HarmonyPatch(typeof(KolibriManager), "Update")]
        public static class KolibriUpdateAuthPatch
        {
            [HarmonyPrefix]
            public static void Prefix(KolibriManager __instance)
            {
                if (__instance == null || !_kolibriHeld || !NetGate.Live || NetGate.Host) return;
                try { __instance.dead = _kolibriDead; } catch { }
                try { __instance.frequency = _kolibriFreq; } catch { }
                try { __instance.intensity = _kolibriIntensity; } catch { }
                try { __instance.radioIntensity = _kolibriRadio; } catch { }
            }
        }

        [HarmonyPatch(typeof(BOS_Adler), "Update")]
        public static class AdlerUpdateAuthPatch
        {
            [HarmonyPrefix]
            public static void Prefix(BOS_Adler __instance)
            {
                if (__instance == null || !_adlerHeld || !NetGate.Live || NetGate.Host) return;
                try { __instance.intensity = _adlerIntensity; } catch { }
                try { __instance.progress = _adlerProgress; } catch { }
            }
        }
    }
}
