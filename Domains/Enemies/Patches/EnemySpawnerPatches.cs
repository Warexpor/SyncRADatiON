// Gate native EnemySpawner: clients never Instantiate; host owns spawn + registry.
// Decompile: EnemySpawner.FixedUpdate proximity Instantiates into _Child (dump.cs ~454504).
using HarmonyLib;
using SyncRADation.Cheats;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Patches
{
    [HarmonyPatch(typeof(EnemySpawner), "FixedUpdate")]
    public static class EnemySpawnerPatches
    {
        public struct PrefixState
        {
            public bool Swapped;
            public GameObject OriginalPlayer;
        }

        [HarmonyPrefix]
        public static bool Prefix(EnemySpawner __instance, ref PrefixState __state)
        {
            __state = default;
            if (__instance == null) return true;
            if (!NetGate.Live) return true;

            // Client must not spawn — dual Instantiates yield divergent WorldIds / map misses.
            // Exception: personal scene (wreck/hole, airlock). Host snapshots never map there, so the
            // client's spawners must run natively or the room stays empty.
            if (NetGate.Client)
            {
                var clientNet = LanNetworkManager.Instance;
                if (clientNet != null && clientNet.SceneMismatch) return true;
                return false;
            }

            // Host FixedUpdate distances against Player (local Elster). When only a peer is
            // in radius, point Player at that proxy for this tick so the spawner still fires.
            TryRedirectPlayerForPeers(__instance, ref __state);
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(EnemySpawner __instance, PrefixState __state)
        {
            if (__state.Swapped && __instance != null)
            {
                try { __instance.Player = __state.OriginalPlayer; }
                catch (System.Exception e) { Guard.Swallow(e); }
            }

            if (!NetGate.Host || __instance == null) return;
            TryAdoptNativeChild(__instance);
        }

        static void TryRedirectPlayerForPeers(EnemySpawner spawner, ref PrefixState state)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || net.ProxyManager == null) return;

            Vector3 origin;
            float radius;
            try
            {
                origin = spawner.transform.position;
                radius = spawner.radius;
            }
            catch { return; }
            if (radius <= 0f) return;
            float r2 = radius * radius;

            GameObject local = null;
            try { local = net.GetLocalPlayer(); } catch (System.Exception e) { Guard.Swallow(e); }
            if (local == null)
            {
                try { local = spawner.Player; } catch (System.Exception e) { Guard.Swallow(e); }
            }

            bool localInRange = false;
            if (local != null)
            {
                try { localInRange = (local.transform.position - origin).sqrMagnitude <= r2; }
                catch (System.Exception e) { Guard.Swallow(e); }
            }
            if (localInRange) return;

            GameObject best = null;
            float bestDist = r2;
            foreach (int pid in net.GetRemotePlayerIds())
            {
                var proxy = net.ProxyManager.GetProxy(pid);
                if (proxy == null || proxy.GameObject == null) continue;
                float d;
                try { d = (proxy.GameObject.transform.position - origin).sqrMagnitude; }
                catch { continue; }
                if (d <= bestDist)
                {
                    bestDist = d;
                    best = proxy.GameObject;
                }
            }
            if (best == null) return;

            try
            {
                state.OriginalPlayer = spawner.Player;
                state.Swapped = true;
                spawner.Player = best;
            }
            catch
            {
                state = default;
            }
        }

        // FixedUpdate runs this per spawner per physics tick: remember which _Child was already
        // adopted so the steady state is one field read + one int compare (no GetComponent / name).
        static readonly System.Collections.Generic.HashSet<int> _adoptedChildren
            = new System.Collections.Generic.HashSet<int>();

        internal static void ClearAdopted() => _adoptedChildren.Clear();

        static void TryAdoptNativeChild(EnemySpawner spawner)
        {
            GameObject child = null;
            try { child = spawner._Child; } catch (System.Exception e) { Guard.Swallow(e); }
            if (child == null) return;

            int cid;
            try { cid = child.GetInstanceID(); } catch { return; }
            if (_adoptedChildren.Contains(cid)) return;

            EnemyController ec = null;
            try { ec = child.GetComponent<EnemyController>(); } catch (System.Exception e) { Guard.Swallow(e); }
            if (ec == null)
            {
                try { ec = child.GetComponentInChildren<EnemyController>(true); } catch (System.Exception e) { Guard.Swallow(e); }
            }
            if (ec == null) return;

            _adoptedChildren.Add(cid);
            EntitySpawner.AdoptNativeSpawn(ec, broadcast: true);
        }
    }
}
