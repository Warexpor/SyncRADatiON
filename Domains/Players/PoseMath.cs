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
    }
}
