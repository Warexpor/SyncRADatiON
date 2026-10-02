// Local Elster -> PlayerState pose fields + edge cues, read once per 30 Hz send with no per-send allocation.
using SyncRADation.Config;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Players
{
    public static class SourceAnimReader
    {
        // ElsterNewController parameters still read (hashed once; AssetRipper AnimatorController/ElsterNewController).
        private static readonly int ForwardHash = Animator.StringToHash("Forward");
        private static readonly int AimingTimeHash = Animator.StringToHash("AimingTime");
        private static readonly int InventoryHash = Animator.StringToHash("Inventory");
        private static readonly int InjuredHash = Animator.StringToHash("Injured");
        // Bool pulse: native ElsterHurtAnimation.hurt sets it per hit and clears it 0.2 s later (Ghidra <Callback>d__17).
        private static readonly int HurtHash = Animator.StringToHash("Hurt");
        // Trigger, read as a bool: set until the controller's transition consumes it.
        private static readonly int ReloadHash = Animator.StringToHash("Reload");

        // Per player object (looked up once, not on every send).
        private static GameObject _player;
        private static Animator _anim;
        private static Transform _model;
        private static Transform _hips;
        private static CharacterModelType _outfit;
        private static readonly BoneSyncManager _bones = new BoneSyncManager();

        private static float _prevLoopTime;
        private static bool _hasLast;
        private static bool _lastInjured;
        private static bool _lastHurt;
        private static bool _lastReload;
        private static bool _lastTriggerHeld;
        private static WeaponType _lastWeapon;
        private static int _lastMagAmmo;
        private static bool _hasMagAmmo;
        private static bool _magRefilled;
        private static float _crawlCheckAt;
        private static bool _crawlActive;

        /// <summary>
        /// Shot serial: +1 for every pose tick that saw the equipped weapon's magAmmo drop (a live round). Local
        /// friendly fire keys off this edge, the same one that sends AvatarCue.Fire. Intentionally persistent:
        /// readers compare it with the value they last saw, never with zero.
        /// </summary>
        public static int ShotSerial { get; private set; }

        /// <summary>Fills the pose fields of <paramref name="msg"/> (all but position / velocity / sender) and returns this tick's edge cues.</summary>
        public static AvatarCue Read(GameObject player, ref PlayerStateMessage msg)
        {
            if (_player != player) Bind(player);
            if (_model != null) msg.SetFacingWorld(_model.rotation);
            // Bone indices match because the proxy is a clone of this same model.
            msg.BoneRotations = _bones.ReadRotations();
            // The one positional channel the Animator writes; rotations alone would leave the proxy hips at bind height.
            if (_hips != null)
            {
                var hp = _hips.localPosition;
                msg.HipsX = hp.x;
                msg.HipsY = hp.y;
                msg.HipsZ = hp.z;
                msg.Flags |= PoseFlags.HasHips;
                if (ModConfig.DiagnosticsOn)
                    FlickerTrace.SelfBob(player.transform.position.z, player.transform.position.z - _hips.position.z);
            }

            AvatarCue cues = AvatarCue.None;
            try
            {
                if (_outfit != null) msg.ModelState = (byte)_outfit.modelState;
                if (CharacterModelType.wearHat) msg.Flags |= PoseFlags.WearHat;
                if (AlternatePlayerController.running || PlayerState.charState == PlayerState.charStates.run)
                    msg.Flags |= PoseFlags.Running;
                if (PlayerState.gameState == PlayerState.gameStates.traversing || CrawlActive())
                    msg.Flags |= PoseFlags.Climbing;

                msg.Weapon = WeaponUtils.EquippedWeaponType();
                if (msg.Weapon != _lastWeapon)
                {
                    PlaytestLog.Event("Weapon", "local " + _lastWeapon + " -> " + msg.Weapon);
                    _lastWeapon = msg.Weapon;
                    _hasMagAmmo = false; // new magazine baseline
                }
                var equipped = InventoryManager.EquippedWeapon;
                if (ReadMagShot(equipped))
                {
                    ShotSerial++;
                    cues |= AvatarCue.Fire;
                }

                bool reload = PlayerState.reloading || PlayerAttack.reloading;
                bool aiming = PlayerState.aiming;
                bool inventory = false, injured = false, hurt = false;
                if (_anim != null)
                {
                    float forward = _anim.GetFloat(ForwardHash);
                    msg.SetForward(forward);
                    aiming |= _anim.GetFloat(AimingTimeHash) > 0.5f;
                    inventory = _anim.GetBool(InventoryHash);
                    injured = _anim.GetBool(InjuredHash);
                    hurt = _anim.GetBool(HurtHash);
                    reload |= _anim.GetBool(ReloadHash);
                    // A step lands when the base-layer locomotion loop crosses its half or wraps.
                    var st = _anim.GetCurrentAnimatorStateInfo(0);
                    float loop = st.normalizedTime - Mathf.Floor(st.normalizedTime);
                    if (forward > 0.05f && ((_prevLoopTime < 0.5f && loop >= 0.5f) || (loop < _prevLoopTime && loop < 0.3f)))
                        msg.Flags |= PoseFlags.Step;
                    _prevLoopTime = loop;
                }
                if (aiming) msg.Flags |= PoseFlags.Aiming;

                // Dry fire: a fresh trigger press with an empty magazine while able to shoot (a live round is Fire).
                bool triggerHeld = Input.GetButton("Fire1") || Input.GetMouseButton(0);
                bool canFire = aiming && !inventory && !PlayerState.reloading && PlayerState.gameState == PlayerState.gameStates.play;
                bool gun = msg.Weapon != WeaponType.None && msg.Weapon != WeaponType.Melee;
                if (canFire && gun && triggerHeld && !_lastTriggerHeld && _hasMagAmmo && equipped != null && equipped.magAmmo <= 0)
                    msg.Flags |= PoseFlags.EmptyClick;
                _lastTriggerHeld = triggerHeld;

                if (_magRefilled || (_hasLast && reload && !_lastReload))
                    cues |= AvatarCue.Reload;
                _magRefilled = false;
                if (_hasLast && ((injured && !_lastInjured) || (hurt && !_lastHurt)))
                    cues |= AvatarCue.Hurt;
                _lastReload = reload;
                _lastInjured = injured;
                _lastHurt = hurt;
                _hasLast = true;
            }
            catch (System.Exception e) { Guard.Swallow("SourceAnim.Read", e); }
            return cues;
        }

        /// <summary>World rotation of the local Elster model (its facing pivot); the root's when there is none.</summary>
        public static Quaternion ReadFacingWorldRotation(GameObject player)
        {
            if (_player != player) Bind(player);
            return _model != null ? _model.rotation : player.transform.rotation;
        }

        public static void Reset()
        {
            _player = null;
            _anim = null;
            _model = null;
            _hips = null;
            _outfit = null;
            _bones.FindArmature(null);
            _prevLoopTime = 0f;
            _hasLast = false;
            _lastInjured = false;
            _lastHurt = false;
            _lastReload = false;
            _lastTriggerHeld = false;
            _lastWeapon = WeaponType.None;
            _hasMagAmmo = false;
            _magRefilled = false;
            _crawlCheckAt = 0f;
            _crawlActive = false;
        }

        private static void Bind(GameObject player)
        {
            _player = player;
            _anim = null;
            _hips = null;
            _model = BoneSyncManager.FindModelRoot(player.transform);
            _bones.FindArmature(_model);
            _outfit = player.GetComponentInChildren<CharacterModelType>(true);
            var anims = player.GetComponentsInChildren<Animator>(true);
            for (int i = 0; i < anims.Length && _anim == null; i++)
            {
                if (anims[i] != null && anims[i].runtimeAnimatorController != null)
                    _anim = anims[i];
            }
            if (_anim != null && _anim.isHuman)
                _hips = _anim.GetBoneTransform(HumanBodyBones.Hips);
        }

        /// <summary>
        /// True once per tick in which the equipped magazine lost rounds (one visual shot even for a multi-round
        /// burst; full-auto re-samples next tick). A rise is a reload / swap: flagged for the Reload cue, not a shot.
        /// </summary>
        private static bool ReadMagShot(AnWeapon equipped)
        {
            if (equipped == null)
            {
                _hasMagAmmo = false;
                return false;
            }
            int mag = equipped.magAmmo;
            if (!_hasMagAmmo)
            {
                _lastMagAmmo = mag;
                _hasMagAmmo = true;
                return false;
            }
            int last = _lastMagAmmo;
            _lastMagAmmo = mag;
            if (mag > last) _magRefilled = true;
            return mag < last;
        }

        /// <summary>The crawl-space Elster (PEN_CodeRoom / DET_Oven crawlPlayer) is active: polled at 5 Hz.</summary>
        private static bool CrawlActive()
        {
            if (Time.unscaledTime - _crawlCheckAt < 0.2f)
                return _crawlActive;
            _crawlCheckAt = Time.unscaledTime;
            _crawlActive = false;
            var rooms = WorldLookup.All<PEN_CodeRoom>();
            for (int i = 0; rooms != null && i < rooms.Length && !_crawlActive; i++)
            {
                var r = rooms[i];
                _crawlActive = r != null && r.crawlPlayer != null && r.crawlPlayer.activeInHierarchy;
            }
            var ovens = WorldLookup.All<DET_Oven>();
            for (int i = 0; ovens != null && i < ovens.Length && !_crawlActive; i++)
            {
                var o = ovens[i];
                _crawlActive = o != null && o.crawlPlayer != null && o.crawlPlayer.activeInHierarchy;
            }
            return _crawlActive;
        }
    }
}
