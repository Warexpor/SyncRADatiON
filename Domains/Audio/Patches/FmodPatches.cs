// Relay world FMOD emitters; skip local Elster and radio UI.
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
        // Remote door apply calls native openDoors/closeDoors which Play() the
        // door emitters ungated. Skip those; DoorNative.PlayWorld distance-gates.
        [HarmonyPrefix]
        public static bool Prefix(StudioEventEmitter __instance)
        {
            // Client copy of the Kolibri feedback hurt: the host plays and relays the real one.
            if (KolibriAdlerAuthPatches.SuppressClientEmitter(__instance))
                return false;
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
        public static void Postfix(StudioEventEmitter __instance)
        {
            // Harmony runs postfixes after a skipping prefix too: a suppressed Play is not forwarded either.
            if (KolibriAdlerAuthPatches.SuppressClientEmitter(__instance)) return;
            FmodEmitterSync.EmitterChanged(__instance, true);
        }
    }

    [HarmonyPatch(typeof(StudioEventEmitter), nameof(StudioEventEmitter.Stop))]
    public static class StudioEmitterStopPatch
    {
        [HarmonyPostfix]
        public static void Postfix(StudioEventEmitter __instance)
        {
            FmodEmitterSync.EmitterChanged(__instance, false);
        }
    }

    [HarmonyPatch(typeof(RuntimeManager), nameof(RuntimeManager.PlayOneShot), new[] { typeof(string), typeof(Vector3) })]
    public static class PlayOneShotWorldPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(string path, Vector3 position)
        {
            if (FmodEmitterSync.ShouldBlockDoorOneShot(path))
                return false;
            // A peer's puzzle press replayed here: 3D at the panel unless this player is looking at it.
            return PuzzleFx.RemoteOneShot(path);
        }

        [HarmonyPostfix]
        public static void Postfix(string path, Vector3 position)
        {
            FmodEmitterSync.TryHostWorldOneShot(path, position);
        }
    }

    [HarmonyPatch(typeof(RuntimeManager), nameof(RuntimeManager.PlayOneShot), new[] { typeof(Il2CppSystem.Guid), typeof(Vector3) })]
    public static class PlayOneShotGuidWorldPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Il2CppSystem.Guid guid, Vector3 position)
        {
            string path = FmodEmitterSync.PathFromGuid(guid);
            if (FmodEmitterSync.ShouldBlockDoorOneShot(path))
                return false;
            return PuzzleFx.RemoteOneShot(path);
        }

        [HarmonyPostfix]
        public static void Postfix(Il2CppSystem.Guid guid, Vector3 position)
        {
            FmodEmitterSync.TryHostWorldOneShot(FmodEmitterSync.PathFromGuid(guid), position);
        }
    }

    [HarmonyPatch(typeof(RuntimeManager), nameof(RuntimeManager.PlayOneShotAttached), new[] { typeof(string), typeof(GameObject) })]
    public static class PlayOneShotAttachedStringPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(string path, GameObject gameObject)
        {
            if (FmodEmitterSync.ShouldBlockDoorOneShot(path))
                return false;
            if (NetGate.IsApplying && !FmodEmitterSync.AudibleHere(gameObject))
                return false;
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(string path, GameObject gameObject)
        {
            Vector3 pos = AttachedPos(gameObject);
            FmodEmitterSync.TryHostWorldOneShot(path, pos);
        }

        static Vector3 AttachedPos(GameObject go)
        {
            try
            {
                if (go != null) return go.transform.position;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return Vector3.zero;
        }
    }

    [HarmonyPatch(typeof(RuntimeManager), nameof(RuntimeManager.PlayOneShotAttached), new[] { typeof(Il2CppSystem.Guid), typeof(GameObject) })]
    public static class PlayOneShotAttachedGuidPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Il2CppSystem.Guid guid, GameObject gameObject)
        {
            string path = FmodEmitterSync.PathFromGuid(guid);
            if (FmodEmitterSync.ShouldBlockDoorOneShot(path))
                return false;
            if (NetGate.IsApplying && !FmodEmitterSync.AudibleHere(gameObject))
                return false;
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(Il2CppSystem.Guid guid, GameObject gameObject)
        {
            Vector3 pos = Vector3.zero;
            try
            {
                if (gameObject != null) pos = gameObject.transform.position;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            FmodEmitterSync.TryHostWorldOneShot(FmodEmitterSync.PathFromGuid(guid), pos);
        }
    }

    [HarmonyPatch(typeof(fmod), nameof(fmod.PlayOneShot), new[] { typeof(string) })]
    public static class FmodPlayOneShotPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(string _event)
        {
            if (FmodEmitterSync.ShouldBlockDoorOneShot(_event))
                return false;
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(string _event)
        {
            Vector3 pos = Vector3.zero;
            try
            {
                var player = PlayerState.player;
                if (player != null) pos = player.transform.position;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            FmodEmitterSync.TryHostWorldOneShot(_event, pos);
        }
    }
}
