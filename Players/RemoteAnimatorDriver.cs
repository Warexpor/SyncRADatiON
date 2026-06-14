using SyncRADation.Networking;
using UnityEngine;

namespace SyncRADation.Players
{
    public sealed class RemoteAnimatorDriver
    {
        private Animator[] _animators;
        private SpriteRenderer[] _spriteRenderers;
        private Transform _rootTransform;
        private Transform _facingPivot;

        private float _targetForward;
        private float _smoothedForward;
        private float _targetTurn;
        private float _smoothedTurn;
        private float _targetAimingTime;
        private float _smoothedAimingTime;
        private float _currentFacing;
        private float _targetFacing;
        private float _stamina;
        private float _blend;
        private float _ikWalk;
        private float _inputX;
        private float _inputY;
        private float _hurtTime;
        private AnimBools _animBools;
        private AnimTriggers _pendingTriggers;
        private Networking.WeaponType _weapon;
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
            _smoothedForward = 0f;
            SyncRADation.ModRuntime.Log?.Msg("[DRV] Init: animators=" + (_animators != null ? _animators.Length.ToString() : "null")
                + " sprites=" + (_spriteRenderers != null ? _spriteRenderers.Length.ToString() : "null")
                + " facingPivot=" + (_facingPivot != null ? _facingPivot.name : "NULL"));
        }

        public void ApplyState(PlayerStateMessage state)
        {
            _targetForward = state.Forward;
            _targetTurn = state.Turn;
            _targetAimingTime = state.AimingTime;
            _stamina = state.Stamina;
            _blend = state.Blend;
            _ikWalk = state.IKwalk;
            _inputX = state.InputX;
            _inputY = state.InputY;
            _hurtTime = state.HurtTime;
            _animBools = state.AnimBools;
            _pendingTriggers |= state.AnimTriggers;
            _weapon = state.Weapon;
            _facing = state.Facing;
            _targetFacing = state.RotY;

            if (!_snappedToFirst)
            {
                _smoothedForward = state.Forward;
                _smoothedTurn = state.Turn;
                _smoothedAimingTime = state.AimingTime;
                _currentFacing = state.RotY;
                _snappedToFirst = true;

                foreach (var sr in _spriteRenderers)
                {
                    if (sr != null)
                        sr.flipX = ShouldFlipX(state.Facing);
                }

                foreach (var anim in _animators)
                {
                    if (anim == null) continue;
                    ApplyAllAnimParams(anim, state.Forward, state.Turn, state.AimingTime, state.Stamina, state.Blend, state.IKwalk, state.InputX, state.InputY, state.HurtTime, state.AnimBools, state.Weapon);
                    ApplyPendingTriggers(anim);
                    anim.Update(0f);
                }

                ApplyFacing();
            }
        }

        public void PreTick()
        {
            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            _smoothedForward = Mathf.Lerp(_smoothedForward, _targetForward, dt * SmoothRate);
            _smoothedTurn = Mathf.Lerp(_smoothedTurn, _targetTurn, dt * SmoothRate);
            _smoothedAimingTime = Mathf.Lerp(_smoothedAimingTime, _targetAimingTime, dt * SmoothRate);
            _currentFacing = Mathf.LerpAngle(_currentFacing, _targetFacing, dt * 12f);
            if (Mathf.Abs(Mathf.DeltaAngle(_currentFacing, _targetFacing)) < 0.5f)
                _currentFacing = _targetFacing;
        }

        public void Tick()
        {
            float forwardAmount = _smoothedForward;

            foreach (var sr in _spriteRenderers)
            {
                if (sr != null)
                    sr.flipX = ShouldFlipX(_facing);
            }

            foreach (var anim in _animators)
            {
                if (anim == null) continue;
                ApplyAllAnimParams(anim, forwardAmount, _smoothedTurn, _smoothedAimingTime, _stamina, _blend, _ikWalk, _inputX, _inputY, _hurtTime, _animBools, _weapon);
                ApplyPendingTriggers(anim);
            }

            _pendingTriggers = 0;
        }

        private static void ApplyAllAnimParams(Animator anim, float forward, float turn, float aimingTime, float stamina, float blend, float ikWalk, float inputX, float inputY, float hurtTime, AnimBools bools, Networking.WeaponType weapon)
        {
            // Floats
            anim.SetFloat("Forward", forward);
            anim.SetFloat("Turn", turn);
            anim.SetFloat("AimingTime", aimingTime);
            anim.SetFloat("Stamina", stamina);
            anim.SetFloat("Blend", blend);
            anim.SetFloat("IKwalk", ikWalk);
            anim.SetFloat("X", inputX);
            anim.SetFloat("Y", inputY);
            anim.SetFloat("HurtTime", hurtTime);

            // Action/movement/state bools
            anim.SetBool("Aiming", bools.HasFlag(AnimBools.Aiming));
            anim.SetBool("Shooting", bools.HasFlag(AnimBools.Shooting));
            anim.SetBool("Running", bools.HasFlag(AnimBools.Running));
            anim.SetBool("Grounded", bools.HasFlag(AnimBools.Grounded));
            anim.SetBool("Crouch", bools.HasFlag(AnimBools.Crouch));
            anim.SetBool("Blocked", bools.HasFlag(AnimBools.Blocked));
            anim.SetBool("Dead", bools.HasFlag(AnimBools.Dead));
            anim.SetBool("Inventory", bools.HasFlag(AnimBools.Inventory));
            anim.SetBool("Attack", bools.HasFlag(AnimBools.Attack));
            anim.SetBool("Injured", bools.HasFlag(AnimBools.Injured));
            anim.SetBool("Stomp", bools.HasFlag(AnimBools.Stomp));
            anim.SetBool("Push", bools.HasFlag(AnimBools.Push));
            anim.SetBool("Melee", bools.HasFlag(AnimBools.Melee));
            anim.SetBool("Snap", bools.HasFlag(AnimBools.Snap));
            anim.SetBool("Reload", bools.HasFlag(AnimBools.Reload));
            anim.SetBool("Swap", bools.HasFlag(AnimBools.Swap));
            anim.SetBool("Burst", bools.HasFlag(AnimBools.Burst));
            anim.SetBool("Taser", bools.HasFlag(AnimBools.Taser));
            anim.SetBool("Random", bools.HasFlag(AnimBools.Random));
            anim.SetBool("Hugged", bools.HasFlag(AnimBools.Hugged));
            anim.SetBool("ReloadRounds", bools.HasFlag(AnimBools.ReloadRounds));
            anim.SetBool("ReloadChamber", bools.HasFlag(AnimBools.ReloadChamber));

            // Weapon bools: reset all, then set active one
            anim.SetBool("Handgun", false);
            anim.SetBool("Pistol", false);
            anim.SetBool("Revolver", false);
            anim.SetBool("Shotgun", false);
            anim.SetBool("Rifle", false);
            anim.SetBool("SMG", false);
            anim.SetBool("Flare", false);
            anim.SetBool("CAR", false);
            switch (weapon)
            {
                case Networking.WeaponType.Handgun: anim.SetBool("Handgun", true); break;
                case Networking.WeaponType.Melee: break; // already set via Melee bool above
                case Networking.WeaponType.Pistol: anim.SetBool("Pistol", true); break;
                case Networking.WeaponType.Revolver: anim.SetBool("Revolver", true); break;
                case Networking.WeaponType.Shotgun: anim.SetBool("Shotgun", true); break;
                case Networking.WeaponType.Rifle: anim.SetBool("Rifle", true); break;
                case Networking.WeaponType.SMG: anim.SetBool("SMG", true); break;
                case Networking.WeaponType.Flare: anim.SetBool("Flare", true); break;
                case Networking.WeaponType.CAR: anim.SetBool("CAR", true); break;
            }
        }

        private void ApplyPendingTriggers(Animator anim)
        {
            if (_pendingTriggers == 0) return;
            if (_pendingTriggers.HasFlag(AnimTriggers.Hurt)) anim.SetTrigger("Hurt");
            if (_pendingTriggers.HasFlag(AnimTriggers.Die)) anim.SetTrigger("Die");
            if (_pendingTriggers.HasFlag(AnimTriggers.Fire)) anim.SetTrigger("Fire");
            if (_pendingTriggers.HasFlag(AnimTriggers.Pickup)) anim.SetTrigger("Pickup");
            if (_pendingTriggers.HasFlag(AnimTriggers.Radio)) anim.SetTrigger("Radio");
            if (_pendingTriggers.HasFlag(AnimTriggers.Drop)) anim.SetTrigger("Drop");
            if (_pendingTriggers.HasFlag(AnimTriggers.Sleep)) anim.SetTrigger("Sleep");
            if (_pendingTriggers.HasFlag(AnimTriggers.Injector)) anim.SetTrigger("Injector");
            if (_pendingTriggers.HasFlag(AnimTriggers.InjectorCancel)) anim.SetTrigger("InjectorCancel");
            if (_pendingTriggers.HasFlag(AnimTriggers.ReloadTrigger)) anim.SetTrigger("Reload");
            if (_pendingTriggers.HasFlag(AnimTriggers.AttackTrigger)) anim.SetTrigger("Attack");
            if (_pendingTriggers.HasFlag(AnimTriggers.SwapTrigger)) anim.SetTrigger("Swap");
            if (_pendingTriggers.HasFlag(AnimTriggers.BurstTrigger)) anim.SetTrigger("Burst");
            if (_pendingTriggers.HasFlag(AnimTriggers.StompTrigger)) anim.SetTrigger("Stomp");
            if (_pendingTriggers.HasFlag(AnimTriggers.PushTrigger)) anim.SetTrigger("Push");
            if (_pendingTriggers.HasFlag(AnimTriggers.SnapTrigger)) anim.SetTrigger("Snap");
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
                    sb.Append("] fwd="); sb.Append(a.GetFloat("Forward").ToString("F2"));
                    sb.Append(" turn="); sb.Append(a.GetFloat("Turn").ToString("F2"));
                    sb.Append(" aimT="); sb.Append(a.GetFloat("AimingTime").ToString("F2"));
                }
                SyncRADation.ModRuntime.Log?.Msg(sb.ToString());
                _lastLog = Time.time;
            }
        }

        private static Transform FindFacingPivot(Transform root)
        {
            if (root == null) return null;
            int count = root.childCount;
            for (int i = 0; i < count; i++)
            {
                Transform child = root.GetChild(i);
                if (HasSkinnedMeshInDescendants(child))
                    return child;
            }
            return null;
        }

        private static bool HasSkinnedMeshInDescendants(Transform t)
        {
            if (t == null) return false;
            if (t.GetComponent<SkinnedMeshRenderer>() != null) return true;
            int count = t.childCount;
            for (int i = 0; i < count; i++)
            {
                if (HasSkinnedMeshInDescendants(t.GetChild(i)))
                    return true;
            }
            return false;
        }

        private static bool ShouldFlipX(byte facing)
        {
            return facing == 1 || facing == 3;
        }
    }
}