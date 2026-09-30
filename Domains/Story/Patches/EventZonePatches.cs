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
            ClientKeypad.OnSceneChanged();
            UseItemInteractionPatch.OnSceneChanged();
            InteractionSyncService.OnSceneChanged();
            AirlockCinematic.Reset();
        }

        public static void MarkFired(ulong id)
        {
            if (id != 0) _fired.Add(id);
        }

        public static bool MarkMultiOnce(ulong id) => id != 0 && _fired.Add(id ^ 0x9E3779B97F4A7C15UL);

        public static bool MarkMultiTrigger(ulong id) => id != 0 && _fired.Add(id ^ 0xC2B2AE3D27D4EB4FUL);

        private static readonly System.Collections.Generic.Dictionary<ulong, float> _lastRequest
            = new System.Collections.Generic.Dictionary<ulong, float>();

        [HarmonyPrefix]
        public static bool Prefix(EventZone __instance)
        {
            if (NetGate.IsApplying || !NetGate.Party) return true;
            if (__instance == null || __instance.triggered) return true;
            // Host still runs native (keycard slot EventZones). Client skips so
            // airlock titles stay local and are not remoted as a party EventZone.
            if (LocalInspect.AirlockCinematic(__instance.gameObject))
                return NetGate.Host;
            if (LocalInspect.LockWorld(__instance.gameObject)) return true;
            try
            {
                if (__instance.inter != null && LocalInspect.LockWorld(__instance.inter.gameObject))
                    return true;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            if (NetGate.Host) return true;

            bool inRange = false;
            try { inRange = __instance.inter != null && __instance.inter.inRange; } catch (System.Exception e) { Guard.Swallow(e); }
            if (!inRange) return true;

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
            if (LocalInspect.AirlockCinematic(__instance.gameObject)) return;
            if (LocalInspect.LockWorld(__instance.gameObject)) return;
            ulong id = WorldId.FromGameObject(__instance.gameObject);
            if (!_fired.Add(id)) return;
            // Host ran onInRange natively in its own room: it counted any END effects.
            LanNetworkManager.Instance.StorySync.BroadcastPresentation(StoryCmd.EventZoneFire, id, 0, StoryWire.HostCounted);
        }
    }
}
