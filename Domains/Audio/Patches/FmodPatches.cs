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
        public static bool Prefix(StudioEventEmitter __instance)
        {
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

    /// <summary>
    /// Shared body of every world one-shot hook. Prefix: drop door one-shots during a remote apply (DoorNative plays
    /// the gated copy), then the per-entry-point gate. Postfix (Harmony runs it after a skipping prefix too): the
    /// host relay, which applies its own skip rules.
    /// </summary>
    static class WorldOneShot
    {
        internal enum Gate
        {
            /// <summary>At a position: a replayed puzzle press plays 3D at its panel unless this player views it.</summary>
            Positional,
            /// <summary>Attached to an object: a remote action applied here plays only in this player's room.</summary>
            Attached,
            /// <summary>No position (fmod helper): door rule only.</summary>
            Global
        }

        internal static bool Before(string path, Gate gate, GameObject attached)
        {
            if (FmodEmitterSync.ShouldBlockDoorOneShot(path))
                return false;
            switch (gate)
            {
                case Gate.Positional: return PuzzleFx.RemoteOneShot(path);
                case Gate.Attached: return !NetGate.IsApplying || FmodEmitterSync.AudibleHere(attached);
                default: return true;
            }
        }

        internal static void After(string path, Vector3 position)
        {
            FmodEmitterSync.TryHostWorldOneShot(path, position);
        }

        /// <summary>
        /// Guid entry points: the lookupPath round trip only when something will read the path (a remote apply, a
        /// replayed puzzle press, or a host with peers to relay to).
        /// </summary>
        internal static string PathOf(Il2CppSystem.Guid guid)
        {
            if (!NetGate.IsApplying && !PuzzleFx.Active && !(NetGate.Host && NetGate.Party)) return null;
            return FmodEmitterSync.PathFromGuid(guid);
        }

        internal static Vector3 PositionOf(GameObject go)
        {
            try
            {
                if (go != null) return go.transform.position;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return Vector3.zero;
        }

        internal static Vector3 PlayerPosition()
        {
            try
            {
                var player = PlayerState.player;
                if (player != null) return player.transform.position;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return Vector3.zero;
        }
    }

    [HarmonyPatch(typeof(RuntimeManager), nameof(RuntimeManager.PlayOneShot), new[] { typeof(string), typeof(Vector3) })]
    public static class PlayOneShotWorldPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(string path) => WorldOneShot.Before(path, WorldOneShot.Gate.Positional, null);

        [HarmonyPostfix]
        public static void Postfix(string path, Vector3 position) => WorldOneShot.After(path, position);
    }

    [HarmonyPatch(typeof(RuntimeManager), nameof(RuntimeManager.PlayOneShot), new[] { typeof(Il2CppSystem.Guid), typeof(Vector3) })]
    public static class PlayOneShotGuidWorldPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Il2CppSystem.Guid guid, out string __state)
        {
            __state = WorldOneShot.PathOf(guid);
            return WorldOneShot.Before(__state, WorldOneShot.Gate.Positional, null);
        }

        [HarmonyPostfix]
        public static void Postfix(Il2CppSystem.Guid guid, Vector3 position, string __state)
            => WorldOneShot.After(__state ?? WorldOneShot.PathOf(guid), position);
    }

    [HarmonyPatch(typeof(RuntimeManager), nameof(RuntimeManager.PlayOneShotAttached), new[] { typeof(string), typeof(GameObject) })]
    public static class PlayOneShotAttachedStringPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(string path, GameObject gameObject)
            => WorldOneShot.Before(path, WorldOneShot.Gate.Attached, gameObject);

        [HarmonyPostfix]
        public static void Postfix(string path, GameObject gameObject)
            => WorldOneShot.After(path, WorldOneShot.PositionOf(gameObject));
    }

    [HarmonyPatch(typeof(RuntimeManager), nameof(RuntimeManager.PlayOneShotAttached), new[] { typeof(Il2CppSystem.Guid), typeof(GameObject) })]
    public static class PlayOneShotAttachedGuidPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Il2CppSystem.Guid guid, GameObject gameObject, out string __state)
        {
            __state = WorldOneShot.PathOf(guid);
            return WorldOneShot.Before(__state, WorldOneShot.Gate.Attached, gameObject);
        }

        [HarmonyPostfix]
        public static void Postfix(Il2CppSystem.Guid guid, GameObject gameObject, string __state)
            => WorldOneShot.After(__state ?? WorldOneShot.PathOf(guid), WorldOneShot.PositionOf(gameObject));
    }

    [HarmonyPatch(typeof(fmod), nameof(fmod.PlayOneShot), new[] { typeof(string) })]
    public static class FmodPlayOneShotPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(string _event) => WorldOneShot.Before(_event, WorldOneShot.Gate.Global, null);

        [HarmonyPostfix]
        public static void Postfix(string _event) => WorldOneShot.After(_event, WorldOneShot.PlayerPosition());
    }
}
