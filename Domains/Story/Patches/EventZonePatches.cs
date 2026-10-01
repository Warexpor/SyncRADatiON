// Client EventZone → host request; host Presentation broadcast. Scene-changed reset hub.
using HarmonyLib;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Patches
{
    [HarmonyPatch(typeof(EventZone), "Update")]
    public static class EventZonePatch
    {
        private static readonly System.Collections.Generic.HashSet<ulong> _fired
            = new System.Collections.Generic.HashSet<ulong>();

        public static void OnSceneChanged()
        {
            _fired.Clear();
            _lastRequest.Clear();
            _kind.Clear();
            _handled.Clear();
            KeypadPress.Clear();
            UseItemInteractionPatch.OnSceneChanged();
            CutsceneSync.OnSceneChanged();
            AirlockCinematic.Reset();
        }

        public static void MarkFired(ulong id)
        {
            if (id != 0) _fired.Add(id);
        }

        public static bool MarkMultiOnce(ulong id) => id != 0 && _fired.Add(id ^ 0x9E3779B97F4A7C15UL);

        private static readonly System.Collections.Generic.Dictionary<ulong, float> _lastRequest
            = new System.Collections.Generic.Dictionary<ulong, float>();

        // Per-instance, per-scene caches (EventZone.Update runs every frame for every zone): the LocalInspect ancestor
        // walks are structural, and a triggered zone the host already handled is skipped by instance id first.
        const byte KindAirlock = 1, KindLock = 2, KindParty = 3;
        private static readonly System.Collections.Generic.Dictionary<int, byte> _kind
            = new System.Collections.Generic.Dictionary<int, byte>();
        private static readonly System.Collections.Generic.HashSet<int> _handled
            = new System.Collections.Generic.HashSet<int>();

        static byte KindOf(EventZone z)
        {
            int key = z.GetInstanceID();
            byte k;
            if (_kind.TryGetValue(key, out k)) return k;
            k = KindParty;
            if (LocalInspect.AirlockCinematic(z.gameObject)) k = KindAirlock;
            else if (LocalInspect.LockWorld(z.gameObject)) k = KindLock;
            else
            {
                try
                {
                    if (z.inter != null && LocalInspect.LockWorld(z.inter.gameObject))
                        k = KindLock;
                }
                catch (System.Exception e) { Guard.Swallow(e); }
            }
            _kind[key] = k;
            return k;
        }

        [HarmonyPrefix]
        public static bool Prefix(EventZone __instance)
        {
            // The host always runs native Update (keycard-slot / lock zones and its own party zones alike).
            if (NetGate.IsApplying || !NetGate.Party || NetGate.Host) return true;
            if (__instance == null || __instance.triggered) return true;

            // Out of range the native Update only checks gameState: nothing to gate.
            bool inRange = false;
            try { inRange = __instance.inter != null && __instance.inter.inRange; } catch (System.Exception e) { Guard.Swallow(e); }
            if (!inRange) return true;

            byte kind = KindOf(__instance);
            // Client skips so airlock titles stay local and are not remoted as a party EventZone.
            if (kind == KindAirlock) return false;
            if (kind == KindLock) return true;
            // Wreck / hole split: the host has no such zone, so the request would never fire it.
            if (AirlockCinematic.ClientSplitFromHost()) return true;

            try
            {
                ulong id = WorldId.FromGameObject(__instance.gameObject);
                if (id != 0 && _fired.Contains(id)) return false;
                float last;
                if (_lastRequest.TryGetValue(id, out last) && Time.unscaledTime - last < 0.25f)
                    return false;
                _lastRequest[id] = Time.unscaledTime;
                LanNetworkManager.Instance.SendInteractionRequest(id, InteractionKind.EventZone);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return false;
        }

        [HarmonyPostfix]
        public static void Postfix(EventZone __instance)
        {
            if (!NetGate.Host || NetGate.IsApplying || !NetGate.Party) return;
            if (__instance == null || !__instance.triggered) return;
            if (!_handled.Add(__instance.GetInstanceID())) return;
            if (KindOf(__instance) == KindAirlock) return;
            if (LocalInspect.LockWorld(__instance.gameObject)) return;
            ulong id = WorldId.FromGameObject(__instance.gameObject);
            if (!_fired.Add(id)) return;
            // Host ran onInRange natively in its own room: it counted any END effects.
            LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.EventZoneFire, id, 0, StoryWire.HostCounted);
        }
    }
}
