// SyncRADation — Dictionary<int,RemotePlayerProxy>, position interpolation, collider lookup
using System.Collections.Generic;
using SyncRADation.Networking;
using UnityEngine;

namespace SyncRADation.Players
{
    public sealed class PlayerProxyManager
    {
        private readonly Dictionary<int, RemotePlayerProxy> _proxies = new Dictionary<int, RemotePlayerProxy>();
        private readonly Dictionary<int, GameObject> _proxyObjects = new Dictionary<int, GameObject>();
        private readonly Dictionary<Collider, int> _proxyColliders = new Dictionary<Collider, int>();

        // Per-proxy interpolation state
        private class InterpState
        {
            public Vector3 prevPos;
            public Vector3 targetPos;
            public float prevRotY;
            public float targetRotY;
            public float arrivalTime;
            public bool hasTarget;
            public bool isFirst;
        }
        private readonly Dictionary<int, InterpState> _interp = new Dictionary<int, InterpState>();

        private static float SnapshotInterval => PluginInfo.SendInterval;
        private const float TeleportDistance = 15f;
        private int _proxyLayer = -1;

        public int ProxyLayer => _proxyLayer;
        public bool HasProxy(int playerId) => _proxies.ContainsKey(playerId);
        public RemotePlayerProxy GetProxy(int playerId) => _proxies.TryGetValue(playerId, out var p) ? p : null;

        public int GetPlayerIdByGameObject(GameObject go)
        {
            if (go == null) return -1;
            foreach (var kvp in _proxyObjects)
            {
                if (kvp.Value == go)
                    return kvp.Key;
            }
            // Check if it's the local player (host)
            try
            {
                var local = PlayerState.player;
                if (local == go) return 0;
            }
            catch { }
            return -1;
        }

        public int GetPlayerIdByCollider(Collider col)
        {
            if (col == null) return -1;
            if (_proxyColliders.TryGetValue(col, out var id)) return id;
            Transform t = col.transform;
            while (t != null)
            {
                foreach (var kvp in _proxyObjects)
                {
                    if (kvp.Value != null && kvp.Value.transform == t)
                        return kvp.Key;
                }
                t = t.parent;
            }
            return -1;
        }

        public void CreateProxy(int playerId, GameObject source)
        {
            if (_proxies.ContainsKey(playerId))
            {
                ModRuntime.Log?.Msg("[ProxyManager] Proxy for player " + playerId + " already exists");
                return;
            }

            GameObject clone = PlayerProxyBuilder.CreatePlayerClone(source, "RemotePlayer_" + playerId, Vector3.zero, ModRuntime.Log);
            if (clone == null)
            {
                ModRuntime.Log?.Warning("[ProxyManager] Failed to create proxy for player " + playerId);
                return;
            }

            var proxy = new RemotePlayerProxy(clone, playerId);
            _proxies[playerId] = proxy;
            _proxyObjects[playerId] = clone;
            var capCol = clone.GetComponent<Collider>();
            if (capCol != null)
            {
                _proxyColliders[capCol] = playerId;
                if (_proxyLayer < 0) _proxyLayer = capCol.gameObject.layer;
            }
            _interp[playerId] = new InterpState { isFirst = true };
            ModRuntime.Log?.Msg("[ProxyManager] Created proxy for player " + playerId);
        }

        public void DestroyProxy(int playerId)
        {
            if (_proxies.TryGetValue(playerId, out var proxy))
            {
                proxy.Destroy();
                if (_proxyObjects.TryGetValue(playerId, out var go) && go != null)
                {
                    var capCol = go.GetComponent<Collider>();
                    if (capCol != null) _proxyColliders.Remove(capCol);
                    Object.Destroy(go);
                }
                _proxies.Remove(playerId);
                _proxyObjects.Remove(playerId);
                _interp.Remove(playerId);
                ModRuntime.Log?.Msg("[ProxyManager] Destroyed proxy for player " + playerId);
            }
        }

        public void DestroyAll()
        {
            var ids = new List<int>(_proxies.Keys);
            foreach (int id in ids)
                DestroyProxy(id);
        }

        public void ApplyState(int playerId, PlayerStateMessage state)
        {
            if (_proxies.TryGetValue(playerId, out var proxy))
            {
                proxy.ApplyState(state);
                // Store position for interpolation
                var targetPos = new Vector3(state.PosX, state.PosY, state.PosZ);
                ApplyPosition(playerId, targetPos, state.RotY);
            }
        }

        private void ApplyPosition(int playerId, Vector3 position, float rotY)
        {
            if (!_interp.TryGetValue(playerId, out var ist)) return;

            bool teleport = !ist.isFirst &&
                (Vector3.Distance(ist.targetPos, position) > TeleportDistance);

            if (ist.isFirst || teleport)
            {
                if (_proxyObjects.TryGetValue(playerId, out var go) && go != null)
                    go.transform.position = position;
                ist.prevPos = position;
                ist.targetPos = position;
                ist.prevRotY = rotY;
                ist.targetRotY = rotY;
                ist.arrivalTime = Time.time;
                ist.hasTarget = true;
                ist.isFirst = false;
                return;
            }

            ist.prevPos = _proxyObjects.TryGetValue(playerId, out var go2) && go2 != null
                ? go2.transform.position : ist.targetPos;
            ist.prevRotY = rotY; // Facing handled by RemoteAnimatorDriver
            ist.targetPos = position;
            ist.targetRotY = rotY;
            ist.arrivalTime = Time.time;
            ist.hasTarget = true;
        }

        public void LateUpdate()
        {
            TickAll();
            float now = Time.time;

            foreach (var kvp in _interp)
            {
                int pid = kvp.Key;
                var ist = kvp.Value;
                if (!ist.hasTarget) continue;
                if (!_proxyObjects.TryGetValue(pid, out var go) || go == null) continue;

                float elapsed = now - ist.arrivalTime;

                if (elapsed > 0.3f)
                {
                    go.transform.position = ist.targetPos;
                    ist.hasTarget = false;
                }
                else if (elapsed > SnapshotInterval)
                {
                    float extrapT = elapsed - SnapshotInterval;
                    Vector3 vel = (ist.targetPos - ist.prevPos) / SnapshotInterval;
                    float damp = Mathf.Clamp01(1f - (extrapT / 0.27f));
                    go.transform.position = ist.targetPos + vel * extrapT * damp;
                }
                else
                {
                    float t = elapsed / SnapshotInterval;
                    float smoothT = t * t * (3f - 2f * t);
                    go.transform.position = Vector3.Lerp(ist.prevPos, ist.targetPos, smoothT);
                }
            }
        }

        private void TickAll()
        {
            foreach (var kvp in _proxies)
            {
                var driver = kvp.Value.AnimDriver;
                if (driver != null)
                {
                    driver.Tick();
                    driver.LateTick();
                }
            }
        }
    }
}
