// SyncRADation � drives the proxy Animator params that exist in ElsterNewController, bone + hips timeline
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
        private float _stamina;
        private float _hurtTime;
        private AnimBools _animBools;
        private AnimBools _prevBools;
        private bool _hasPrevBools;
        private bool _edgeTaser;
        private bool _edgeReloadChamber;
        private AnimTriggers _pendingTriggers;
        private Networking.WeaponType _weapon;
        private byte _facing;
        private bool _snappedToFirst;

        // Last values written to the Animator: bools / weapon flags are only re-sent when they change.
        private bool _paramsApplied;
        private bool _appliedAiming, _appliedDead, _appliedInventory, _appliedInjured;
        private bool _weaponApplied;
        private Networking.WeaponType _appliedWeapon;
        private bool _flipApplied;
        private bool _appliedFlip;

        // Hurt / Pickup are Bool params in ElsterNewController: native ElsterHurtAnimation.hurt sets Hurt true and
        // clears it 0.2 s later (Ghidra ElsterHurtAnimation.<Callback>d__17). The one-shot mirrors that pulse.
        private const float BoolPulse = 0.2f;
        private float _hurtOffAt = -1f;
        private float _pickupOffAt = -1f;

        private BoneSyncManager _boneSync;
        private float[] _boneAssemble;
        private int _boneAssembleGot;
        private int _boneMismatchLogged = -1;
        private struct BoneSnap
        {
            public float Time;
            public Quaternion[] Rots;
            public bool HasHips;
            public Vector3 Hips;
        }
        // Sender's humanoid hips localPosition from the latest PlayerState; rides the next committed bone snapshot
        // (same send frame: inline bones, or the chunks that follow the pose packet).
        private bool _latestHasHips;
        private Vector3 _latestHips;
        private Transform _proxyHips;
        private float _hipsLogAt;
        private int _hipsFixes;
        private float _hipsWorst;
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

        public RemoteAnimatorDriver(GameObject target)
        {
            _rootTransform = target.transform;
        }

        public void Initialize(GameObject target)
        {
            _rootTransform = target.transform;
            _animators = target.GetComponentsInChildren<Animator>(true);
            _spriteRenderers = target.GetComponentsInChildren<SpriteRenderer>(true);
            _facingPivot = FindFacingPivot(_rootTransform);
            // Facing lives on the proxy root (PlayerProxyManager: yaw of the sender's facing-pivot world rotation).
            // The pivot clone keeps whatever local yaw the local Elster's pivot had at clone time: zero it once, or
            // the model is turned twice.
            if (_facingPivot != null)
                _facingPivot.localRotation = Quaternion.identity;
            _smoothedForward = 0f;
            _boneSync = new BoneSyncManager();
            // Use facing pivot (same hierarchy as source) for consistent bone indices
            if (_facingPivot != null)
                _boneSync.FindArmature(_facingPivot.gameObject);
            else
                _boneSync.FindArmature(target);
            _proxyHips = null;
            for (int i = 0; _animators != null && i < _animators.Length && _proxyHips == null; i++)
            {
                try
                {
                    var a = _animators[i];
                    if (a != null && a.isHuman) _proxyHips = a.GetBoneTransform(HumanBodyBones.Hips);
                }
                catch (System.Exception e) { Guard.Swallow(e); }
            }
            PlaytestLog.Event("DRV", "init animators=" + (_animators != null ? _animators.Length.ToString() : "null")
                + " sprites=" + (_spriteRenderers != null ? _spriteRenderers.Length.ToString() : "null")
                + " facingPivot=" + (_facingPivot != null ? _facingPivot.name : "NULL")
                + " bones=" + _boneSync.BoneCount
                + " hips=" + (_proxyHips != null ? _proxyHips.name : "NULL"));
        }

        public void ApplyState(PlayerStateMessage state)
        {
            _targetForward = state.Forward;
            _targetTurn = state.Turn;
            _targetAimingTime = state.AimingTime;
            _stamina = state.Stamina;
            _hurtTime = state.HurtTime;
            _animBools = state.AnimBools;
            if (_hasPrevBools)
            {
                // Trigger params the sender samples as bools: replay the rising edge as the trigger.
                if (!_prevBools.HasFlag(AnimBools.Taser) && _animBools.HasFlag(AnimBools.Taser)) _edgeTaser = true;
                if (!_prevBools.HasFlag(AnimBools.ReloadChamber) && _animBools.HasFlag(AnimBools.ReloadChamber)) _edgeReloadChamber = true;
            }
            _prevBools = _animBools;
            _hasPrevBools = true;
            _pendingTriggers |= state.AnimTriggers;
            _weapon = state.Weapon;
            _facing = state.Facing;
            _latestHasHips = state.HasHips;
            _latestHips = new Vector3(state.HipsX, state.HipsY, state.HipsZ);
            // Timestamped bone snapshot; sampled on the same delay as root pose.
            if (state.BoneRotations != null && state.BoneRotations.Length > 0)
                CommitBoneSnapshot(state.BoneRotations);

            if (!_snappedToFirst)
            {
                _smoothedForward = state.Forward;
                _smoothedTurn = state.Turn;
                _smoothedAimingTime = state.AimingTime;
                _snappedToFirst = true;

                Tick();
                foreach (var anim in _animators)
                {
                    if (anim == null) continue;
                    try { anim.Update(0f); } catch (System.Exception e) { Guard.Swallow(e); }
                }
                // Snap the first pose directly (no interpolation yet), after the Animator wrote its own.
                int n = _boneSnaps.Count;
                if (n > 0 && _boneSync != null)
                {
                    _boneSync.ApplyRotations(_boneSnaps[n - 1].Rots);
                    if (_boneSnaps[n - 1].HasHips && _proxyHips != null)
                        _proxyHips.localPosition = _boneSnaps[n - 1].Hips;
                }
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

        /// <summary>
        /// PlayerVital: Dead param, the Die trigger when this death was not already played by a one-shot, and the
        /// revive reset (Die trigger + Hurt bool cleared). The pose bits take over again on the next Tick.
        /// </summary>
        public void ApplyVital(bool dead, bool playDie, bool revived)
        {
            if (_animators == null) return;
            foreach (var anim in _animators)
            {
                if (anim == null) continue;
                try
                {
                    anim.SetBool(P.Dead, dead);
                    if (playDie) anim.SetTrigger(P.Die);
                    if (revived)
                    {
                        anim.ResetTrigger(P.Die);
                        anim.SetBool(P.Hurt, false);
                    }
                }
                catch (System.Exception e) { Guard.Swallow(e); }
            }
            if (revived) _hurtOffAt = -1f;
            _paramsApplied = false;
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
            if (data == null || data.Length < 3 || _boneSync == null) return;
            int bones = _boneSync.BoneCount;
            if (bones == 0) return;
            // Bone identity is the tree-walk index: a different count means the two hierarchies differ, and applying
            // by index would twist the wrong bones. Drop the snapshot (the proxy keeps its own Animator pose).
            if (data.Length / 3 != bones)
            {
                if (_boneMismatchLogged != data.Length / 3)
                {
                    _boneMismatchLogged = data.Length / 3;
                    PlaytestLog.Warn("DRV", "bone count mismatch proxy=" + bones + " source=" + (data.Length / 3)
                        + " - bone snapshots ignored");
                }
                return;
            }
            // Ring of pooled buffers: the oldest snapshot's array is recycled instead of a new one every pose
            // (30 Hz x every remote player). Converted to rotations once here, not per rendered frame.
            Quaternion[] rots;
            if (_boneSnaps.Count >= BoneSnapCap)
            {
                rots = _boneSnaps[0].Rots;
                _boneSnaps.RemoveAt(0);
                if (rots == null || rots.Length != bones)
                    rots = new Quaternion[bones];
            }
            else
                rots = new Quaternion[bones];
            BoneSyncManager.EulersToRotations(data, rots);
            _boneSnaps.Add(new BoneSnap
            {
                Time = _boneClock.Stamp(Time.unscaledTime),
                Rots = rots,
                HasHips = _latestHasHips,
                Hips = _latestHips
            });
            if (!_snappedToFirst)
                _boneSync.ApplyRotations(rots);
        }

        private void SampleBones(float renderTime)
        {
            int n = _boneSnaps.Count;
            if (n == 0 || _boneSync == null) return;
            var newest = _boneSnaps[n - 1];
            var oldest = _boneSnaps[0];
            if (n == 1 || renderTime <= oldest.Time)
            {
                _boneSync.ApplyRotations(oldest.Rots);
                if (oldest.HasHips) ApplyHips(oldest.Hips);
                return;
            }
            if (renderTime >= newest.Time)
            {
                _boneSync.ApplyRotations(newest.Rots);
                if (newest.HasHips) ApplyHips(newest.Hips);
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
            _boneSync.ApplyRotationsInterpolated(a.Rots, b.Rots, t);
            if (a.HasHips && b.HasHips) ApplyHips(Vector3.Lerp(a.Hips, b.Hips, t));
            else if (b.HasHips) ApplyHips(b.Hips);
        }

        /// <summary>
        /// Overwrites the hips position the proxy's own Animator just wrote. Logs (rate-limited) when that Animator
        /// was far off: those frames were the visible lift-offs / sinks before v18.
        /// </summary>
        private void ApplyHips(Vector3 target)
        {
            if (_proxyHips == null) return;
            var parent = _proxyHips.parent;
            Vector3 off = _proxyHips.localPosition - target;
            float d = parent != null ? parent.TransformVector(off).magnitude : off.magnitude;
            _proxyHips.localPosition = target;
            if (d < HipsLogMin) return;
            _hipsFixes++;
            if (d > _hipsWorst) _hipsWorst = d;
            float now = Time.unscaledTime;
            if (now - _hipsLogAt < 2f) return;
            _hipsLogAt = now;
            PlaytestLog.Event("Proxy", "hips corrected d=" + _hipsWorst.ToString("F2") + " n=" + _hipsFixes
                + " proxyState=" + ProxyStateName() + " fwd=" + _smoothedForward.ToString("F2"));
            _hipsFixes = 0;
            _hipsWorst = 0f;
        }

        private const float HipsLogMin = 0.1f;

        private string ProxyStateName()
        {
            try
            {
                if (_animators == null || _animators.Length == 0 || _animators[0] == null) return "?";
                var a = _animators[0];
                string s = StateName(a.GetCurrentAnimatorStateInfo(0).shortNameHash);
                if (a.IsInTransition(0))
                    s += ">" + StateName(a.GetNextAnimatorStateInfo(0).shortNameHash);
                return s;
            }
            catch { return "?"; }
        }

        // ElsterNewController base-layer state names (decompile), for readable [Proxy] hips lines.
        private static System.Collections.Generic.Dictionary<int, string> _stateNames;

        private static string StateName(int hash)
        {
            if (_stateNames == null)
            {
                _stateNames = new System.Collections.Generic.Dictionary<int, string>();
                const string names = "Unarmed|Pistol Uninjured|Shotgun|Rifle|SMG|Melee Uninjured|Hurt|Hurt Pistol|Hurt Shotgun"
                    + "|Hurt Rifle|Hurt SMG|Hurt Melee|Hurt Pistol Aim|Hurt Shotgun Aim|Hurt SMG Aim|Hurt Melee Aim|Hurt Flare Aim"
                    + "|Aim Pistol|Aim Shotgun|Aim Rifle|Aim SMG|Aim Machete|Aim Flare|Idle>Aim Transition|Aim>Idle Transition"
                    + "|Down|Stay|Up|Drop|Drop Big|Getup|Hug|SnapTurn|Dying|Dying Handgun|Dying Rifle|Dying Shotgun"
                    + "|Stomp Unarmed|Stomp Unarmed 0|Stomp Pistol|Stomp Pistol 0|Stomp Shotgun|Stomp Shotgun 0|Stomp Rifle"
                    + "|Stomp Rifle 0|Stomp SMG|Stomp SMG 0|Stomp Melee|Stomp Melee 0|Idle Melee|Idle Taser|Idle Injector";
                foreach (var n in names.Split('|'))
                    _stateNames[Animator.StringToHash(n)] = n;
            }
            return _stateNames.TryGetValue(hash, out var s) ? s : hash.ToString();
        }

        public void PreTick()
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            _smoothedForward = Mathf.Lerp(_smoothedForward, _targetForward, dt * SmoothRate);
            _smoothedTurn = Mathf.Lerp(_smoothedTurn, _targetTurn, dt * SmoothRate);
            _smoothedAimingTime = Mathf.Lerp(_smoothedAimingTime, _targetAimingTime, dt * SmoothRate);
        }

        public void Tick()
        {
            bool flip = ShouldFlipX(_facing);
            if (!_flipApplied || flip != _appliedFlip)
            {
                foreach (var sr in _spriteRenderers)
                {
                    if (sr != null)
                        sr.flipX = flip;
                }
                _appliedFlip = flip;
                _flipApplied = true;
            }

            float now = Time.unscaledTime;
            bool hurtOff = _hurtOffAt >= 0f && now >= _hurtOffAt;
            bool pickupOff = _pickupOffAt >= 0f && now >= _pickupOffAt;
            if (hurtOff) _hurtOffAt = -1f;
            if (pickupOff) _pickupOffAt = -1f;

            bool weaponChanged = !_weaponApplied || _weapon != _appliedWeapon;
            foreach (var anim in _animators)
            {
                if (anim == null) continue;
                ApplyAnimParams(anim);
                if (weaponChanged) ApplyWeaponParams(anim);
                if (hurtOff) anim.SetBool(P.Hurt, false);
                if (pickupOff) anim.SetBool(P.Pickup, false);
                ApplyPendingTriggers(anim, now);
            }
            _paramsApplied = true;
            _weaponApplied = true;
            _appliedWeapon = _weapon;

            _pendingTriggers = 0;
            _edgeTaser = false;
            _edgeReloadChamber = false;
        }

        // Animator.StringToHash once: string-keyed SetBool/SetFloat hashes the name on every call.
        // Only parameters that exist in ElsterNewController (AssetRipper AnimatorController/ElsterNewController.controller).
        private static class P
        {
            // Float
            public static readonly int Forward = Animator.StringToHash("Forward");
            public static readonly int Turn = Animator.StringToHash("Turn");
            public static readonly int AimingTime = Animator.StringToHash("AimingTime");
            public static readonly int Stamina = Animator.StringToHash("Stamina");
            public static readonly int HurtTime = Animator.StringToHash("HurtTime");
            // Bool
            public static readonly int Aiming = Animator.StringToHash("Aiming");
            public static readonly int Dead = Animator.StringToHash("Dead");
            public static readonly int Inventory = Animator.StringToHash("Inventory");
            public static readonly int Injured = Animator.StringToHash("Injured");
            public static readonly int Hurt = Animator.StringToHash("Hurt");
            public static readonly int Pickup = Animator.StringToHash("Pickup");
            // Trigger
            public static readonly int Die = Animator.StringToHash("Die");
            public static readonly int Attack = Animator.StringToHash("Attack");
            public static readonly int Push = Animator.StringToHash("Push");
            public static readonly int Reload = Animator.StringToHash("Reload");
            public static readonly int ReloadChamber = Animator.StringToHash("ReloadChamber");
            public static readonly int Stomp = Animator.StringToHash("Stomp");
            public static readonly int Swap = Animator.StringToHash("Swap");
            public static readonly int Drop = Animator.StringToHash("Drop");
            public static readonly int Taser = Animator.StringToHash("Tools/Taser");
            public static readonly int Injector = Animator.StringToHash("Tools/Injector");
            public static readonly int InjectorCancel = Animator.StringToHash("Tools/InjectorCancel");
        }

        private void ApplyAnimParams(Animator anim)
        {
            anim.SetFloat(P.Forward, _smoothedForward);
            anim.SetFloat(P.Turn, _smoothedTurn);
            anim.SetFloat(P.AimingTime, _smoothedAimingTime);
            anim.SetFloat(P.Stamina, _stamina);
            anim.SetFloat(P.HurtTime, _hurtTime);

            var bools = _animBools;
            bool aiming = bools.HasFlag(AnimBools.Aiming);
            bool dead = bools.HasFlag(AnimBools.Dead);
            bool inventory = bools.HasFlag(AnimBools.Inventory);
            bool injured = bools.HasFlag(AnimBools.Injured);
            if (!_paramsApplied || aiming != _appliedAiming) anim.SetBool(P.Aiming, aiming);
            if (!_paramsApplied || dead != _appliedDead) anim.SetBool(P.Dead, dead);
            if (!_paramsApplied || inventory != _appliedInventory) anim.SetBool(P.Inventory, inventory);
            if (!_paramsApplied || injured != _appliedInjured) anim.SetBool(P.Injured, injured);
            _appliedAiming = aiming;
            _appliedDead = dead;
            _appliedInventory = inventory;
            _appliedInjured = injured;
        }

        private void ApplyWeaponParams(Animator anim)
        {
            int active = WeaponUtils.AnimatorBoolHash(_weapon);
            var ids = WeaponUtils.AnimatorBoolHashes;
            for (int i = 0; i < ids.Length; i++)
                anim.SetBool(ids[i], ids[i] == active);
        }

        private void ApplyPendingTriggers(Animator anim, float now)
        {
            if (_edgeTaser) anim.SetTrigger(P.Taser);
            if (_edgeReloadChamber) anim.SetTrigger(P.ReloadChamber);
            var t = _pendingTriggers;
            if (t == 0) return;
            if (t.HasFlag(AnimTriggers.Hurt))
            {
                anim.SetBool(P.Hurt, true);
                _hurtOffAt = now + BoolPulse;
            }
            if (t.HasFlag(AnimTriggers.Pickup))
            {
                anim.SetBool(P.Pickup, true);
                _pickupOffAt = now + BoolPulse;
            }
            if (t.HasFlag(AnimTriggers.Die)) anim.SetTrigger(P.Die);
            if (t.HasFlag(AnimTriggers.Drop)) anim.SetTrigger(P.Drop);
            if (t.HasFlag(AnimTriggers.Injector)) anim.SetTrigger(P.Injector);
            if (t.HasFlag(AnimTriggers.InjectorCancel)) anim.SetTrigger(P.InjectorCancel);
            if (t.HasFlag(AnimTriggers.ReloadTrigger)) anim.SetTrigger(P.Reload);
            if (t.HasFlag(AnimTriggers.AttackTrigger)) anim.SetTrigger(P.Attack);
            if (t.HasFlag(AnimTriggers.SwapTrigger)) anim.SetTrigger(P.Swap);
            if (t.HasFlag(AnimTriggers.StompTrigger)) anim.SetTrigger(P.Stomp);
            if (t.HasFlag(AnimTriggers.PushTrigger)) anim.SetTrigger(P.Push);
        }

        private float _lastLog;
        public void LateTick()
        {
            // Apply bone rotations � interpolate between snapshots
            if (_boneSync != null && _boneSnaps.Count > 0)
                SampleBones(Time.unscaledTime - PluginInfo.PoseInterpDelay);

            if (SyncRADation.ModRuntime.VerboseLogging && Time.unscaledTime - _lastLog > 30f)
            {
                try
                {
                    var sb = new System.Text.StringBuilder("root=");
                    sb.Append(_rootTransform.eulerAngles.ToString("F1"));
                    sb.Append(" bones="); sb.Append(_boneSync != null ? _boneSync.BoneCount.ToString() : "0");
                    for (int i = 0; i < _animators.Length; i++)
                    {
                        var a = _animators[i];
                        if (a == null) continue;
                        sb.Append(" [a"); sb.Append(i);
                        sb.Append("] fwd="); sb.Append(a.GetFloat(P.Forward).ToString("F2"));
                        sb.Append(" turn="); sb.Append(a.GetFloat(P.Turn).ToString("F2"));
                        sb.Append(" aimT="); sb.Append(a.GetFloat(P.AimingTime).ToString("F2"));
                        sb.Append(" aim="); sb.Append(a.GetBool(P.Aiming) ? "1" : "0");
                        sb.Append(" inv="); sb.Append(a.GetBool(P.Inventory) ? "1" : "0");
                        var wpnNames = WeaponUtils.AnimatorBoolNames;
                        var wpnIds = WeaponUtils.AnimatorBoolHashes;
                        for (int wi = 0; wi < wpnNames.Length; wi++)
                        {
                            sb.Append(" ").Append(wpnNames[wi]).Append("=");
                            try { sb.Append(a.GetBool(wpnIds[wi]) ? "1" : "0"); }
                            catch { sb.Append("E"); }
                        }
                        try
                        {
                            var si = a.GetCurrentAnimatorStateInfo(0);
                            sb.Append(" s0=").Append(StateName(si.shortNameHash));
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
                _lastLog = Time.unscaledTime;
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
