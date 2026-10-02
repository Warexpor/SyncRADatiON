// Allocation-free pose blending for the proxy timelines. Pure C# on the Quaternion fields (tests).
using UnityEngine;

namespace SyncRADation.Players
{
    public static class PoseMath
    {
        /// <summary>
        /// Shortest-path normalized lerp. Consecutive snapshots are ~33 ms apart, where nlerp and slerp are visually
        /// identical; it skips a native Slerp call per bone per frame.
        /// </summary>
        public static Quaternion Nlerp(Quaternion a, Quaternion b, float t)
        {
            float dot = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
            float s = dot < 0f ? -t : t;
            float u = 1f - t;
            float x = a.x * u + b.x * s;
            float y = a.y * u + b.y * s;
            float z = a.z * u + b.z * s;
            float w = a.w * u + b.w * s;
            float mag = (float)System.Math.Sqrt(x * x + y * y + z * z + w * w);
            if (mag < 1e-6f) return b;
            float inv = 1f / mag;
            return new Quaternion(x * inv, y * inv, z * inv, w * inv);
        }

        /// <summary>
        /// Remote root height (z; SIGNALIS up is -Z, so the floor is the largest z). Elster cannot jump, yet the
        /// sender's root z wobbles a few tenths (door spawn points sit above the floor, the 3D rigidbody settles),
        /// and a 30 Hz sample of that wobble plays back as hops. Feet stay on the lowest height of the buffered
        /// timeline unless the whole buffer travels by more than <paramref name="travel"/> (elevator, stairs, a
        /// ledge): then the sampled height is followed as is.
        /// </summary>
        public static float FloorZ(SnapshotRing<float> heights, float sampledZ, float travel)
        {
            int n = heights.Count;
            if (n < 2) return sampledZ;
            float oldest = heights.At(0);
            float newest = heights.At(n - 1);
            float trend = newest - oldest;
            if (trend > travel || trend < -travel) return sampledZ;
            float floor = oldest;
            for (int i = 1; i < n; i++)
            {
                float z = heights.At(i);
                if (z > floor) floor = z;
            }
            return floor;
        }
    }
}
