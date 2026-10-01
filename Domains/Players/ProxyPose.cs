// Remote Elster body pose: the sender's bone rotations + humanoid hips position, replayed on a delayed snapshot timeline.
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Players
{
    /// <summary>
    /// The proxy has no Animator: every ElsterNewController clip binds only humanoid muscles / RootT / RootQ / IK goals
    /// (no transform, renderer or object curves), and those land on exactly the bone rotations + hips position this
    /// class writes. The clip events (StepSound, reload SFX) had no receiver on the stripped clone; ProxyAudioSync
    /// plays those sounds instead.
    /// </summary>
    public sealed class ProxyPose
    {
        private const int Capacity = 8;
        /// <summary>Fallback reveal when no bone snapshot ever arrives (sender without an armature, count mismatch).</summary>
        private const float RevealTimeout = 0.5f;

        // Class, not struct: a ring slot keeps its rotation buffer when it is recycled.
        private sealed class Frame
        {
            public int Serial;
            public Quaternion[] Rots;
            public bool HasHips;
            public Vector3 Hips;
        }

        private readonly Transform _model;
        private readonly Transform _hips;
        private readonly BoneSyncManager _bones = new BoneSyncManager();
        private readonly SnapshotRing<Frame> _ring = new SnapshotRing<Frame>(Capacity);
        private readonly SnapClock _clock = new SnapClock(PluginInfo.SendInterval);
        private int _serial;
        private int _heldSerial = -1;

        // Sender's hips from the latest PlayerState; rides the next committed bone snapshot (inline bones, or the
        // BonePose chunks sent right after that pose).
        private bool _pendingHasHips;
        private Vector3 _pendingHips;

        private float[] _assemble;
        private int _assembleGot;
        private int _mismatchLogged = -1;

        private SkinnedMeshRenderer[] _hidden;
        private readonly float _revealAt;

        public ProxyPose(Transform model, Transform hips)
        {
            _model = model;
            _hips = hips;
            // Facing lives on the proxy root (yaw of the sender's model world rotation). The clone keeps whatever local
            // yaw the local Elster's model had at clone time: zero it once, or the model is turned twice.
            _model.localRotation = Quaternion.identity;
            _bones.FindArmature(_model);
            _revealAt = Time.unscaledTime + RevealTimeout;
            Hide();
            PlaytestLog.Event("DRV", "pose model=" + _model.name + " bones=" + _bones.BoneCount
                + " hips=" + (_hips != null ? _hips.name : "NULL"));
        }

        /// <summary>Muzzle / laser fallback direction: the model faces along the proxy root.</summary>
        public Vector3 AimDirection => _model.forward;

        /// <summary>Per received PlayerState: hips for the next snapshot, then the inline bones when present.</summary>
        public void OnState(float[] eulers, bool hasHips, Vector3 hips)
        {
            _pendingHasHips = hasHips;
            _pendingHips = hips;
            if (eulers != null && eulers.Length > 0)
                Commit(eulers);
        }

        /// <summary>BonePose chunk (bones too large for the pose packet): commits once every bone has arrived.</summary>
        public void OnBoneChunk(ushort totalBones, ushort startBone, float[] eulers)
        {
            if (eulers == null || eulers.Length < 3 || totalBones == 0) return;
            if (startBone == 0 || _assemble == null || _assemble.Length != totalBones * 3)
            {
                if (startBone != 0) return;
                if (_assemble == null || _assemble.Length != totalBones * 3)
                    _assemble = new float[totalBones * 3];
                _assembleGot = 0;
            }
            int dest = startBone * 3;
            if (dest + eulers.Length > _assemble.Length) return;
            System.Array.Copy(eulers, 0, _assemble, dest, eulers.Length);
            _assembleGot += eulers.Length / 3;
            if (_assembleGot < totalBones) return;
            _assembleGot = 0;
            Commit(_assemble);
        }

        private void Commit(float[] eulers)
        {
            int bones = _bones.BoneCount;
            if (bones == 0) return;
            // Bone identity is the tree-walk index: a different count means the hierarchies differ, and applying by
            // index would twist the wrong bones.
            if (eulers.Length / 3 != bones)
            {
                if (_mismatchLogged != eulers.Length / 3)
                {
                    _mismatchLogged = eulers.Length / 3;
                    PlaytestLog.Warn("DRV", "bone count mismatch proxy=" + bones + " source=" + _mismatchLogged
                        + " - bone snapshots ignored");
                }
                return;
            }
            ref Frame f = ref _ring.Push(_clock.Stamp(Time.unscaledTime));
            if (f == null) f = new Frame();
            if (f.Rots == null || f.Rots.Length != bones) f.Rots = new Quaternion[bones];
            BoneSyncManager.EulersToRotations(eulers, f.Rots);
            f.Serial = ++_serial;
            f.HasHips = _pendingHasHips;
            f.Hips = _pendingHips;
        }

        /// <summary>Every frame after the root: writes the bone timeline at the delayed render time.</summary>
        public void LateTick(float renderTime)
        {
            var mode = _ring.Sample(renderTime, out int lo, out int hi, out float t);
            if (mode == RingSample.Empty)
            {
                if (_hidden != null && Time.unscaledTime >= _revealAt) Reveal();
                return;
            }
            if (mode == RingSample.Between)
            {
                var a = _ring.At(lo);
                var b = _ring.At(hi);
                _bones.ApplyRotationsInterpolated(a.Rots, b.Rots, t);
                if (a.HasHips && b.HasHips) SetHips(Vector3.Lerp(a.Hips, b.Hips, t));
                else if (b.HasHips) SetHips(b.Hips);
                _heldSerial = -1;
            }
            else
            {
                // Holding one snapshot: nothing else writes these bones, so an unchanged hold is not re-applied.
                var f = _ring.At(lo);
                if (f.Serial != _heldSerial)
                {
                    _bones.ApplyRotations(f.Rots);
                    if (f.HasHips) SetHips(f.Hips);
                    _heldSerial = f.Serial;
                }
            }
            if (_hidden != null) Reveal();
        }

        private void SetHips(Vector3 p)
        {
            if (_hips != null) _hips.localPosition = p;
        }

        /// <summary>The clone starts in whatever pose the local Elster had: keep it unseen until the sender's pose lands.</summary>
        private void Hide()
        {
            var smrs = _model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var hidden = new System.Collections.Generic.List<SkinnedMeshRenderer>(smrs.Length);
            for (int i = 0; i < smrs.Length; i++)
            {
                var r = smrs[i];
                if (r == null || !r.enabled) continue;
                r.enabled = false;
                hidden.Add(r);
            }
            _hidden = hidden.ToArray();
        }

        private void Reveal()
        {
            for (int i = 0; i < _hidden.Length; i++)
            {
                if (_hidden[i] != null) _hidden[i].enabled = true;
            }
            _hidden = null;
        }
    }
}
