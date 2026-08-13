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
        [HarmonyPostfix]
        public static void Postfix(string path, Vector3 position)
        {
            if (NetGate.IsApplying || !NetGate.Host) return;
            if (FmodEmitterSync.IsLocalOneShot(path)) return;
            var player = PlayerState.player;
            if (player != null && (player.transform.position - position).sqrMagnitude < 4f)
                return;
            FmodEmitterSync.HostOneShot(path, position);
        }
    }
}
