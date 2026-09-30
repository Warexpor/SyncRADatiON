// Host-authoritative Kolibri/Adler phase fields. Client Update must not clobber snaps.
using HarmonyLib;
using SyncRADation.Sync;

namespace SyncRADation.Patches
{
    /// <summary>
    /// KolibriManager / BOS_Adler stay enabled on clients so glitch/SFX presentation runs,
    /// but their Update recomputes intensity/progress (and Kolibri frequency) from local
    /// Elster/radio. Re-apply the last host PuzzleState snap each frame on clients —
    /// Prefix feeds inputs Update may read; Postfix wins after Update writes back.
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

        static void ApplyKolibriHold(KolibriManager inst)
        {
            if (inst == null || !_kolibriHeld || !NetGate.Live || NetGate.Host) return;
            try { inst.dead = _kolibriDead; } catch (System.Exception e) { Guard.Swallow(e); }
            try { inst.frequency = _kolibriFreq; } catch (System.Exception e) { Guard.Swallow(e); }
            try { inst.intensity = _kolibriIntensity; } catch (System.Exception e) { Guard.Swallow(e); }
            try { inst.radioIntensity = _kolibriRadio; } catch (System.Exception e) { Guard.Swallow(e); }
        }

        static void ApplyAdlerHold(BOS_Adler inst)
        {
            if (inst == null || !_adlerHeld || !NetGate.Live || NetGate.Host) return;
            try { inst.intensity = _adlerIntensity; } catch (System.Exception e) { Guard.Swallow(e); }
            try { inst.progress = _adlerProgress; } catch (System.Exception e) { Guard.Swallow(e); }
        }

        [HarmonyPatch(typeof(KolibriManager), "Update")]
        public static class KolibriUpdateAuthPatch
        {
            [HarmonyPrefix]
            public static void Prefix(KolibriManager __instance) => ApplyKolibriHold(__instance);

            [HarmonyPostfix]
            public static void Postfix(KolibriManager __instance) => ApplyKolibriHold(__instance);
        }

        [HarmonyPatch(typeof(BOS_Adler), "Update")]
        public static class AdlerUpdateAuthPatch
        {
            [HarmonyPrefix]
            public static void Prefix(BOS_Adler __instance) => ApplyAdlerHold(__instance);

            [HarmonyPostfix]
            public static void Postfix(BOS_Adler __instance) => ApplyAdlerHold(__instance);
        }
    }
}
