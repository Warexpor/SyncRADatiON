// Remote Elster root motion: position + facing replayed PoseInterpDelay behind on a jitter-smoothed snapshot timeline.
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Players
{
    /// <summary>
    /// Rendered between snapshots (Hermite on position, Slerp on facing), not exponential-lerped at the live packet.
    /// SIGNALIS walks the XY plane; Z is height (up = -Z), so velocity is planar and height is never extrapolated.
    /// </summary>
    public sealed class ProxyMotion
    {
        private struct Snap
        {
            public Vector3 Pos;
            public Vector3 Vel;
            public Quaternion Facing;
        }

        private const int Capacity = 8;
        private const float TeleportDistance = 15f;
        private const float ExtrapolateMax = 0.12f;
        /// <summary>Height change across the whole buffer (~0.25 s) that is real travel, not wobble (PoseMath.FloorZ).</summary>
        private const float HeightTravel = 0.3f;
        /// <summary>Exponential rate the rendered height follows its target at (1/s).</summary>
        private const float HeightRate = 20f;

        private readonly Transform _root;
        private readonly SnapshotRing<Snap> _snaps = new SnapshotRing<Snap>(Capacity);
        // Same stamps as _snaps: the heights alone, for PoseMath.FloorZ.
        private readonly SnapshotRing<float> _heights = new SnapshotRing<float>(Capacity);
        private readonly SnapClock _clock = new SnapClock(PluginInfo.SendInterval);
        private float _z;
        private bool _hasZ;

        /// <summary>Last sampling branch ("hold" / "extrap" / "lerp") for FlickerTrace.</summary>
        public string Mode { get; private set; } = "hold";

        /// <summary>Root z sampled from the sender's timeline before the floor lock (FlickerTrace bob line).</summary>
        public float RawZ { get; private set; }

        public ProxyMotion(Transform root)
        {
            _root = root;
        }

        public void OnState(Vector3 position, Vector3 velocity, Quaternion facingWorld)
        {
            if (velocity.sqrMagnitude > PluginInfo.MaxProxySpeed * PluginInfo.MaxProxySpeed)
                velocity = Vector3.zero;
            // First pose or a room-to-room door: place the body there instead of gliding across the map.
            int n = _snaps.Count;
            if (n == 0 || Vector3.Distance(_snaps.At(n - 1).Pos, position) > TeleportDistance)
            {
                _root.SetPositionAndRotation(position, YawOnPlane(facingWorld, _root.up));
                _snaps.Clear();
                _heights.Clear();
                _clock.Reset();
                _hasZ = false;
            }
            float stamp = _clock.Stamp(Time.unscaledTime);
            _snaps.Push(stamp) = new Snap { Pos = position, Vel = velocity, Facing = facingWorld };
            _heights.Push(stamp) = position.z;
        }

        /// <summary>Rendered root z: the buffered floor height (PoseMath.FloorZ), eased so a target change never steps.</summary>
        private float Height(float sampledZ)
        {
            float target = PoseMath.FloorZ(_heights, sampledZ, HeightTravel);
            if (!_hasZ)
            {
                _z = target;
                _hasZ = true;
            }
            else
            {
                _z += (target - _z) * (1f - Mathf.Exp(-HeightRate * Time.unscaledDeltaTime));
            }
            return _z;
        }

        /// <summary>
        /// Writes the root at render time. Real time: a local pause (inventory / menu sets timeScale 0) must not freeze
        /// or back up the remote timeline.
        /// </summary>
        public void LateTick(float renderTime)
        {
            var mode = _snaps.Sample(renderTime, out int lo, out int hi, out float t);
            Vector3 pos;
            Quaternion facing;
            switch (mode)
            {
                case RingSample.Empty:
                    return;
                case RingSample.BeforeOldest:
                    pos = _snaps.At(0).Pos;
                    facing = _snaps.At(0).Facing;
                    Mode = "hold";
                    break;
                case RingSample.AfterNewest:
                {
                    var s = _snaps.At(hi);
                    // Planar only: a stale vertical guess reads as a hop.
                    float extra = Mathf.Min(renderTime - _snaps.TimeAt(hi), ExtrapolateMax);
                    pos = s.Pos + s.Vel * extra;
                    pos.z = s.Pos.z;
                    facing = s.Facing;
                    Mode = "extrap";
                    break;
                }
                default:
                {
                    var a = _snaps.At(lo);
                    var b = _snaps.At(hi);
                    float span = _snaps.TimeAt(hi) - _snaps.TimeAt(lo);
                    pos = Hermite(a.Pos, a.Vel, b.Pos, b.Vel, span, t);
                    pos.z = Mathf.Lerp(a.Pos.z, b.Pos.z, t);
                    facing = Quaternion.Slerp(a.Facing, b.Facing, t);
                    Mode = "lerp";
                    break;
                }
            }
            HitchTrace.Interp(Mode);
            RawZ = pos.z;
            pos.z = Height(pos.z);
            _root.SetPositionAndRotation(pos, YawOnPlane(facing, _root.up));
        }

        private static Vector3 Hermite(Vector3 p0, Vector3 v0, Vector3 p1, Vector3 v1, float dt, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            return (2f * t3 - 3f * t2 + 1f) * p0
                + (t3 - 2f * t2 + t) * (dt * v0)
                + (-2f * t3 + 3f * t2) * p1
                + (t3 - t2) * (dt * v1);
        }

        /// <summary>The sender's facing turned into a yaw about the proxy's own up (its root keeps the source tilt).</summary>
        private static Quaternion YawOnPlane(Quaternion facingWorld, Vector3 up)
        {
            if (up.sqrMagnitude < 0.0001f)
                up = Vector3.up;
            else
                up.Normalize();
            Vector3 fwd = Vector3.ProjectOnPlane(facingWorld * Vector3.forward, up);
            if (fwd.sqrMagnitude < 0.0001f)
                fwd = Vector3.ProjectOnPlane(facingWorld * Vector3.right, up);
            if (fwd.sqrMagnitude < 0.0001f)
                return Quaternion.LookRotation(Vector3.forward, up);
            return Quaternion.LookRotation(fwd.normalized, up);
        }
    }
}
