// SyncRADation � reads Animator state from local player via individual GetFloat/GetBool (IL2CPP-safe)
using MelonLoader;
using SyncRADation.Networking;
using UnityEngine;

namespace SyncRADation.Players
{
    public static class SourceAnimReader
    {
        private static BoneSyncManager _boneReader;
        private static Transform _facingPivotCache;
        private static Transform _lastPlayerRoot;
        private static AnimBools _lastBools;
        private static AnimTriggers _accumulatedTriggers;
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

        public static void ReadFromPlayer(GameObject player, ref PlayerStateMessage msg)
        {
            Animator anim = null;
            var anims = player.GetComponentsInChildren<Animator>(true);
            for (int i = 0; i < anims.Length; i++)
            {
                if (anims[i] != null && anims[i].runtimeAnimatorController != null)
                {
                    anim = anims[i];
                    break;
                }
            }
            if (anim == null) return;

            // Read floats
            msg.Forward = SafeGetFloat(anim, "Forward");
            msg.Turn = SafeGetFloat(anim, "Turn");
            msg.AimingTime = SafeGetFloat(anim, "AimingTime");
            msg.Stamina = SafeGetFloat(anim, "Stamina");
            msg.Blend = SafeGetFloat(anim, "Blend");
            msg.IKwalk = SafeGetFloat(anim, "IKwalk");
            msg.InputX = SafeGetFloat(anim, "X");
            msg.InputY = SafeGetFloat(anim, "Y");
            msg.HurtTime = SafeGetFloat(anim, "HurtTime");

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
            catch { }

            msg.Climbing = false;
            try
            {
                if (PlayerState.gameState == PlayerState.gameStates.traversing)
                    msg.Climbing = true;
            }
            catch { }
            if (!msg.Climbing)
                msg.Climbing = CrawlMeshActive();

            msg.Weapon = ReadWeaponFromInventory();
            if (msg.Weapon != _lastWeaponRead)
            {
                ModRuntime.Log?.Msg("[WeaponSync] Source weapon: " + _lastWeaponRead + " -> " + msg.Weapon);
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
            try { if (PlayerState.aiming) b |= AnimBools.Aiming; } catch { }

            bool aiming = b.HasFlag(AnimBools.Aiming);
            bool inventory = false;
            try { inventory = SafeGetBool(anim, "Inventory"); } catch { }
            bool playOk = true;
            try
            {
                playOk = PlayerState.gameState == PlayerState.gameStates.play
                    && !PlayerState.reloading;
            }
            catch { }
            bool canFire = aiming && playOk && !inventory;

            // Live round: magAmmo decreased. Empty click is a separate flag — never Fire.
            bool ammoShot = TryDetectAmmoShot();
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
            catch { }

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

            if (SafeGetBool(anim, "Running")) b |= AnimBools.Running;
            try { if (AlternatePlayerController.running) b |= AnimBools.Running; } catch { }
            try { if (PlayerState.charState == PlayerState.charStates.run) b |= AnimBools.Running; } catch { }
            if (SafeGetBool(anim, "Grounded")) b |= AnimBools.Grounded;
            if (SafeGetBool(anim, "Crouch")) b |= AnimBools.Crouch;
            if (SafeGetBool(anim, "Blocked")) b |= AnimBools.Blocked;
            if (SafeGetBool(anim, "Dead")) b |= AnimBools.Dead;
            if (SafeGetBool(anim, "Inventory")) b |= AnimBools.Inventory;
            try { if (PlayerState.reloading) b |= AnimBools.Reload; } catch { }
            try { if (PlayerAttack.reloading) b |= AnimBools.Reload; } catch { }
            if (SafeGetBool(anim, "Attack")) b |= AnimBools.Attack;
            if (SafeGetBool(anim, "Injured")) b |= AnimBools.Injured;
            if (SafeGetBool(anim, "Stomp")) b |= AnimBools.Stomp;
            if (SafeGetBool(anim, "Push")) b |= AnimBools.Push;
            if (SafeGetBool(anim, "Melee")) b |= AnimBools.Melee;
            if (SafeGetBool(anim, "Snap")) b |= AnimBools.Snap;
            if (SafeGetBool(anim, "Reload")) b |= AnimBools.Reload;
            if (SafeGetBool(anim, "Swap")) b |= AnimBools.Swap;
            if (SafeGetBool(anim, "Burst")) b |= AnimBools.Burst;
            if (SafeGetBool(anim, "Taser")) b |= AnimBools.Taser;
            if (SafeGetBool(anim, "Random")) b |= AnimBools.Random;
            if (SafeGetBool(anim, "Hugged")) b |= AnimBools.Hugged;
            if (SafeGetBool(anim, "ReloadRounds")) b |= AnimBools.ReloadRounds;
            if (SafeGetBool(anim, "ReloadChamber")) b |= AnimBools.ReloadChamber;
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
                if (!_lastBools.HasFlag(AnimBools.Burst) && b.HasFlag(AnimBools.Burst))
                    triggers |= AnimTriggers.BurstTrigger;
                if (!_lastBools.HasFlag(AnimBools.Stomp) && b.HasFlag(AnimBools.Stomp))
                    triggers |= AnimTriggers.StompTrigger;
                if (!_lastBools.HasFlag(AnimBools.Push) && b.HasFlag(AnimBools.Push))
                    triggers |= AnimTriggers.PushTrigger;
                if (!_lastBools.HasFlag(AnimBools.Snap) && b.HasFlag(AnimBools.Snap))
                    triggers |= AnimTriggers.SnapTrigger;
                if (!_lastBools.HasFlag(AnimBools.Injured) && b.HasFlag(AnimBools.Injured))
                    triggers |= AnimTriggers.Hurt;
                if (!_lastBools.HasFlag(AnimBools.Dead) && b.HasFlag(AnimBools.Dead))
                    triggers |= AnimTriggers.Die;
            }
            triggers |= _accumulatedTriggers;
            _accumulatedTriggers = 0;
            msg.AnimTriggers = triggers;
            _lastBools = b;
            _hasLast = true;

            if (ModRuntime.VerboseLogging && Time.time - _lastSrcLog > 30f)
            {
                ModRuntime.Log?.Msg("[SRC] shoot=" + (b.HasFlag(AnimBools.Shooting) ? "1" : "0")
                    + " Fire1=" + (Input.GetButton("Fire1") ? "1" : "0")
                    + " Mouse0=" + (Input.GetMouseButton(0) ? "1" : "0")
                    + " aiming=" + (msg.AimingTime > 0.5f ? "1" : "0")
                    + " aimingTime=" + msg.AimingTime.ToString("F2")
                    + " weapon=" + msg.Weapon
                    + " forward=" + msg.Forward.ToString("F2"));
                _lastSrcLog = Time.time;
            }
        }

        public static void AccumulateTrigger(AnimTriggers trigger)
        {
            _accumulatedTriggers |= trigger;
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
            catch { }
            return player.transform.rotation;
        }

        public static void Reset()
        {
            _hasLast = false;
            _lastBools = 0;
            _accumulatedTriggers = 0;
            _prevNormTime = 0f;
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
        }

        static bool CrawlMeshActive()
        {
            if (Time.unscaledTime - _crawlCheckAt < 0.2f)
                return _crawlActive;
            _crawlCheckAt = Time.unscaledTime;
            _crawlActive = false;
            try
            {
                var rooms = UnityEngine.Object.FindObjectsOfType<PEN_CodeRoom>();
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
                        catch { }
                    }
                }
            }
            catch { }
            try
            {
                var ovens = UnityEngine.Object.FindObjectsOfType<DET_Oven>();
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
                        catch { }
                    }
                }
            }
            catch { }
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

        private static float SafeGetFloat(Animator a, string name)
        {
            try { return a.GetFloat(name); }
            catch { return 0f; }
        }

        private static bool SafeGetBool(Animator a, string name)
        {
            try { return a.GetBool(name); }
            catch { return false; }
        }

        private static Networking.WeaponType ReadWeaponFromInventory()
        {
            try
            {
                var equipped = InventoryManager.EquippedWeapon;
                if (equipped == null || equipped.parentItem == null) return Networking.WeaponType.None;
                return Networking.WeaponUtils.ItemToWeaponType(equipped.parentItem._item);
            }
            catch { return Networking.WeaponType.None; }
        }
    }
}