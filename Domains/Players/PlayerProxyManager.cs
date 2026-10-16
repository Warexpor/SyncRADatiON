// Remote-player proxy registry: lifecycle, stale cleanup, per-frame tick, collider -> player lookup.
using System.Collections.Generic;
using SyncRADation.Config;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Players
{
    public sealed class PlayerProxyManager
    {
        private readonly Dictionary<int, RemotePlayerProxy> _proxies = new Dictionary<int, RemotePlayerProxy>();
        private readonly Dictionary<Collider, int> _proxyColliders = new Dictionary<Collider, int>();
        private readonly Dictionary<int, Collider> _colliderOf = new Dictionary<int, Collider>();
        private readonly List<int> _staleScratch = new List<int>(8);

        /// <summary>
        /// A proxy whose sender stopped sending poses (loading a scene, left this scene: the host stops relaying it)
        /// is removed instead of standing frozen where it was last seen; the next pose recreates it.
        /// </summary>
        private const float StaleProxySeconds = 3f;

        private int _proxyLayer = -1;
        private int[] _idCache = System.Array.Empty<int>();
        private bool _idDirty;

        public int ProxyLayer => _proxyLayer;
        public bool HasProxy(int playerId) => _proxies.ContainsKey(playerId);
        public RemotePlayerProxy GetProxy(int playerId) => _proxies.TryGetValue(playerId, out var p) ? p : null;

        /// <summary>
        /// Cached snapshot (rebuilt only when a proxy is added/removed) — callers iterate this per
        /// enemy/boss per frame, so no iterator allocation, and it is safe against DestroyProxy mid-loop.
        /// </summary>
        public int[] GetProxyPlayerIds()
        {
            if (_idDirty)
            {
                var ids = new int[_proxies.Count];
                int i = 0;
                foreach (var kvp in _proxies)
                    ids[i++] = kvp.Key;
                _idCache = ids;
                _idDirty = false;
            }
            return _idCache;
        }

        public int GetPlayerIdByGameObject(GameObject go)
        {
            if (go == null) return -1;
            foreach (var kvp in _proxies)
            {
                if (kvp.Value.GameObject == go)
                    return kvp.Key;
            }
            if (PlayerState.player == go)
            {
                var net = LanNetworkManager.Instance;
                return net != null ? net.LocalPlayerId : 0;
            }
            return -1;
        }

        public int GetPlayerIdByCollider(Collider col)
        {
            if (col == null) return -1;
            if (_proxyColliders.TryGetValue(col, out var id)) return id;
            for (Transform t = col.transform; t != null; t = t.parent)
            {
                foreach (var kvp in _proxies)
                {
                    var go = kvp.Value.GameObject;
                    if (go != null && go.transform == t)
                        return kvp.Key;
                }
            }
            return -1;
        }

        public void CreateProxy(int playerId, GameObject source)
        {
            if (_proxies.ContainsKey(playerId))
                DestroyProxy(playerId);

            GameObject clone = PlayerProxyBuilder.CreatePlayerClone(source, "RemotePlayer_" + playerId, out var rig);
            if (clone == null) return;

            _proxies[playerId] = new RemotePlayerProxy(clone, playerId, rig);
            _idDirty = true;
            var capCol = clone.GetComponent<Collider>();
            if (capCol != null)
            {
                _proxyColliders[capCol] = playerId;
                _colliderOf[playerId] = capCol;
                if (_proxyLayer < 0) _proxyLayer = capCol.gameObject.layer;
            }
            PlaytestLog.Event("Proxy", "created p" + playerId);
        }

        public void DestroyProxy(int playerId)
        {
            // The collider entry goes even when a scene load already destroyed the proxy object.
            if (_colliderOf.TryGetValue(playerId, out var col))
            {
                _colliderOf.Remove(playerId);
                try { _proxyColliders.Remove(col); }
                catch (System.Exception e) { Guard.Swallow(e); }
            }
            if (!_proxies.TryGetValue(playerId, out var proxy)) return;
            if (proxy.GameObject != null)
                Object.Destroy(proxy.GameObject);
            _proxies.Remove(playerId);
            _idDirty = true;
            FlickerTrace.ProxyGone(playerId);
            PlaytestLog.Event("Proxy", "destroyed p" + playerId);
        }

        public void DestroyAll()
        {
            foreach (int id in new List<int>(_proxies.Keys))
                DestroyProxy(id);
            _proxyColliders.Clear();
            _colliderOf.Clear();
        }

        public void ApplyState(int playerId, PlayerStateMessage state)
        {
            if (!_proxies.TryGetValue(playerId, out var proxy)) return;
            proxy.ApplyState(state);
            HitchTrace.Recv(playerId);
        }

        public void LateUpdate()
        {
            float now = Time.unscaledTime;
            _staleScratch.Clear();
            foreach (var kvp in _proxies)
            {
                int pid = kvp.Key;
                var proxy = kvp.Value;
                if (proxy.GameObject == null)
                {
                    _staleScratch.Add(pid);
                    continue;
                }
                if (now - proxy.LastStateAt > StaleProxySeconds)
                {
                    PlaytestLog.Event("Proxy", "p" + pid + " sent no pose for " + StaleProxySeconds.ToString("F0") + "s - removed");
                    _staleScratch.Add(pid);
                    continue;
                }
                try
                {
                    proxy.LateTick(now - PluginInfo.PoseInterpDelay);
                    FlickerTrace.Proxy(pid, proxy.GameObject, proxy.Motion.Mode);
                    if (ModConfig.DiagnosticsOn)
                    {
                        var root = proxy.GameObject.transform;
                        FlickerTrace.ProxyBob(pid, proxy.Motion.RawZ, root.position.z, proxy.Pose.HipsHeight(root), proxy.Motion.Mode);
                    }
                }
                catch (System.Exception e) { Guard.Swallow("Proxy.LateTick", e); }
            }
            for (int i = 0; i < _staleScratch.Count; i++)
                DestroyProxy(_staleScratch[i]);
        }
    }
}
