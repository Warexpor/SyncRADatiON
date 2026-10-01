// SyncRADation � reads Animator state from local player via individual GetFloat/GetBool (IL2CPP-safe)
using MelonLoader;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Players
{
    public static class SourceAnimReader
    {
        private static BoneSyncManager _boneReader;
        private static Transform _facingPivotCache;
        private static Transform _lastPlayerRoot;
        private static AnimBools _lastBools;
        private static bool _hasLast;
        private static float _prevNormTime;
        private static Networking.WeaponType _lastWeaponRead;
        private static float _lastSrcLog;
        private static int _lastMagAmmo = -1;
        private static bool _hasMagAmmo;
        private static bool _lastTriggerHeld;
        private static bool _magReloadPulse;
        private static float _crawlCheckAt;
        private static bool _crawlActive;
        private static Animator _hipsAnim;
        private static Transform _hips;

        /// <summary>
        /// Shot serial: +1 for every pose tick that saw the equipped weapon's magAmmo drop (a live round). Local
        /// friendly fire keys off this edge, the same one that sends AnimTriggers.Fire. Intentionally persistent:
        /// readers compare it with the value they last saw, never with zero.
        /// </summary>
        public static int ShotSerial { get; private set; }

        private static GameObject _animFor;
        private static Animator _anim;

        // Animator.StringToHash once. Only parameters that exist in ElsterNewController; the wire fields for
        // names it does not have (Blend, IKwalk, X, Y, Running/Grounded/Crouch/... bools) stay 0.
        private static class P
        {
            public static readonly int Forward = Animator.StringToHash("Forward");
            public static readonly int Turn = Animator.StringToHash("Turn");
            public static readonly int AimingTime = Animator.StringToHash("AimingTime");
            public static readonly int Stamina = Animator.StringToHash("Stamina");
            public static readonly int HurtTime = Animator.StringToHash("HurtTime");
            public static readonly int Dead = Animator.StringToHash("Dead");
            public static readonly int Inventory = Animator.StringToHash("Inventory");
            public static readonly int Injured = Animator.StringToHash("Injured");
            // Trigger params read as bools (set until a transition consumes them).
            public static readonly int Attack = Animator.StringToHash("Attack");
            public static readonly int Stomp = Animator.StringToHash("Stomp");
            public static readonly int Push = Animator.StringToHash("Push");
            public static readonly int Reload = Animator.StringToHash("Reload");
            public static readonly int Swap = Animator.StringToHash("Swap");
            public static readonly int ReloadChamber = Animator.StringToHash("ReloadChamber");
            public static readonly int Taser = Animator.StringToHash("Tools/Taser");
        }

        /// <summary>The local Elster's controller-driven Animator, looked up once per player object.</summary>
        private static Animator SourceAnimator(GameObject player)
        {
            if (_animFor == player && _anim != null) return _anim;
            _animFor = player;
            _anim = null;
            var anims = player.GetComponentsInChildren<Animator>(true);
            for (int i = 0; i < anims.Length; i++)
            {
                if (anims[i] != null && anims[i].runtimeAnimatorController != null)
                {
                    _anim = anims[i];
                    break;
                }
            }
            return _anim;
        }

        public static void ReadFromPlayer(GameObject player, ref PlayerStateMessage msg)
        {
            Animator anim = SourceAnimator(player);
            if (anim == null) return;

            // Read floats
            msg.Forward = SafeGetFloat(anim, P.Forward);
            msg.Turn = SafeGetFloat(anim, P.Turn);
            msg.AimingTime = SafeGetFloat(anim, P.AimingTime);
            msg.Stamina = SafeGetFloat(anim, P.Stamina);
            msg.HurtTime = SafeGetFloat(anim, P.HurtTime);

            // Detect footsteps from animation normalizedTime (base layer)
            try
            {
                var stateInfo = anim.GetCurrentAnimatorStateInfo(0);
                float nt = stateInfo.normalizedTime - Mathf.Floor(stateInfo.normalizedTime);
                msg.StepHappened = false;
                if (msg.Forward > 0.05f)
                {
                    if ((_prevNormTime < 0.5f && nt >= 0.5f) || (nt < _prevNormTime && nt < 0.3f))
                        msg.StepHappened = true;
                }
                _prevNormTime = nt;
            }
            catch (System.Exception e) { Guard.Swallow(e); }

            msg.Climbing = false;
            try
            {
                if (PlayerState.gameState == PlayerState.gameStates.traversing)
                    msg.Climbing = true;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            if (!msg.Climbing)
                msg.Climbing = CrawlMeshActive();

            msg.Weapon = Networking.WeaponUtils.EquippedWeaponType();
            if (msg.Weapon != _lastWeaponRead)
            {
                PlaytestLog.Event("Weapon", "local " + _lastWeaponRead + " -> " + msg.Weapon);
                _lastWeaponRead = msg.Weapon;
                _hasMagAmmo = false; // resync mag baseline on weapon swap
                _lastMagAmmo = -1;
            }

            // Read bools
            // IL2CPP: Animator.GetBool for Aiming/Shooting is unreliable.
            // Aiming: AimingTime float. Shot edge: equipped magAmmo decrease → AnimTriggers.Fire.
            AnimBools b = 0;

            if (msg.AimingTime > 0.5f)
                b |= AnimBools.Aiming;
            try { if (PlayerState.aiming) b |= AnimBools.Aiming; } catch (System.Exception e) { Guard.Swallow(e); }

            bool aiming = b.HasFlag(AnimBools.Aiming);
            bool inventory = SafeGetBool(anim, P.Inventory);
            bool playOk = true;
            try
            {
                playOk = PlayerState.gameState == PlayerState.gameStates.play
                    && !PlayerState.reloading;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            bool canFire = aiming && playOk && !inventory;

            // Live round: magAmmo decreased. Empty click is a separate flag — never Fire.
            bool ammoShot = TryDetectAmmoShot();
            if (ammoShot) ShotSerial++;
            bool triggerHeld = Input.GetButton("Fire1") || Input.GetMouseButton(0);
            bool triggerEdge = triggerHeld && !_lastTriggerHeld;
            _lastTriggerHeld = triggerHeld;
            bool magEmpty = false;
            try
            {
                var eq = InventoryManager.EquippedWeapon;
                if (eq != null && _hasMagAmmo)
                    magEmpty = eq.magAmmo <= 0;
            }
            catch (System.Exception e) { Guard.Swallow(e); }

            if (ammoShot)
                b |= AnimBools.Shooting;
            else if (canFire && triggerHeld && !magEmpty && _hasMagAmmo)
                b |= AnimBools.Shooting;
            else if (canFire && triggerHeld && !_hasMagAmmo && msg.Weapon != Networking.WeaponType.None
                && msg.Weapon != Networking.WeaponType.Melee)
                b |= AnimBools.Shooting;

            if (canFire && triggerEdge && magEmpty && _hasMagAmmo
                && msg.Weapon != Networking.WeaponType.None
                && msg.Weapon != Networking.WeaponType.Melee)
                b |= AnimBools.EmptyClick;

            // Running is not an ElsterNewController param: native run state only (proxy footstep loudness).
            try { if (AlternatePlayerController.running) b |= AnimBools.Running; } catch (System.Exception e) { Guard.Swallow(e); }
            try { if (PlayerState.charState == PlayerState.charStates.run) b |= AnimBools.Running; } catch (System.Exception e) { Guard.Swallow(e); }
            if (SafeGetBool(anim, P.Dead)) b |= AnimBools.Dead;
            if (inventory) b |= AnimBools.Inventory;
            try { if (PlayerState.reloading) b |= AnimBools.Reload; } catch (System.Exception e) { Guard.Swallow(e); }
            try { if (PlayerAttack.reloading) b |= AnimBools.Reload; } catch (System.Exception e) { Guard.Swallow(e); }
            if (SafeGetBool(anim, P.Attack)) b |= AnimBools.Attack;
            if (SafeGetBool(anim, P.Injured)) b |= AnimBools.Injured;
            if (SafeGetBool(anim, P.Stomp)) b |= AnimBools.Stomp;
            if (SafeGetBool(anim, P.Push)) b |= AnimBools.Push;
            if (SafeGetBool(anim, P.Reload)) b |= AnimBools.Reload;
            if (SafeGetBool(anim, P.Swap)) b |= AnimBools.Swap;
            if (SafeGetBool(anim, P.Taser)) b |= AnimBools.Taser;
            if (SafeGetBool(anim, P.ReloadChamber)) b |= AnimBools.ReloadChamber;
            msg.AnimBools = b;

            // Read all armature bone rotations from the facing pivot child only
            // (same hierarchy that model-only proxy clones — ensures indices match)
            if (_lastPlayerRoot != player.transform)
            {
                _lastPlayerRoot = player.transform;
                _facingPivotCache = FindFacingPivot(player.transform);
                if (_facingPivotCache != null)
                {
                    _boneReader = new BoneSyncManager();
                    _boneReader.FindArmature(_facingPivotCache.gameObject);
                }
            }
            if (_boneReader != null)
                msg.BoneRotations = _boneReader.ReadRotations();

            // Humanoid hips position: the one positional channel the Animator writes; rotations alone leave the
            // proxy's hips at its own (drifting) Animator height.
            if (_hipsAnim != anim)
            {
                _hipsAnim = anim;
                _hips = null;
                try { if (anim.isHuman) _hips = anim.GetBoneTransform(HumanBodyBones.Hips); }
                catch (System.Exception e) { Guard.Swallow(e); }
            }
            if (_hips != null)
            {
                var hp = _hips.localPosition;
                msg.HasHips = true;
                msg.HipsX = hp.x;
                msg.HipsY = hp.y;
                msg.HipsZ = hp.z;
            }

            // Detect triggers: if a bool changed from false→true, fire the trigger
            AnimTriggers triggers = 0;
            if (ammoShot)
                triggers |= AnimTriggers.Fire;
            if (_magReloadPulse)
            {
                triggers |= AnimTriggers.ReloadTrigger;
                _magReloadPulse = false;
            }
            if (_hasLast)
            {
                if (!_lastBools.HasFlag(AnimBools.Reload) && b.HasFlag(AnimBools.Reload))
                    triggers |= AnimTriggers.ReloadTrigger;
                if (!_lastBools.HasFlag(AnimBools.Attack) && b.HasFlag(AnimBools.Attack))
                    triggers |= AnimTriggers.AttackTrigger;
                if (!_lastBools.HasFlag(AnimBools.Swap) && b.HasFlag(AnimBools.Swap))
                    triggers |= AnimTriggers.SwapTrigger;
                if (!_lastBools.HasFlag(AnimBools.Stomp) && b.HasFlag(AnimBools.Stomp))
                    triggers |= AnimTriggers.StompTrigger;
                if (!_lastBools.HasFlag(AnimBools.Push) && b.HasFlag(AnimBools.Push))
                    triggers |= AnimTriggers.PushTrigger;
                if (!_lastBools.HasFlag(AnimBools.Injured) && b.HasFlag(AnimBools.Injured))
                    triggers |= AnimTriggers.Hurt;
                if (!_lastBools.HasFlag(AnimBools.Dead) && b.HasFlag(AnimBools.Dead))
                    triggers |= AnimTriggers.Die;
            }
            msg.AnimTriggers = triggers;
            _lastBools = b;
            _hasLast = true;

            if (ModRuntime.VerboseLogging && Time.time - _lastSrcLog > 30f)
            {
                PlaytestLog.Verbose("SRC", "shoot=" + (b.HasFlag(AnimBools.Shooting) ? "1" : "0")
                    + " Fire1=" + (Input.GetButton("Fire1") ? "1" : "0")
                    + " Mouse0=" + (Input.GetMouseButton(0) ? "1" : "0")
                    + " aiming=" + (msg.AimingTime > 0.5f ? "1" : "0")
                    + " aimingTime=" + msg.AimingTime.ToString("F2")
                    + " weapon=" + msg.Weapon
                    + " forward=" + msg.Forward.ToString("F2"));
                _lastSrcLog = Time.time;
            }
        }

        public static Quaternion ReadFacingWorldRotation(GameObject player)
        {
            if (player == null) return Quaternion.identity;
            try
            {
                if (_facingPivotCache == null || _lastPlayerRoot != player.transform)
                    _facingPivotCache = FindFacingPivot(player.transform);
                if (_facingPivotCache != null)
                    return _facingPivotCache.rotation;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return player.transform.rotation;
        }

        public static void Reset()
        {
            _hasLast = false;
            _lastBools = 0;
            _prevNormTime = 0f;
            _animFor = null;
            _anim = null;
            _facingPivotCache = null;
            _lastPlayerRoot = null;
            _boneReader = null;
            _lastWeaponRead = 0;
            _lastMagAmmo = -1;
            _hasMagAmmo = false;
            _lastTriggerHeld = false;
            _magReloadPulse = false;
            _crawlCheckAt = 0f;
            _crawlActive = false;
            _hipsAnim = null;
            _hips = null;
        }

        static bool CrawlMeshActive()
        {
            if (Time.unscaledTime - _crawlCheckAt < 0.2f)
                return _crawlActive;
            _crawlCheckAt = Time.unscaledTime;
            _crawlActive = false;
            try
            {
                var rooms = SyncRADation.Sync.WorldLookup.All<PEN_CodeRoom>();
                if (rooms != null)
                {
                    for (int i = 0; i < rooms.Length; i++)
                    {
                        var r = rooms[i];
                        if (r == null) continue;
                        try
                        {
                            if (r.crawlPlayer != null && r.crawlPlayer.activeInHierarchy)
                            {
                                _crawlActive = true;
                                return true;
                            }
                        }
                        catch (System.Exception e) { Guard.Swallow(e); }
                    }
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                var ovens = SyncRADation.Sync.WorldLookup.All<DET_Oven>();
                if (ovens != null)
                {
                    for (int i = 0; i < ovens.Length; i++)
                    {
                        var o = ovens[i];
                        if (o == null) continue;
                        try
                        {
                            if (o.crawlPlayer != null && o.crawlPlayer.activeInHierarchy)
                            {
                                _crawlActive = true;
                                return true;
                            }
                        }
                        catch (System.Exception e) { Guard.Swallow(e); }
                    }
                }
            }
            catch (System.Exception e) { Guard.Swallow(e); }
            return false;
        }

        /// <summary>True once per expended round when EquippedWeapon.magAmmo decreases.</summary>
        private static bool TryDetectAmmoShot()
        {
            try
            {
                var equipped = InventoryManager.EquippedWeapon;
                if (equipped == null)
                {
                    _hasMagAmmo = false;
                    _lastMagAmmo = -1;
                    return false;
                }
                int mag = equipped.magAmmo;
                if (!_hasMagAmmo)
                {
                    _lastMagAmmo = mag;
                    _hasMagAmmo = true;
                    return false;
                }
                // Reload / swap can increase mag — resync, not a shot
                if (mag > _lastMagAmmo)
                {
                    if (_hasMagAmmo)
                        _magReloadPulse = true;
                    _lastMagAmmo = mag;
                    return false;
                }
                if (mag < _lastMagAmmo)
                {
                    int spent = _lastMagAmmo - mag;
                    _lastMagAmmo = mag;
                    // One network pulse per state tick; multi-round spend still one visual shot (full-auto re-samples next tick)
                    return spent > 0;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static Transform FindFacingPivot(Transform root)
        {
            if (root == null) return null;
            for (int i = 0; i < root.childCount; i++)
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
            for (int i = 0; i < t.childCount; i++)
            {
                if (HasSkinnedMeshInDescendants(t.GetChild(i)))
                    return true;
            }
            return false;
        }

        private static float SafeGetFloat(Animator a, int id)
        {
            try { return a.GetFloat(id); }
            catch { return 0f; }
        }

        private static bool SafeGetBool(Animator a, int id)
        {
            try { return a.GetBool(id); }
            catch { return false; }
        }
    }
}