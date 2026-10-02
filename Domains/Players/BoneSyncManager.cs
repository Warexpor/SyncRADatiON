// Elster armature bone list: the sender reads it, the proxy writes it. Same tree walk on the same hierarchy on both
// sides, so a bone's list index is its wire identity.
using System.Collections.Generic;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Players
{
    public sealed class BoneSyncManager
    {
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

        /// <summary>Rebuilds the bone list under <paramref name="modelRoot"/>; null just clears it.</summary>
        public void FindArmature(Transform modelRoot)
        {
            _bones.Clear();
            _readBuf = null;
            if (modelRoot == null) return;
            Transform armature = null;
            var smrs = modelRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < smrs.Length && armature == null; i++)
            {
                if (smrs[i] == null || smrs[i].rootBone == null) continue;
                if (InWeaponPropTree(smrs[i].transform, modelRoot)) continue;
                Transform top = smrs[i].rootBone;
                while (top.parent != null && top.parent.parent != null && top.parent.parent != modelRoot)
                    top = top.parent;
                armature = top;
            }
            // No SMR with a rootBone: the armature is the bone-only child with a real subtree.
            for (int i = 0; armature == null && i < modelRoot.childCount; i++)
            {
                var c = modelRoot.GetChild(i);
                if (c.GetComponent<SkinnedMeshRenderer>() == null && c.childCount > 3)
                    armature = c;
            }
            if (armature != null)
                CollectBonesSkipProps(armature);
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

        /// <summary>
        /// Another Elster rig on the same avatar (the PEN_Hole crawl rig `elster_metarig_IK`): per bone-list index, the
        /// transform at the same name path under its armature top, or null. Name paths, not tree-walk indices: the
        /// player rig also carries props (flashlight flare, blood FX, VisibleEquip) the crawl rig has not.
        /// </summary>
        public Transform[] MapByPath(Transform otherRig)
        {
            int count = _bones.Count;
            if (count == 0 || otherRig == null) return null;
            Transform myTop = _bones[0];
            Transform otherTop = FindNamed(otherRig, myTop.name, myTop.parent != null ? myTop.parent.name : null);
            if (otherTop == null) return null;
            var map = new Transform[count];
            map[0] = otherTop;
            for (int i = 1; i < count; i++)
            {
                var b = _bones[i];
                if (b == null) continue;
                string path = b.name;
                for (var t = b.parent; t != null && t != myTop; t = t.parent)
                    path = t.name + "/" + path;
                map[i] = otherTop.Find(path);
            }
            return map;
        }

        static Transform FindNamed(Transform root, string name, string parentName)
        {
            var all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i];
                if (t != null && t.name == name && (parentName == null || (t.parent != null && t.parent.name == parentName)))
                    return t;
            }
            return null;
        }

        /// <summary>ReadRotations with each bone read from <paramref name="alt"/>[i] where mapped (own bone otherwise).</summary>
        public float[] ReadRotations(Transform[] alt)
        {
            if (alt == null) return ReadRotations();
            int count = _bones.Count;
            if (count == 0) return null;
            if (_readBuf == null || _readBuf.Length != count * 3)
                _readBuf = new float[count * 3];
            for (int i = 0; i < count; i++)
            {
                var b = i < alt.Length && alt[i] != null ? alt[i] : _bones[i];
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
