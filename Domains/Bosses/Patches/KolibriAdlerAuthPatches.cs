// Host-authoritative Kolibri/Adler phase fields. Client Update must not clobber snaps.
using FMODUnity;
using HarmonyLib;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Patches
{
    /// <summary>
    /// KolibriManager / BOS_Adler stay enabled on clients so glitch/SFX presentation runs,
    /// but their Update recomputes intensity/progress (and Kolibri frequency) from local
    /// Elster/radio. Re-apply the last host PuzzleState snap each frame on clients:
    /// Prefix feeds inputs Update may read; Postfix wins after Update writes back.
    /// END/Chimera/Mynah are halted instead (BossSyncService.DisableLocalAI).
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

        // Client: the KolibriManager whose Update is running right now (held), and the frame it started in.
        static KolibriManager _inUpdate;
        static int _inUpdateFrame = -1;

        public static void Clear()
        {
            _kolibriHeld = false;
            _adlerHeld = false;
            _inUpdate = null;
            _inUpdateFrame = -1;
        }

        /// <summary>
        /// Client inside a held KolibriManager.Update. Its feedback branch (Ghidra KolibriManager.c Update:
        /// Real.hitbox.HP -= stepdamage; Real.TakeDamage(0,0,100); HurtSFX.Play()) runs off the host's held
        /// radioIntensity, so it is the host's hit replayed: TakeDamage must not be forwarded and HurtSFX comes
        /// from the host's relay.
        /// </summary>
        public static bool InClientKolibriUpdate
            => _inUpdate != null && _inUpdateFrame == Time.frameCount;

        /// <summary>StudioEventEmitter.Play on the client during a held Kolibri Update: the host's HurtSFX relay covers it.</summary>
        public static bool SuppressClientEmitter(StudioEventEmitter e)
        {
            if (e == null || !InClientKolibriUpdate) return false;
            try { return _inUpdate.HurtSFX != null && _inUpdate.HurtSFX == e; }
            catch (System.Exception ex) { Guard.Swallow(ex); return false; }
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
            try
            {
                inst.dead = _kolibriDead;
                inst.frequency = _kolibriFreq;
                inst.intensity = _kolibriIntensity;
                inst.radioIntensity = _kolibriRadio;
            }
            catch (System.Exception e) { Guard.Swallow("BossAuth.HoldKolibri", e); }
        }

        static void ApplyAdlerHold(BOS_Adler inst)
        {
            if (inst == null || !_adlerHeld || !NetGate.Live || NetGate.Host) return;
            try
            {
                inst.intensity = _adlerIntensity;
                inst.progress = _adlerProgress;
            }
            catch (System.Exception e) { Guard.Swallow("BossAuth.HoldAdler", e); }
        }

        [HarmonyPatch(typeof(KolibriManager), "Update")]
        public static class KolibriUpdateAuthPatch
        {
            [HarmonyPrefix]
            public static void Prefix(KolibriManager __instance)
            {
                _inUpdate = null;
                ApplyKolibriHold(__instance);
                if (_kolibriHeld && NetGate.Client)
                {
                    _inUpdate = __instance;
                    _inUpdateFrame = Time.frameCount;
                }
            }

            [HarmonyPostfix]
            public static void Postfix(KolibriManager __instance)
            {
                _inUpdate = null;
                ApplyKolibriHold(__instance);
            }
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
