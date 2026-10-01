// SyncRADation ? bind-pose bones only (skip runtime mount props). Euler read, quaternion apply.
using System.Collections.Generic;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Players
{
    public sealed class BoneSyncManager
    {
        private Transform _armatureRoot;
        private List<Transform> _bones;
        private float[] _readBuf;

        public int BoneCount => _bones?.Count ?? 0;

        public void FindArmature(GameObject root)
        {
            _armatureRoot = null;
            _bones = new List<Transform>();
            _readBuf = null;
            SkinnedMeshRenderer[] smrs = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < smrs.Length; i++)
            {
                if (smrs[i] == null || smrs[i].rootBone == null) continue;
                if (InWeaponPropTree(smrs[i].transform, root.transform)) continue;
                Transform top = smrs[i].rootBone;
                while (top.parent != null && top.parent.parent != null && top.parent.parent != root.transform)
                    top = top.parent;
                _armatureRoot = top;
                break;
            }
            if (_armatureRoot == null)
            {
                PlaytestLog.Warn("DRV", "armature root not on SMR, searching hierarchy");
                var smrParents = root.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < smrParents.Length; i++)
                {
                    if (smrParents[i] != null && smrParents[i].parent == root.transform
                        && smrParents[i].GetComponent<SkinnedMeshRenderer>() == null
                        && smrParents[i].childCount > 3)
                    {
                        _armatureRoot = smrParents[i];
                        break;
                    }
                }
            }
            if (_armatureRoot != null)
            {
                CollectBonesSkipProps(_armatureRoot);
                PlaytestLog.Verbose("DRV", "armature=" + _armatureRoot.name + " bones=" + _bones.Count);
            }
            else
            {
                PlaytestLog.Warn("DRV", "no armature found");
            }
        }

        private void CollectBonesSkipProps(Transform t)
        {
            if (t == null || IsWeaponPropName(t.name)) return;
            _bones.Add(t);
            for (int i = 0; i < t.childCount; i++)
                CollectBonesSkipProps(t.GetChild(i));
        }

        private static bool InWeaponPropTree(Transform t, Transform stop)
        {
            while (t != null && t != stop)
            {
                if (IsWeaponPropName(t.name)) return true;
                t = t.parent;
            }
            return false;
        }

        /// <summary>
        /// Mounts whose children change at runtime (equipped weapon under hand_R/WeaponMount, reload props under
        /// hand_L/MagazineMount): never part of the bone list, on the sender and on the proxy alike, so the
        /// tree-walk index of every real bone is the same on both sides.
        /// </summary>
        public static bool IsWeaponPropName(string name)
        {
            return name == "WeaponMount" || name == "Weapons" || name == "MagazineMount";
        }

        /// <summary>
        /// Local euler angles of every bone. The returned buffer is reused by the next call: the caller serializes it
        /// synchronously (PlayerState / BonePose send) and must not keep it.
        /// </summary>
        public float[] ReadRotations()
        {
            if (_bones == null || _bones.Count == 0) return null;
            int n = _bones.Count * 3;
            if (_readBuf == null || _readBuf.Length != n)
                _readBuf = new float[n];
            float[] data = _readBuf;
            for (int i = 0; i < _bones.Count; i++)
            {
                if (_bones[i] == null) continue;
                Vector3 e = _bones[i].localEulerAngles;
                data[i * 3] = e.x;
                data[i * 3 + 1] = e.y;
                data[i * 3 + 2] = e.z;
            }
            return data;
        }

        /// <summary>Euler snapshot to local rotations, once per snapshot (not once per rendered frame).</summary>
        public static void EulersToRotations(float[] eulers, Quaternion[] dst)
        {
            int n = System.Math.Min(dst.Length, eulers.Length / 3);
            for (int i = 0; i < n; i++)
                dst[i] = Quaternion.Euler(eulers[i * 3], eulers[i * 3 + 1], eulers[i * 3 + 2]);
        }

        public void ApplyRotations(Quaternion[] rots)
        {
            if (_bones == null || rots == null) return;
            int count = Mathf.Min(_bones.Count, rots.Length);
            for (int i = 0; i < count; i++)
            {
                var b = _bones[i];
                if (b == null) continue;
                b.localRotation = rots[i];
            }
        }

        public void ApplyRotationsInterpolated(Quaternion[] prev, Quaternion[] cur, float t)
        {
            if (_bones == null || prev == null || cur == null) return;
            int count = System.Math.Min(_bones.Count, System.Math.Min(prev.Length, cur.Length));
            for (int i = 0; i < count; i++)
            {
                var b = _bones[i];
                if (b == null) continue;
                b.localRotation = Nlerp(prev[i], cur[i], t);
            }
        }

        /// <summary>
        /// Shortest-path normalized lerp in managed code. Consecutive snapshots are ~33 ms apart, where nlerp and
        /// slerp are visually identical; it skips the native Slerp call per bone per frame.
        /// </summary>
        private static Quaternion Nlerp(Quaternion a, Quaternion b, float t)
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
