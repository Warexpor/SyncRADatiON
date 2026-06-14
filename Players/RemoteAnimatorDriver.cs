using UnityEngine;

namespace SyncRADation.Players
{
    public sealed class RemoteAnimatorDriver
    {
        private Animator[] _animators;
        private SpriteRenderer[] _spriteRenderers;
        private Transform _rootTransform;
        private Transform _facingPivot;

        private float _targetSpeed;
        private float _smoothedSpeed;
        private float _currentFacing;
        private float _targetFacing;
        private bool _aiming;
        private bool _shooting;
        private bool _running;
        private byte _facing;
        private bool _snappedToFirst;

        private const float SmoothRate = 6f;

        public RemoteAnimatorDriver(GameObject target)
        {
            _rootTransform = target.transform;
        }

        public Transform RootTransform => _rootTransform;

        public void Initialize(GameObject target)
        {
            _rootTransform = target.transform;
            _animators = target.GetComponentsInChildren<Animator>(true);
            _spriteRenderers = target.GetComponentsInChildren<SpriteRenderer>(true);
            _facingPivot = FindFacingPivot(_rootTransform);
            _smoothedSpeed = 0f;
            SyncRADation.ModRuntime.Log?.Msg("[DRV] Init: animators=" + (_animators != null ? _animators.Length.ToString() : "null")
                + " sprites=" + (_spriteRenderers != null ? _spriteRenderers.Length.ToString() : "null")
                + " facingPivot=" + (_facingPivot != null ? _facingPivot.name : "NULL"));
        }

        public void ApplyState(float speed, bool aiming, bool shooting, bool running, byte facing, byte charState, float rotY)
        {
            _targetSpeed = speed;
            _aiming = aiming;
            _shooting = shooting;
            _running = running;
            _facing = facing;
            _targetFacing = rotY;

            if (!_snappedToFirst)
            {
                _smoothedSpeed = speed;
                _currentFacing = rotY;
                _snappedToFirst = true;

                foreach (var sr in _spriteRenderers)
                {
                    if (sr != null)
                        sr.flipX = ShouldFlipX(facing);
                }

                foreach (var anim in _animators)
                {
                    if (anim == null) continue;
                    anim.SetFloat("Speed", speed);
                    anim.SetFloat("Forward", speed);
                    anim.SetFloat("Turn", 0f);
                    anim.SetBool("Aiming", aiming);
                    anim.SetBool("Shooting", shooting);
                    anim.SetBool("Running", running);
                    anim.Update(0f);
                }

                ApplyFacing();
            }
        }

        public void PreTick()
        {
            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            _smoothedSpeed = Mathf.Lerp(_smoothedSpeed, _targetSpeed, dt * SmoothRate);
            _currentFacing = Mathf.LerpAngle(_currentFacing, _targetFacing, dt * 12f);
            if (Mathf.Abs(Mathf.DeltaAngle(_currentFacing, _targetFacing)) < 0.5f)
                _currentFacing = _targetFacing;
        }

        public void Tick()
        {
            float forwardAmount = _smoothedSpeed;

            foreach (var sr in _spriteRenderers)
            {
                if (sr != null)
                    sr.flipX = ShouldFlipX(_facing);
            }

            foreach (var anim in _animators)
            {
                if (anim == null) continue;
                anim.SetFloat("Speed", _smoothedSpeed);
                anim.SetFloat("Forward", forwardAmount);
                anim.SetFloat("Turn", 0f);
                anim.SetBool("Aiming", _aiming);
                anim.SetBool("Shooting", _shooting);
                anim.SetBool("Running", _running);
            }
        }

        private void ApplyFacing()
        {
            if (_facingPivot == null)
                return;
            _facingPivot.localEulerAngles = new Vector3(0f, _currentFacing, 0f);
        }

        private float _lastLog;

        public void LateTick()
        {
            ApplyFacing();

            if (Time.time - _lastLog > 3f)
            {
                var sb = new System.Text.StringBuilder("[DRV] root=");
                sb.Append(_rootTransform.eulerAngles.ToString("F1"));
                sb.Append(" pivot=");
                sb.Append(_facingPivot != null ? _facingPivot.localEulerAngles.ToString("F1") : "NULL");
                sb.Append(" curFacing="); sb.Append(_currentFacing.ToString("F1"));
                sb.Append(" target="); sb.Append(_targetFacing.ToString("F1"));
                for (int i = 0; i < _animators.Length; i++)
                {
                    var a = _animators[i];
                    if (a == null) continue;
                    sb.Append(" [a"); sb.Append(i);
                    sb.Append("] spd="); sb.Append(a.GetFloat("Speed").ToString("F2"));
                    sb.Append(" fwd="); sb.Append(a.GetFloat("Forward").ToString("F2"));
                }
                SyncRADation.ModRuntime.Log?.Msg(sb.ToString());
                _lastLog = Time.time;
            }
        }

        private static Transform FindFacingPivot(Transform root)
        {
            SkinnedMeshRenderer[] smrs = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int si = 0; si < smrs.Length; si++)
            {
                if (smrs[si] != null)
                {
                    Transform t = smrs[si].transform;
                    while (t != null && t.parent != null && t.parent != root)
                        t = t.parent;
                    if (t != null && t.parent == root)
                        return t;
                }
            }
            return null;
        }

        private static bool ShouldFlipX(byte facing)
        {
            switch (facing)
            {
                case 0: return false;
                case 1: return false;
                case 2: return true;
                case 3: return false;
                case 4: return true;
                case 5: return true;
                case 6: return true;
                case 7: return false;
                default: return false;
            }
        }
    }
}
