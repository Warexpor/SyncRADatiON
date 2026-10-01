// SyncRADation � Dictionary<int,RemotePlayerProxy>, position interpolation, collider lookup
using System.Collections.Generic;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Players
{
    public sealed class PlayerProxyManager
    {
        private readonly Dictionary<int, RemotePlayerProxy> _proxies = new Dictionary<int, RemotePlayerProxy>();
        private readonly Dictionary<int, GameObject> _proxyObjects = new Dictionary<int, GameObject>();
        private readonly Dictionary<Collider, int> _proxyColliders = new Dictionary<Collider, int>();

        // Snapshot interpolation: render PoseInterpDelay behind (~2.5 packets at the real ~25 Hz) so the
        // pose is sampled between snaps (Hermite + Slerp), not exponential-lerped at the live packet.
        // SIGNALIS walks the XY plane; Z is height (up = -Z). Vel is planar (x, y, 0).
        private struct PoseSnap
        {
            public float Time;
            public Vector3 Pos;
            public Vector3 Vel;
            public Quaternion Facing;
        }
        private class InterpState
        {
            public readonly List<PoseSnap> Snaps = new List<PoseSnap>(8);
            public readonly SnapClock Clock = new SnapClock(PluginInfo.SendInterval);
            public bool isFirst;
        }
        private readonly Dictionary<int, InterpState> _interp = new Dictionary<int, InterpState>();
        private readonly List<int> _staleScratch = new List<int>(8);

        private const float TeleportDistance = 15f;
        private const float ExtrapolateMax = 0.12f;
        private const int SnapshotCap = 8;
        private int _proxyLayer = -1;

        public int ProxyLayer => _proxyLayer;
        public bool HasProxy(int playerId) => _proxies.ContainsKey(playerId);
        public RemotePlayerProxy GetProxy(int playerId) => _proxies.TryGetValue(playerId, out var p) ? p : null;

        private int[] _idCache = System.Array.Empty<int>();
        private bool _idDirty;

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
            catch (System.Exception e) { Guard.Swallow(e); }
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
                PlaytestLog.Warn("Proxy", "failed to create p" + playerId);
                return;
            }

            var proxy = new RemotePlayerProxy(clone, playerId);
            _proxies[playerId] = proxy;
            _idDirty = true;
            _proxyObjects[playerId] = clone;
            var capCol = clone.GetComponent<Collider>();
            if (capCol != null)
            {
                _proxyColliders[capCol] = playerId;
                if (_proxyLayer < 0) _proxyLayer = capCol.gameObject.layer;
            }
            _interp[playerId] = new InterpState { isFirst = true };
            PlaytestLog.Event("Proxy", "created p" + playerId);
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
                _idDirty = true;
                _proxyObjects.Remove(playerId);
                _interp.Remove(playerId);
                FlickerTrace.ProxyGone(playerId);
                PlaytestLog.Event("Proxy", "destroyed p" + playerId);
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
                ApplyPosition(playerId, targetPos, new Vector3(state.VelX, state.VelY, 0f), state.GetFacingWorld());
                HitchTrace.Recv(playerId);
            }
        }

        private void ApplyPosition(int playerId, Vector3 position, Vector3 velocity, Quaternion facingWorld)
        {
            if (!_interp.TryGetValue(playerId, out var ist)) return;

            bool teleport = !ist.isFirst && ist.Snaps.Count > 0
                && Vector3.Distance(ist.Snaps[ist.Snaps.Count - 1].Pos, position) > TeleportDistance;

            if (ist.isFirst || teleport)
            {
                if (_proxyObjects.TryGetValue(playerId, out var go) && go != null)
                {
                    go.transform.position = position;
                    go.transform.rotation = YawOnPlane(facingWorld, go.transform.up);
                }
                ist.Snaps.Clear();
                ist.Clock.Reset();
                ist.Snaps.Add(new PoseSnap
                {
                    Time = ist.Clock.Stamp(Time.unscaledTime),
                    Pos = position,
                    Vel = velocity,
                    Facing = facingWorld
                });
                ist.isFirst = false;
                return;
            }

            ist.Snaps.Add(new PoseSnap
            {
                Time = ist.Clock.Stamp(Time.unscaledTime),
                Pos = position,
                Vel = velocity,
                Facing = facingWorld
            });
            while (ist.Snaps.Count > SnapshotCap)
                ist.Snaps.RemoveAt(0);
        }

        public void LateUpdate()
        {
            // Real time: a local pause (inventory / menu sets timeScale 0) must not freeze or back up the remote timeline.
            float renderTime = Time.unscaledTime - PluginInfo.PoseInterpDelay;
            _staleScratch.Clear();

            foreach (var kvp in _proxyObjects)
            {
                int pid = kvp.Key;
                var go = kvp.Value;
                if (go == null)
                {
                    _staleScratch.Add(pid);
                    continue;
                }
                if (!_interp.TryGetValue(pid, out var ist) || ist.Snaps.Count == 0) continue;

                SamplePose(ist, renderTime, out Vector3 pos, out Quaternion facing);
                go.transform.position = pos;
                go.transform.rotation = YawOnPlane(facing, go.transform.up);
                try { FlickerTrace.Proxy(pid, go, _mode); } catch (System.Exception e) { Guard.Swallow(e); }
            }

            TickAll();
            for (int i = 0; i < _staleScratch.Count; i++)
                DestroyProxy(_staleScratch[i]);
        }

        static string _mode = "hold"; // last SamplePose branch, for FlickerTrace

        static void SamplePose(InterpState ist, float renderTime, out Vector3 pos, out Quaternion facing)
        {
            var snaps = ist.Snaps;
            int n = snaps.Count;
            var newest = snaps[n - 1];
            var oldest = snaps[0];

            if (n == 1 || renderTime <= oldest.Time)
            {
                pos = oldest.Pos;
                facing = oldest.Facing;
                HitchTrace.Interp("hold");
                _mode = "hold";
                return;
            }

            if (renderTime >= newest.Time)
            {
                // Planar only: never extrapolate height (a stale vertical guess reads as a hop).
                float extra = Mathf.Min(renderTime - newest.Time, ExtrapolateMax);
                pos = newest.Pos + newest.Vel * extra;
                pos.z = newest.Pos.z;
                facing = newest.Facing;
                HitchTrace.Interp("extrap");
                _mode = "extrap";
                return;
            }

            int hi = n - 1;
            while (hi > 0 && snaps[hi].Time > renderTime)
                hi--;
            int lo = hi;
            hi = Mathf.Min(lo + 1, n - 1);
            var a = snaps[lo];
            var b = snaps[hi];
            float span = b.Time - a.Time;
            float t = span > 0.0001f ? Mathf.Clamp01((renderTime - a.Time) / span) : 1f;
            pos = span > 0.0001f ? Hermite(a.Pos, a.Vel, b.Pos, b.Vel, span, t) : a.Pos;
            pos.z = Mathf.Lerp(a.Pos.z, b.Pos.z, t);
            facing = Quaternion.Slerp(a.Facing, b.Facing, t);
            HitchTrace.Interp("lerp");
            _mode = "lerp";
        }

        static Vector3 Hermite(Vector3 p0, Vector3 v0, Vector3 p1, Vector3 v1, float dt, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            return (2f * t3 - 3f * t2 + 1f) * p0
                + (t3 - 2f * t2 + t) * (dt * v0)
                + (-2f * t3 + 3f * t2) * p1
                + (t3 - t2) * (dt * v1);
        }

        static Quaternion YawOnPlane(Quaternion facingWorld, Vector3 up)
        {
            if (up.sqrMagnitude < 0.0001f)
                up = Vector3.up;
            else
                up.Normalize();
            Vector3 fwd = facingWorld * Vector3.forward;
            fwd = Vector3.ProjectOnPlane(fwd, up);
            if (fwd.sqrMagnitude < 0.0001f)
                fwd = Vector3.ProjectOnPlane(facingWorld * Vector3.right, up);
            if (fwd.sqrMagnitude < 0.0001f)
                return Quaternion.LookRotation(Vector3.forward, up);
            return Quaternion.LookRotation(fwd.normalized, up);
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
