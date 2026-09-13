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
            if (NetGate.IsApplying && FmodEmitterSync.IsDoorEmitter(__instance))
                return false;
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(StudioEventEmitter __instance)
        {
            FmodEmitterSync.HostEmit(__instance, true);
        }
    }

    [HarmonyPatch(typeof(StudioEventEmitter), nameof(StudioEventEmitter.Stop))]
    public static class StudioEmitterStopPatch
    {
        [HarmonyPostfix]
        public static void Postfix(StudioEventEmitter __instance)
        {
            FmodEmitterSync.HostEmit(__instance, false);
        }
    }

    [HarmonyPatch(typeof(RuntimeManager), nameof(RuntimeManager.PlayOneShot), new[] { typeof(string), typeof(Vector3) })]
    public static class PlayOneShotWorldPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(string path, Vector3 position)
        {
            if (NetGate.IsApplying && FmodEmitterSync.IsDoorSfxPath(path))
                return false;
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(string path, Vector3 position)
        {
            if (NetGate.IsApplying || !NetGate.Host) return;
            if (FmodEmitterSync.IsLocalOneShot(path)) return;
            if (FmodEmitterSync.IsDoorSfxPath(path)) return;
            var player = PlayerState.player;
            if (player != null && (player.transform.position - position).sqrMagnitude < 4f)
                return;
            FmodEmitterSync.HostOneShot(path, position);
        }
    }
}
