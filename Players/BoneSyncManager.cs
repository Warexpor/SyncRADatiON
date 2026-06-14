// SyncRADation — armature bone Euler read/apply at 20Hz (every 3rd packet)
using System.Collections.Generic;
using UnityEngine;

namespace SyncRADation.Players
{
    public sealed class BoneSyncManager
    {
        private Transform _armatureRoot;
        private List<Transform> _bones;

        public int BoneCount => _bones?.Count ?? 0;

        public void FindArmature(GameObject root)
        {
            _armatureRoot = null;
            _bones = new List<Transform>();
            // Find armature by looking for SkinnedMeshRenderer rootBone ancestors
            SkinnedMeshRenderer[] smrs = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < smrs.Length; i++)
            {
                if (smrs[i] != null && smrs[i].rootBone != null)
                {
                    Transform top = smrs[i].rootBone;
                    // Walk up to the child of the facing pivot (model root)
                    while (top.parent != null && top.parent.parent != null && top.parent.parent != root.transform)
                        top = top.parent;
                    _armatureRoot = top;
                    break;
                }
            }
            if (_armatureRoot == null)
            {
                ModRuntime.Log?.Warning("[BoneSync] Armature root not found via SMR. Searching hierarchy...");
                // Fallback: find first child that is Transform-only (no renderer, not the facing pivot)
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
                CollectBones(_armatureRoot);
                ModRuntime.Log?.Msg("[BoneSync] Armature=" + _armatureRoot.name + " bones=" + _bones.Count);
            }
            else
            {
                ModRuntime.Log?.Warning("[BoneSync] No armature found!");
            }
        }

        private void CollectBones(Transform t)
        {
            if (t == null) return;
            _bones.Add(t);
            for (int i = 0; i < t.childCount; i++)
                CollectBones(t.GetChild(i));
        }

        public float[] ReadRotations()
        {
            if (_bones == null || _bones.Count == 0) return null;
            float[] data = new float[_bones.Count * 3];
            for (int i = 0; i < _bones.Count; i++)
            {
                Vector3 e = _bones[i].localEulerAngles;
                data[i * 3] = e.x;
                data[i * 3 + 1] = e.y;
                data[i * 3 + 2] = e.z;
            }
            return data;
        }

        public void ApplyRotationsSnap(float[] data)
        {
            if (_bones == null || data == null) return;
            int count = Mathf.Min(_bones.Count, data.Length / 3);
            for (int i = 0; i < count; i++)
            {
                _bones[i].localEulerAngles = new Vector3(data[i * 3], data[i * 3 + 1], data[i * 3 + 2]);
            }
        }

        public void ApplyRotationsInterpolated(float[] prev, float[] cur, float t)
        {
            if (_bones == null || prev == null || cur == null) return;
            int cnt1 = System.Math.Min(_bones.Count, prev.Length / 3);
            int count = System.Math.Min(cnt1, cur.Length / 3);
            for (int i = 0; i < count; i++)
            {
                _bones[i].localEulerAngles = new Vector3(
                    Mathf.LerpAngle(prev[i * 3], cur[i * 3], t),
                    Mathf.LerpAngle(prev[i * 3 + 1], cur[i * 3 + 1], t),
                    Mathf.LerpAngle(prev[i * 3 + 2], cur[i * 3 + 2], t)
                );
            }
        }
    }
}
