// SyncRADation � Dictionary<int,RemotePlayerProxy>, position interpolation, collider lookup
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
            public Vector3 targetPos;
            public Vector3 vel;
            public Quaternion facingWorld = Quaternion.identity;
            public float arrivalTime;
            public bool isFirst;
        }
        private readonly Dictionary<int, InterpState> _interp = new Dictionary<int, InterpState>();

        private const float TeleportDistance = 15f;
        private const float FacingSlerpRate = 16f;
        private const float PositionLerpRate = 16f;
        private const float PredictMaxAge = 0.12f;
        private int _proxyLayer = -1;

        public int ProxyLayer => _proxyLayer;
        public bool HasProxy(int playerId) => _proxies.ContainsKey(playerId);
        public RemotePlayerProxy GetProxy(int playerId) => _proxies.TryGetValue(playerId, out var p) ? p : null;

        public IEnumerable<int> GetProxyPlayerIds()
        {
            foreach (var kvp in _proxies)
                yield return kvp.Key;
        }

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
                if (local == go)
                {
                    var net = LanNetworkManager.Instance;
                    return net != null ? net.LocalPlayerId : 0;
                }
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
                DestroyProxy(playerId);

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
                var targetPos = new Vector3(state.PosX, state.PosY, state.PosZ);
                ApplyPosition(playerId, targetPos, new Vector3(state.VelX, 0f, state.VelZ), state.GetFacingWorld());
            }
        }

        private void ApplyPosition(int playerId, Vector3 position, Vector3 velocity, Quaternion facingWorld)
        {
            if (!_interp.TryGetValue(playerId, out var ist)) return;

            ist.facingWorld = facingWorld;

            bool teleport = !ist.isFirst &&
                (Vector3.Distance(ist.targetPos, position) > TeleportDistance);

            if (ist.isFirst || teleport)
            {
                if (_proxyObjects.TryGetValue(playerId, out var go) && go != null)
                {
                    go.transform.position = position;
                    go.transform.rotation = facingWorld;
                }
                ist.targetPos = position;
                ist.vel = velocity;
                ist.arrivalTime = Time.time;
                ist.isFirst = false;
                return;
            }

            ist.targetPos = position;
            ist.vel = velocity;
            ist.arrivalTime = Time.time;
        }

        public void LateUpdate()
        {
            float now = Time.time;
            float followT = Mathf.Clamp01(Time.deltaTime * PositionLerpRate);
            var stale = new List<int>();

            foreach (var kvp in _proxyObjects)
            {
                int pid = kvp.Key;
                var go = kvp.Value;
                if (go == null)
                {
                    stale.Add(pid);
                    continue;
                }
                if (!_interp.TryGetValue(pid, out var ist)) continue;

                Vector3 predicted = ist.targetPos;
                float age = now - ist.arrivalTime;
                if (age > 0f && age < PredictMaxAge)
                {
                    float spd2 = ist.vel.x * ist.vel.x + ist.vel.z * ist.vel.z;
                    if (spd2 > 0.04f)
                        predicted += ist.vel * age;
                }

                if (Vector3.Distance(go.transform.position, predicted) > TeleportDistance)
                    go.transform.position = predicted;
                else
                    go.transform.position = Vector3.Lerp(go.transform.position, predicted, followT);

                if (Quaternion.Angle(go.transform.rotation, ist.facingWorld) > 50f)
                    go.transform.rotation = ist.facingWorld;
                else
                    go.transform.rotation = Quaternion.Slerp(go.transform.rotation, ist.facingWorld,
                        Mathf.Clamp01(Time.deltaTime * FacingSlerpRate));
            }

            TickAll();
            for (int i = 0; i < stale.Count; i++)
                DestroyProxy(stale[i]);
        }

        private void TickAll()
        {
            foreach (var kvp in _proxies)
            {
                var proxy = kvp.Value;
                var driver = proxy.AnimDriver;
                if (driver != null)
                {
                    driver.Tick();
                    driver.LateTick();
                }
                proxy.LateFxTick();
            }
        }
    }
}
