// A remote player drawn as another model (AvatarSkins): the Elster clone stays the driven skeleton, the skin copies it.
using System.Collections.Generic;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Players
{
    /// <summary>
    /// Ariane's rig is the same Rigify metarig as Elster's (AssetRipper: hips / spine / chest / neck / head, shoulder /
    /// upper_arm / forearm / hand and fingers, thigh / shin / foot / toe, every bone along its local -X), with other
    /// bone lengths and another armature root orientation. So the pose is copied in world space, bone by bone by name:
    /// the hips take the Elster hips' position and rotation, everything below takes the rotation. The Elster clone keeps
    /// everything else a proxy does (pose timeline, weapon clone, FX, sounds); only its body renderers are off.
    /// Her arms are shorter, so the weapon and magazine mounts ride her hands instead of the hidden Elster ones.
    /// </summary>
    public sealed class ProxySkin
    {
        private struct Mount
        {
            public Transform T;          // Elster WeaponMount / MagazineMount
            public Transform SrcParent;  // the Elster hand it hangs on
            public Transform Dst;        // the skin's hand of the same name
            public Vector3 Local;        // its authored local position
        }

        private readonly Transform _root;
        private readonly ProxyRig _rig;
        private readonly ProxyPose _pose;

        private RemoteModel _applied = RemoteModel.Elster;
        // A model this proxy could not take (no hips, a broken template): not retried every frame.
        private RemoteModel _refused = RemoteModel.Elster;
        private GameObject _skin;
        private Transform[] _src, _dst;
        private Transform _srcHips, _dstHips;
        private float _drop;
        private Mount[] _mounts;
        private MeshRenderer[] _props;

        public ProxySkin(Transform root, ProxyRig rig, ProxyPose pose)
        {
            _root = root;
            _rig = rig;
            _pose = pose;
        }

        /// <summary>Every frame after the body pose, before the weapon FX (they read the mounts).</summary>
        public void LateTick()
        {
            var want = AvatarSkins.Wanted;
            if (want != _applied)
            {
                if (want == RemoteModel.Elster) Detach();
                else if (want != _refused && AvatarSkins.Has(want))
                {
                    Attach(want);
                    if (_applied != want) _refused = want;
                }
                // Not banked yet: keep what is shown until it is.
            }
            if (_skin == null) return;
            bool show = _pose.Revealed;
            if (_skin.activeSelf != show) _skin.SetActive(show);
            if (!show) return;

            _dstHips.position = _srcHips.position - _root.up * _drop;
            for (int i = 0; i < _src.Length; i++)
                _dst[i].rotation = _src[i].rotation;
            for (int i = 0; i < _mounts.Length; i++)
            {
                var m = _mounts[i];
                if (m.T == null) continue;
                m.T.position = m.Dst.position + (m.SrcParent.TransformPoint(m.Local) - m.SrcParent.position);
            }
        }

        private void Attach(RemoteModel model)
        {
            Detach();
            if (_rig.Hips == null) return;
            var template = AvatarSkins.Template(model);
            if (template == null) return;
            GameObject go = null;
            try
            {
                go = Object.Instantiate(template, _root, false);
                go.name = AvatarSkins.OwnPrefix + "Skin_" + model;
                AvatarSkins.CopyMeshes(template, go);
                // Last sibling: by-name lookups under the proxy (weapon mounts) keep finding the Elster bones first.
                go.transform.SetAsLastSibling();
                go.transform.localPosition = _rig.Model.localPosition;
                go.transform.localRotation = _rig.Model.localRotation;
                go.transform.localScale = _rig.Model.localScale;

                var srcByName = new Dictionary<string, Transform>();
                foreach (var t in _rig.Hips.GetComponentsInChildren<Transform>(true))
                {
                    if (t == null || BoneSyncManager.InPropTree(t, _rig.Model)) continue;
                    if (!srcByName.ContainsKey(t.name)) srcByName[t.name] = t;
                }
                Transform dstHips = null;
                foreach (var t in go.GetComponentsInChildren<Transform>(true))
                {
                    if (t != null && t.name == _rig.Hips.name) { dstHips = t; break; }
                }
                if (dstHips == null)
                {
                    PlaytestLog.Warn("Skin", "no '" + _rig.Hips.name + "' bone in " + template.name);
                    Object.Destroy(go);
                    return;
                }
                // Parents before children (GetComponentsInChildren walks depth-first): a world rotation written to a
                // parent never moves a child that was already set.
                var src = new List<Transform>();
                var dst = new List<Transform>();
                var dstByName = new Dictionary<string, Transform>();
                foreach (var t in dstHips.GetComponentsInChildren<Transform>(true))
                {
                    if (t == null) continue;
                    if (!dstByName.ContainsKey(t.name)) dstByName[t.name] = t;
                    Transform s;
                    if (!srcByName.TryGetValue(t.name, out s)) continue;
                    src.Add(s);
                    dst.Add(t);
                }
                _src = src.ToArray();
                _dst = dst.ToArray();
                _srcHips = _rig.Hips;
                _dstHips = dstHips;
                _drop = LegLength(srcByName) - LegLength(dstByName);

                var mounts = new List<Mount>();
                foreach (var t in _rig.Model.GetComponentsInChildren<Transform>(true))
                {
                    if (t == null || (t.name != "WeaponMount" && t.name != "MagazineMount") || t.parent == null) continue;
                    Transform hand;
                    if (!dstByName.TryGetValue(t.parent.name, out hand)) continue;
                    mounts.Add(new Mount { T = t, SrcParent = t.parent, Dst = hand, Local = t.localPosition });
                }
                _mounts = mounts.ToArray();

                // Gameplay layer of the Elster body: her source stood in a cutscene, on whatever layer that used.
                int layer = _root.gameObject.layer;
                foreach (var t in go.GetComponentsInChildren<Transform>(true))
                    if (t != null) t.gameObject.layer = layer;

                var props = new List<MeshRenderer>();
                foreach (var r in _rig.Model.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (r == null || !r.enabled || BoneSyncManager.InPropTree(r.transform, _rig.Model)) continue;
                    r.enabled = false;
                    props.Add(r);
                }
                _props = props.ToArray();

                _skin = go;
                _applied = model;
                _pose.SetBodyHidden(true);
                go.SetActive(_pose.Revealed);
                PlaytestLog.Event("Skin", _root.name + " " + model + " bones=" + _dst.Length + " mounts=" + _mounts.Length
                    + " drop=" + _drop.ToString("F2"));
            }
            catch (System.Exception e)
            {
                Guard.Swallow("ProxySkin.Attach", e);
                if (go != null && _skin == null) Object.Destroy(go);
            }
        }

        /// <summary>Thigh to foot along the bones (rotation-only rigs: the same in every pose); 0 when a bone is missing.</summary>
        private static float LegLength(Dictionary<string, Transform> bones)
        {
            Transform thigh, shin, foot;
            if (!bones.TryGetValue("thigh_L", out thigh) || !bones.TryGetValue("shin_L", out shin)
                || !bones.TryGetValue("foot_L", out foot))
                return 0f;
            return Vector3.Distance(thigh.position, shin.position) + Vector3.Distance(shin.position, foot.position);
        }

        private void Detach()
        {
            if (_skin == null)
            {
                _applied = RemoteModel.Elster;
                return;
            }
            try
            {
                Object.Destroy(_skin);
                for (int i = 0; i < _mounts.Length; i++)
                    if (_mounts[i].T != null) _mounts[i].T.localPosition = _mounts[i].Local;
                for (int i = 0; i < _props.Length; i++)
                    if (_props[i] != null) _props[i].enabled = true;
                _pose.SetBodyHidden(false);
            }
            catch (System.Exception e) { Guard.Swallow("ProxySkin.Detach", e); }
            _skin = null;
            _src = _dst = null;
            _mounts = null;
            _props = null;
            _applied = RemoteModel.Elster;
        }
    }
}
