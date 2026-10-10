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
        // Held host snaps per local instance (GetInstanceID, this peer only; the snap was routed by WorldId). One per
        // instance: BOS_Adler has two ADLR Managers, and one shared hold let the asleep one's snap overwrite the live
        // one's every frame (pilot soak: a rejoined client's Adler intensity stuck at 0 while the host's was 1).
        struct KolibriHold { public bool Dead; public int Freq; public float Intensity, Radio; }
        struct AdlerHold { public float Intensity, Progress; }
        static readonly System.Collections.Generic.Dictionary<int, KolibriHold> _kolibri =
            new System.Collections.Generic.Dictionary<int, KolibriHold>();
        static readonly System.Collections.Generic.Dictionary<int, AdlerHold> _adler =
            new System.Collections.Generic.Dictionary<int, AdlerHold>();

        // Client: the KolibriManager whose Update is running right now (held), and the frame it started in.
        static KolibriManager _inUpdate;
        static int _inUpdateFrame = -1;

        public static void Clear()
        {
            _kolibri.Clear();
            _adler.Clear();
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

        public static void HoldKolibri(KolibriManager inst, bool dead, int frequency, float intensity, float radioIntensity)
        {
            if (inst == null || !NetGate.Live || NetGate.Host) return;
            _kolibri[inst.GetInstanceID()] = new KolibriHold
                { Dead = dead, Freq = frequency, Intensity = intensity, Radio = radioIntensity };
        }

        public static void HoldAdler(BOS_Adler inst, float intensity, float progress)
        {
            if (inst == null || !NetGate.Live || NetGate.Host) return;
            _adler[inst.GetInstanceID()] = new AdlerHold { Intensity = intensity, Progress = progress };
        }

        static bool ApplyKolibriHold(KolibriManager inst)
        {
            if (inst == null || !NetGate.Live || NetGate.Host) return false;
            try
            {
                KolibriHold h;
                if (!_kolibri.TryGetValue(inst.GetInstanceID(), out h)) return false;
                inst.dead = h.Dead;
                inst.frequency = h.Freq;
                inst.intensity = h.Intensity;
                inst.radioIntensity = h.Radio;
                return true;
            }
            catch (System.Exception e) { Guard.Swallow("BossAuth.HoldKolibri", e); return false; }
        }

        static void ApplyAdlerHold(BOS_Adler inst)
        {
            if (inst == null || !NetGate.Live || NetGate.Host) return;
            try
            {
                AdlerHold h;
                if (!_adler.TryGetValue(inst.GetInstanceID(), out h)) return;
                inst.intensity = h.Intensity;
                inst.progress = h.Progress;
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
                if (ApplyKolibriHold(__instance) && NetGate.Client)
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
