// SyncRADation � drives proxy Animator params (9 floats, 22 bools, 16 triggers), facing pivot, bone lerp
using SyncRADation.Networking;
using SyncRADation.Sync;
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
    private bool _climbing;
    private AnimTriggers _pendingTriggers;
    private Networking.WeaponType _weapon;
    private byte _facing;
    private bool _snappedToFirst;
    private BoneSyncManager _boneSync;
    private float[] _boneAssemble;
    private int _boneAssembleGot;
        private struct BoneSnap
        {
            public float Time;
            public float[] Eulers;
        }
        private readonly System.Collections.Generic.List<BoneSnap> _boneSnaps = new System.Collections.Generic.List<BoneSnap>(8);
        private readonly SnapClock _boneClock = new SnapClock(PluginInfo.SendInterval);
        private const int BoneSnapCap = 8;

        public Vector3 AimDirection
        {
            get
            {
                if (_facingPivot != null) return _facingPivot.forward;
                if (_rootTransform != null) return _rootTransform.forward;
                return Vector3.forward;
            }
        }

        private const float SmoothRate = 12f;
        private const float FacingSmoothRate = 16f;

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
            _boneSync = new BoneSyncManager();
            // Use facing pivot (same hierarchy as source) for consistent bone indices
            if (_facingPivot != null)
                _boneSync.FindArmature(_facingPivot.gameObject);
            else
                _boneSync.FindArmature(target);
            PlaytestLog.Event("DRV", "init animators=" + (_animators != null ? _animators.Length.ToString() : "null")
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
            _climbing = state.Climbing;
            _pendingTriggers |= state.AnimTriggers;
            _weapon = state.Weapon;
            _facing = state.Facing;
            _targetFacing = state.RotY;
            // Timestamped bone snapshot; sampled on the same delay as root pose.
            if (state.BoneRotations != null && state.BoneRotations.Length > 0)
            {
                if (_boneSync != null && _boneSync.BoneCount > 0 && _boneSync.BoneCount != state.BoneRotations.Length / 3)
                {
                    PlaytestLog.Warn("DRV", "bone count mismatch proxy=" + _boneSync.BoneCount
                        + " source=" + (state.BoneRotations.Length / 3));
                }
                CommitBoneSnapshot(state.BoneRotations);
            }

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
                    ApplyAnimParams(anim, state.Forward, state.Turn, state.AimingTime, state.Stamina, state.Blend, state.IKwalk, state.InputX, state.InputY, state.HurtTime, state.AnimBools, state.Climbing);
                    ApplyWeaponParams(anim);
                    ApplyPendingTriggers(anim);
                    // Snap bones directly on first state (no interpolation yet)
                    if (_boneSync != null && state.BoneRotations != null)
                        _boneSync.ApplyRotationsSnap(state.BoneRotations);
                    anim.Update(0f);
                }

                ApplyFacing();
            }
        }

        /// <summary>Reliable one-shot triggers; consumed by the next Tick.</summary>
        public void AddOneShot(AnimTriggers triggers)
        {
            _pendingTriggers |= triggers;
        }

        /// <summary>Forget queued one-shots (e.g. Die/Hurt of a life that just ended).</summary>
        public void DropPending(AnimTriggers mask)
        {
            _pendingTriggers &= ~mask;
        }

        public void ApplyBoneChunk(ushort totalBones, ushort startBone, float[] eulers)
        {
            if (eulers == null || eulers.Length < 3 || totalBones == 0) return;
            int count = eulers.Length / 3;
            if (startBone == 0 || _boneAssemble == null || _boneAssemble.Length != totalBones * 3)
            {
                if (startBone != 0) return;
                _boneAssemble = new float[totalBones * 3];
                _boneAssembleGot = 0;
            }
            int dest = startBone * 3;
            if (dest + eulers.Length > _boneAssemble.Length) return;
            System.Array.Copy(eulers, 0, _boneAssemble, dest, eulers.Length);
            _boneAssembleGot += count;
            if (_boneAssembleGot < totalBones) return;

            CommitBoneSnapshot(_boneAssemble);
            _boneAssemble = null;
            _boneAssembleGot = 0;
        }

        private void CommitBoneSnapshot(float[] data)
        {
            if (data == null || data.Length < 3) return;
            // Ring of pooled buffers: the oldest snapshot's array is recycled instead of a new float[]
            // every pose (30 Hz x every remote player).
            float[] copy;
            if (_boneSnaps.Count >= BoneSnapCap)
            {
                copy = _boneSnaps[0].Eulers;
                _boneSnaps.RemoveAt(0);
                if (copy == null || copy.Length != data.Length)
                    copy = new float[data.Length];
            }
            else
                copy = new float[data.Length];
            System.Array.Copy(data, copy, data.Length);
            _boneSnaps.Add(new BoneSnap { Time = _boneClock.Stamp(Time.time), Eulers = copy });
            if (!_snappedToFirst && _boneSync != null)
                _boneSync.ApplyRotationsSnap(copy);
        }

        private void SampleBones(float renderTime)
        {
            int n = _boneSnaps.Count;
            if (n == 0 || _boneSync == null) return;
            var newest = _boneSnaps[n - 1];
            var oldest = _boneSnaps[0];
            if (n == 1 || renderTime <= oldest.Time)
            {
                _boneSync.ApplyRotationsSnap(oldest.Eulers);
                return;
            }
            if (renderTime >= newest.Time)
            {
                _boneSync.ApplyRotationsSnap(newest.Eulers);
                return;
            }
            int hi = n - 1;
            while (hi > 0 && _boneSnaps[hi].Time > renderTime)
                hi--;
            int lo = hi;
            hi = Mathf.Min(lo + 1, n - 1);
            var a = _boneSnaps[lo];
            var b = _boneSnaps[hi];
            float span = b.Time - a.Time;
            float t = span > 0.0001f ? Mathf.Clamp01((renderTime - a.Time) / span) : 1f;
            _boneSync.ApplyRotationsInterpolated(a.Eulers, b.Eulers, t);
        }

        public void PreTick()
        {
            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            _smoothedForward = Mathf.Lerp(_smoothedForward, _targetForward, dt * SmoothRate);
            _smoothedTurn = Mathf.Lerp(_smoothedTurn, _targetTurn, dt * SmoothRate);
            _smoothedAimingTime = Mathf.Lerp(_smoothedAimingTime, _targetAimingTime, dt * SmoothRate);
            _currentFacing = Mathf.LerpAngle(_currentFacing, _targetFacing, dt * FacingSmoothRate);
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
                ApplyAnimParams(anim, forwardAmount, _smoothedTurn, _smoothedAimingTime, _stamina, _blend, _ikWalk, _inputX, _inputY, _hurtTime, _animBools, _climbing);
                ApplyWeaponParams(anim);
                ApplyPendingTriggers(anim);
            }

            _pendingTriggers = 0;
        }

        // Animator.StringToHash once: string-keyed SetBool/SetFloat hashes the name on every call.
        private static class P
        {
            public static readonly int Forward = Animator.StringToHash("Forward");
            public static readonly int Turn = Animator.StringToHash("Turn");
            public static readonly int AimingTime = Animator.StringToHash("AimingTime");
            public static readonly int Stamina = Animator.StringToHash("Stamina");
            public static readonly int Blend = Animator.StringToHash("Blend");
            public static readonly int IKwalk = Animator.StringToHash("IKwalk");
            public static readonly int X = Animator.StringToHash("X");
            public static readonly int Y = Animator.StringToHash("Y");
            public static readonly int HurtTime = Animator.StringToHash("HurtTime");

            public static readonly int Aiming = Animator.StringToHash("Aiming");
            public static readonly int Shooting = Animator.StringToHash("Shooting");
            public static readonly int Running = Animator.StringToHash("Running");
            public static readonly int Grounded = Animator.StringToHash("Grounded");
            public static readonly int Crouch = Animator.StringToHash("Crouch");
            public static readonly int Blocked = Animator.StringToHash("Blocked");
            public static readonly int Dead = Animator.StringToHash("Dead");
            public static readonly int Inventory = Animator.StringToHash("Inventory");
            public static readonly int Attack = Animator.StringToHash("Attack");
            public static readonly int Injured = Animator.StringToHash("Injured");
            public static readonly int Stomp = Animator.StringToHash("Stomp");
            public static readonly int Push = Animator.StringToHash("Push");
            public static readonly int Melee = Animator.StringToHash("Melee");
            public static readonly int Snap = Animator.StringToHash("Snap");
            public static readonly int Reload = Animator.StringToHash("Reload");
            public static readonly int Swap = Animator.StringToHash("Swap");
            public static readonly int Burst = Animator.StringToHash("Burst");
            public static readonly int Taser = Animator.StringToHash("Taser");
            public static readonly int Random = Animator.StringToHash("Random");
            public static readonly int Hugged = Animator.StringToHash("Hugged");
            public static readonly int ReloadRounds = Animator.StringToHash("ReloadRounds");
            public static readonly int ReloadChamber = Animator.StringToHash("ReloadChamber");
            public static readonly int Climbing = Animator.StringToHash("Climbing");
            public static readonly int Crawl = Animator.StringToHash("Crawl");

            public static readonly int Hurt = Animator.StringToHash("Hurt");
            public static readonly int Die = Animator.StringToHash("Die");
            public static readonly int Fire = Animator.StringToHash("Fire");
            public static readonly int Pickup = Animator.StringToHash("Pickup");
            public static readonly int Radio = Animator.StringToHash("Radio");
            public static readonly int Drop = Animator.StringToHash("Drop");
            public static readonly int Sleep = Animator.StringToHash("Sleep");
            public static readonly int Injector = Animator.StringToHash("Injector");
            public static readonly int InjectorCancel = Animator.StringToHash("InjectorCancel");
        }

        private static void ApplyAnimParams(Animator anim, float forward, float turn, float aimingTime, float stamina, float blend, float ikWalk, float inputX, float inputY, float hurtTime, AnimBools bools, bool climbing)
        {
            anim.SetFloat(P.Forward, forward);
            anim.SetFloat(P.Turn, turn);
            anim.SetFloat(P.AimingTime, aimingTime);
            anim.SetFloat(P.Stamina, stamina);
            anim.SetFloat(P.Blend, blend);
            anim.SetFloat(P.IKwalk, ikWalk);
            anim.SetFloat(P.X, inputX);
            anim.SetFloat(P.Y, inputY);
            anim.SetFloat(P.HurtTime, hurtTime);

            anim.SetBool(P.Aiming, bools.HasFlag(AnimBools.Aiming));
            anim.SetBool(P.Shooting, bools.HasFlag(AnimBools.Shooting));
            anim.SetBool(P.Running, bools.HasFlag(AnimBools.Running));
            anim.SetBool(P.Grounded, bools.HasFlag(AnimBools.Grounded));
            anim.SetBool(P.Crouch, bools.HasFlag(AnimBools.Crouch) || climbing);
            anim.SetBool(P.Blocked, bools.HasFlag(AnimBools.Blocked));
            anim.SetBool(P.Dead, bools.HasFlag(AnimBools.Dead));
            anim.SetBool(P.Inventory, bools.HasFlag(AnimBools.Inventory));
            anim.SetBool(P.Attack, bools.HasFlag(AnimBools.Attack));
            anim.SetBool(P.Injured, bools.HasFlag(AnimBools.Injured));
            anim.SetBool(P.Stomp, bools.HasFlag(AnimBools.Stomp));
            anim.SetBool(P.Push, bools.HasFlag(AnimBools.Push));
            anim.SetBool(P.Melee, bools.HasFlag(AnimBools.Melee));
            anim.SetBool(P.Snap, bools.HasFlag(AnimBools.Snap));
            anim.SetBool(P.Reload, bools.HasFlag(AnimBools.Reload));
            anim.SetBool(P.Swap, bools.HasFlag(AnimBools.Swap));
            anim.SetBool(P.Burst, bools.HasFlag(AnimBools.Burst));
            anim.SetBool(P.Taser, bools.HasFlag(AnimBools.Taser));
            anim.SetBool(P.Random, bools.HasFlag(AnimBools.Random));
            anim.SetBool(P.Hugged, bools.HasFlag(AnimBools.Hugged));
            anim.SetBool(P.ReloadRounds, bools.HasFlag(AnimBools.ReloadRounds));
            anim.SetBool(P.ReloadChamber, bools.HasFlag(AnimBools.ReloadChamber));
            anim.SetBool(P.Climbing, climbing);
            anim.SetBool(P.Crawl, climbing);
        }

        private void ApplyWeaponParams(Animator anim)
        {
            int active = WeaponUtils.AnimatorBoolHash(_weapon);
            var ids = WeaponUtils.AnimatorBoolHashes;
            for (int i = 0; i < ids.Length; i++)
                anim.SetBool(ids[i], ids[i] == active);
        }

        private void ApplyPendingTriggers(Animator anim)
        {
            var t = _pendingTriggers;
            if (t == 0) return;
            if (t.HasFlag(AnimTriggers.Hurt)) anim.SetTrigger(P.Hurt);
            if (t.HasFlag(AnimTriggers.Die)) anim.SetTrigger(P.Die);
            if (t.HasFlag(AnimTriggers.Fire)) anim.SetTrigger(P.Fire);
            if (t.HasFlag(AnimTriggers.Pickup)) anim.SetTrigger(P.Pickup);
            if (t.HasFlag(AnimTriggers.Radio)) anim.SetTrigger(P.Radio);
            if (t.HasFlag(AnimTriggers.Drop)) anim.SetTrigger(P.Drop);
            if (t.HasFlag(AnimTriggers.Sleep)) anim.SetTrigger(P.Sleep);
            if (t.HasFlag(AnimTriggers.Injector)) anim.SetTrigger(P.Injector);
            if (t.HasFlag(AnimTriggers.InjectorCancel)) anim.SetTrigger(P.InjectorCancel);
            if (t.HasFlag(AnimTriggers.ReloadTrigger)) anim.SetTrigger(P.Reload);
            if (t.HasFlag(AnimTriggers.AttackTrigger)) anim.SetTrigger(P.Attack);
            if (t.HasFlag(AnimTriggers.SwapTrigger)) anim.SetTrigger(P.Swap);
            if (t.HasFlag(AnimTriggers.BurstTrigger)) anim.SetTrigger(P.Burst);
            if (t.HasFlag(AnimTriggers.StompTrigger)) anim.SetTrigger(P.Stomp);
            if (t.HasFlag(AnimTriggers.PushTrigger)) anim.SetTrigger(P.Push);
            if (t.HasFlag(AnimTriggers.SnapTrigger)) anim.SetTrigger(P.Snap);
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
            foreach (var anim in _animators)
            {
                if (anim == null) continue;
                ApplyWeaponParams(anim);
            }

            // Apply bone rotations � interpolate between snapshots
            if (_boneSync != null && _boneSnaps.Count > 0)
                SampleBones(Time.time - PluginInfo.PoseInterpDelay);

            if (SyncRADation.ModRuntime.VerboseLogging && Time.time - _lastLog > 30f)
            {
                try
                {
                    var sb = new System.Text.StringBuilder("root=");
                    sb.Append(_rootTransform.eulerAngles.ToString("F1"));
                    sb.Append(" pivot=");
                    sb.Append(_facingPivot != null ? _facingPivot.localEulerAngles.ToString("F1") : "NULL");
                    sb.Append(" curFacing="); sb.Append(_currentFacing.ToString("F1"));
                    sb.Append(" target="); sb.Append(_targetFacing.ToString("F1"));
                    sb.Append(" bones="); sb.Append(_boneSync != null ? _boneSync.BoneCount.ToString() : "0");
                    for (int i = 0; i < _animators.Length; i++)
                    {
                        var a = _animators[i];
                        if (a == null) continue;
                        sb.Append(" [a"); sb.Append(i);
                        sb.Append("] fwd="); sb.Append(a.GetFloat("Forward").ToString("F2"));
                        sb.Append(" turn="); sb.Append(a.GetFloat("Turn").ToString("F2"));
                        sb.Append(" aimT="); sb.Append(a.GetFloat("AimingTime").ToString("F2"));
                        sb.Append(" aim="); sb.Append(a.GetBool("Aiming") ? "1" : "0");
                        sb.Append(" shoot="); sb.Append(a.GetBool("Shooting") ? "1" : "0");
                        sb.Append(" run="); sb.Append(a.GetBool("Running") ? "1" : "0");
                        sb.Append(" inv="); sb.Append(a.GetBool("Inventory") ? "1" : "0");
                        var wpnNames = WeaponUtils.AnimatorBoolNames;
                        for (int wi = 0; wi < wpnNames.Length; wi++)
                        {
                            string w = wpnNames[wi];
                            sb.Append(" ").Append(w).Append("=");
                            try { sb.Append(a.GetBool(w) ? "1" : "0"); }
                            catch { sb.Append("E"); }
                        }
                        try
                        {
                            var si = a.GetCurrentAnimatorStateInfo(0);
                            sb.Append(" s0=").Append(si.shortNameHash);
                            sb.Append(" t0=").Append(si.normalizedTime.ToString("F2"));
                        }
                        catch (System.Exception e) { Guard.Swallow(e); }
                        try
                        {
                            var si1 = a.GetCurrentAnimatorStateInfo(1);
                            sb.Append(" s1=").Append(si1.shortNameHash);
                            sb.Append(" t1=").Append(si1.normalizedTime.ToString("F2"));
                        }
                        catch (System.Exception e) { Guard.Swallow(e); }
                    }
                    PlaytestLog.Verbose("DRV", sb.ToString());
                }
                catch (System.Exception e) { Guard.Swallow(e); }
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