// World FMOD hooks: StudioEventEmitter Play/Stop relay and the world one-shot entry points (one shared handler).
using FMODUnity;
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Patches
{
    [HarmonyPatch(typeof(StudioEventEmitter), nameof(StudioEventEmitter.Play))]
    public static class StudioEmitterPlayPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(StudioEventEmitter __instance, out bool __state)
        {
            // Native Play returns at once for a TriggerOnce emitter that already fired or a blank Event
            // (FMODUnity StudioEventEmitter.Play): nothing sounds, so nothing is relayed.
            __state = WillPlay(__instance);
            // Client copy of the Kolibri feedback hurt: the host plays and relays the real one.
            if (KolibriAdlerAuthPatches.SuppressClientEmitter(__instance))
                return false;
            // Remote door apply runs native open/close, which Play() the door emitters ungated; DoorNative plays
            // the distance-gated copy instead.
            if (NetGate.IsApplying && FmodEmitterSync.IsDoorEmitter(__instance))
                return false;
            // A remote action applied here (puzzle onSolved, interaction) must not sound in another room: the
            // cryo OverridePanel_Solved emitter sits on a far-off 3D event-screen stage and plays 2D, so the
            // host heard the client's solve from across the ship. Postfix still relays it on the host.
            if (NetGate.IsApplying && !FmodEmitterSync.AudibleHere(__instance != null ? __instance.gameObject : null))
                return false;
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(StudioEventEmitter __instance, bool __state)
        {
            if (!__state) return;
            // Harmony runs postfixes after a skipping prefix too: a suppressed Play is not forwarded either.
            if (KolibriAdlerAuthPatches.SuppressClientEmitter(__instance)) return;
            FmodEmitterSync.EmitterChanged(__instance, true);
        }

        static bool WillPlay(StudioEventEmitter e)
        {
            if (e == null) return false;
            try { return !(e.TriggerOnce && e.hasTriggered) && !string.IsNullOrWhiteSpace(e.Event); }
            catch (System.Exception ex) { Guard.Swallow(ex); return true; }
        }
    }

    [HarmonyPatch(typeof(StudioEventEmitter), nameof(StudioEventEmitter.Stop))]
    public static class StudioEmitterStopPatch
    {
        [HarmonyPrefix]
        public static void Prefix(StudioEventEmitter __instance, out bool __state)
        {
            __state = false;
            if (!NetGate.Host || !NetGate.Party || __instance == null) return;
            try { __state = __instance.IsPlaying(); }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        [HarmonyPostfix]
        public static void Postfix(StudioEventEmitter __instance, bool __state)
        {
            FmodEmitterSync.EmitterChanged(__instance, false, __state);
        }
    }

    /// <summary>
    /// World one-shot hook. RuntimeManager.PlayOneShot(string, Vector3) is the only one-shot entry point the game
    /// calls (PlayOneShot(Guid), both PlayOneShotAttached overloads and fmod.PlayOneShot have no caller in game code,
    /// and PlayOneShot(string) inlines CreateInstance instead of calling the Guid overload). Prefix: drop door
    /// one-shots during a remote apply (DoorNative plays the gated copy), then the replayed-puzzle-press gate.
    /// Postfix (Harmony runs it after a skipping prefix too): the host relay, which applies its own skip rules.
    /// The path may be a "{guid}" string: both sides resolve it to the event path first.
    /// </summary>
    [HarmonyPatch(typeof(RuntimeManager), nameof(RuntimeManager.PlayOneShot), new[] { typeof(string), typeof(Vector3) })]
    public static class PlayOneShotWorldPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(string path)
        {
            path = FmodEmitterSync.NormalizePath(path);
            if (FmodEmitterSync.ShouldBlockDoorOneShot(path))
                return false;
            return PuzzleFx.RemoteOneShot(path);
        }

        [HarmonyPostfix]
        public static void Postfix(string path, Vector3 position) => FmodEmitterSync.TryHostWorldOneShot(path, position);
    }
}
