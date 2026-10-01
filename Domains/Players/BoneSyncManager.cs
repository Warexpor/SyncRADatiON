// Elster armature bone list: the sender reads it, the proxy writes it. Same tree walk on the same hierarchy on both
// sides, so a bone's list index is its wire identity.
using System.Collections.Generic;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Players
{
    public sealed class BoneSyncManager
    {
        private Transform _armatureRoot;
        private readonly List<Transform> _bones = new List<Transform>(96);
        private float[] _readBuf;

        public int BoneCount => _bones.Count;

        /// <summary>
        /// The Elster model under a player root: its first direct child with a skinned mesh below it (Ellie_Default under
        /// Character Root). The sender reads bones from it and the proxy is a clone of it.
        /// </summary>
        public static Transform FindModelRoot(Transform playerRoot)
        {
            if (playerRoot == null) return null;
            var smrs = playerRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < smrs.Length; i++)
            {
                if (smrs[i] == null) continue;
                Transform t = smrs[i].transform;
                while (t.parent != null && t.parent != playerRoot)
                    t = t.parent;
                if (t.parent == playerRoot)
                    return t;
            }
            return null;
        }

        public void FindArmature(Transform modelRoot)
        {
            _armatureRoot = null;
            _bones.Clear();
            _readBuf = null;
            SkinnedMeshRenderer[] smrs = modelRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < smrs.Length; i++)
            {
                if (smrs[i] == null || smrs[i].rootBone == null) continue;
                if (InWeaponPropTree(smrs[i].transform, modelRoot)) continue;
                Transform top = smrs[i].rootBone;
                while (top.parent != null && top.parent.parent != null && top.parent.parent != modelRoot)
                    top = top.parent;
                _armatureRoot = top;
                break;
            }
            if (_armatureRoot == null)
            {
                // No SMR with a rootBone: the armature is the bone-only child with a real subtree.
                for (int i = 0; i < modelRoot.childCount; i++)
                {
                    var c = modelRoot.GetChild(i);
                    if (c.GetComponent<SkinnedMeshRenderer>() == null && c.childCount > 3)
                    {
                        _armatureRoot = c;
                        break;
                    }
                }
            }
            if (_armatureRoot != null)
                CollectBonesSkipProps(_armatureRoot);
            else
                PlaytestLog.Warn("DRV", "no armature under '" + modelRoot.name + "'");
        }

        private void CollectBonesSkipProps(Transform t)
        {
            if (IsWeaponPropName(t.name)) return;
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
            int count = _bones.Count;
            if (count == 0) return null;
            if (_readBuf == null || _readBuf.Length != count * 3)
                _readBuf = new float[count * 3];
            for (int i = 0; i < count; i++)
            {
                var b = _bones[i];
                if (b == null) continue;
                Vector3 e = b.localEulerAngles;
                _readBuf[i * 3] = e.x;
                _readBuf[i * 3 + 1] = e.y;
                _readBuf[i * 3 + 2] = e.z;
            }
            return _readBuf;
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
            int count = Mathf.Min(_bones.Count, rots.Length);
            for (int i = 0; i < count; i++)
            {
                var b = _bones[i];
                if (b != null) b.localRotation = rots[i];
            }
        }

        public void ApplyRotationsInterpolated(Quaternion[] a, Quaternion[] b, float t)
        {
            int count = System.Math.Min(_bones.Count, System.Math.Min(a.Length, b.Length));
            for (int i = 0; i < count; i++)
            {
                var bone = _bones[i];
                if (bone != null) bone.localRotation = PoseMath.Nlerp(a[i], b[i], t);
            }
        }
    }
}
