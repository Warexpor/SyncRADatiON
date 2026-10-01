// Remote player sounds: positional FMOD one-shots on the proxy (steps, weapon, hurt, ladder) and the weapon SFX cache.
using System;
using System.Collections.Generic;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Players
{
    public sealed class ProxyAudioSync
    {
        private readonly Transform _proxyTransform;
        private readonly GameObject _audioAnchor;
        private bool _lastAiming;
        private bool _lastEmptyClick;
        private float _lastReloadTime;
        private float _lastHurtTime;
        private WeaponType _lastWeapon;

        private string _footstepPath;
        private string _hurtPath;
        private string _drawSound;
        private string _holsterSound;
        private string _reloadFMODPath;
        private AudioClip _footstepClip;
        private bool _pathsRetried;
        private bool _hasReceivedFirst;

        private const float ReloadCooldown = 1.5f;
        private const float HurtCooldown = 1f;
        // Native Ladder.ClimbLadder plays ONE event per climb (StringLiteral_1607 from the bottom, 1517 from the top;
        // GUID paths from stringliteral.json). There is no "event:/Elster/Ladder/*" in the banks. The cue fires
        // LadderCueDelay after the climb flag rises (the rendered proxy, PoseInterpDelay behind, is at the ladder by
        // then) and takes the end from the nearest Ladder's own top flag.
        private const float LadderCueDelay = 0.15f;
        private const float LadderCueRange = 4f;
        private const string LadderUpPath = "{f1077367-d8e0-482e-bcdb-71ed5ff8a4cf}";
        private const string LadderDownPath = "{a9714f9b-bb15-493e-a9b2-fa95fb8e90d0}";

        // Delayed case-land / pump (proxy has no ParticleCollisionSound MBs)
        private const int PendingCap = 4;
        private readonly float[] _pendingT = new float[PendingCap];
        private readonly string[] _pendingP = new string[PendingCap];
        private const float CaseLandDelay = 0.32f;

        private float _climbTimer;
        private bool _wasClimbing;

        private static bool _weaponCacheBuilt;
        private static readonly Dictionary<WeaponType, string> _shootFMOD = new Dictionary<WeaponType, string>();
        private static readonly Dictionary<WeaponType, string> _reloadFMOD = new Dictionary<WeaponType, string>();
        private static readonly Dictionary<WeaponType, string> _emptyFMOD = new Dictionary<WeaponType, string>();

        // CombatSfxManager paths — weapon secondary action sounds
        private static bool _combatSfxCached;
        private static string _shotgunPumpPath;

        public ProxyAudioSync(GameObject proxy)
        {
            _proxyTransform = proxy.transform;
            _audioAnchor = new GameObject("ProxyAudioAnchor");
            _audioAnchor.transform.SetParent(_proxyTransform, false);
            _audioAnchor.transform.localPosition = Vector3.zero;
            ReadFMODPaths();
        }

        private static GameObject LocalPlayer()
        {
            var p = LanNetworkManager.Instance?.GetLocalPlayer();
            return p != null ? p : PlayerState.player;
        }

        /// <summary>The local Elster's own sound paths: the proxy plays what she would.</summary>
        private void ReadFMODPaths()
        {
            GameObject player = LocalPlayer();
            if (player == null)
            {
                PlaytestLog.Warn("Audio", "no local player for FMOD paths");
                return;
            }
            try
            {
                var efs = player.GetComponentInChildren<ElsterFootstepSFX>(true);
                if (efs != null && efs.stepSource != null && efs.stepSource.clip != null)
                    _footstepClip = efs.stepSource.clip;
                var hs = player.GetComponentInChildren<ElsterHurtSound>(true);
                if (hs != null) _hurtPath = hs.HurtSound;
                var pa = player.GetComponentInChildren<PlayerAttack>(true);
                if (pa != null)
                {
                    _drawSound = pa.drawSound;
                    _holsterSound = pa.holsterSound;
                }
                var sc = player.GetComponentInChildren<StepSoundClass>(true);
                if (sc != null) _footstepPath = sc.audioStep;
                var inv = player.GetComponentInChildren<InventoryBase>(true);
                if (inv != null) _reloadFMODPath = inv.reloadSound;
            }
            catch (Exception e) { Guard.Swallow("ProxyAudio.paths", e); }

            PlaytestLog.Event("Audio", "cached footstep=" + (_footstepPath ?? "null")
                + " hurt=" + (_hurtPath ?? "null")
                + " draw=" + (_drawSound ?? "null"));
        }

        private void EnsurePaths()
        {
            if (!_weaponCacheBuilt) BuildWeaponCache();

            // Retry reading FMOD paths once if all are null (player might not be ready yet)
            if (!_pathsRetried && string.IsNullOrEmpty(_footstepPath) && string.IsNullOrEmpty(_hurtPath) && _footstepClip == null)
            {
                ReadFMODPaths();
                _pathsRetried = true;
            }
        }

        /// <summary>Distance from this proxy to the local Elster (MaxValue when there is none).</summary>
        private float DistToLocal()
        {
            GameObject localPlayer = LocalPlayer();
            if (localPlayer == null) return float.MaxValue;
            return Vector3.Distance(_proxyTransform.position, localPlayer.transform.position);
        }

        // Hearing range per sound type
        private const float NearRange = 40f;
        private const float FarRange = 75f;

        /// <summary>Sounds of the reliable edge cues (shot, reload, hurt).</summary>
        public void OnCues(WeaponType weapon, AvatarCue cues)
        {
            if (cues == AvatarCue.None) return;
            EnsurePaths();
            float dist = DistToLocal();
            bool nearby = dist < NearRange;
            bool farRange = dist < FarRange;
            float now = Time.unscaledTime;

            if (farRange && (cues & AvatarCue.Fire) != 0)
                PlayShootSound(weapon);

            if (farRange && (cues & AvatarCue.Reload) != 0 && now - _lastReloadTime > ReloadCooldown)
            {
                PlayReloadSound(weapon);
                _lastReloadTime = now;
            }

            if (nearby && (cues & AvatarCue.Hurt) != 0 && now - _lastHurtTime > HurtCooldown)
            {
                PlayFMODAttached(_hurtPath, 0.5f);
                _lastHurtTime = now;
            }
        }

        /// <summary>Per received pose: state-edge sounds (empty click, draw, footsteps, swap, ladder start).</summary>
        public void Tick(PlayerStateMessage state)
        {
            EnsurePaths();
            var flags = state.Flags;
            bool aiming = (flags & PoseFlags.Aiming) != 0;
            bool emptyClick = (flags & PoseFlags.EmptyClick) != 0;
            bool climbing = (flags & PoseFlags.Climbing) != 0;

            float distToLocal = DistToLocal();
            bool nearby = distToLocal < NearRange;
            bool farRange = distToLocal < FarRange;

            if (nearby && emptyClick && !_lastEmptyClick)
                PlayEmptySound(state.Weapon);
            if (nearby && aiming && !_lastAiming)
                PlayFMODAttached(_drawSound, 0.25f);
            _lastEmptyClick = emptyClick;
            _lastAiming = aiming;

            // Footsteps: Step from the sender's locomotion loop; run vs walk is an FMOD param.
            if (nearby && (flags & PoseFlags.Step) != 0)
            {
                bool running = (flags & PoseFlags.Running) != 0;
                float vol = running ? 0.7f : 0.4f + state.ForwardAmount * 0.2f;
                if (!string.IsNullOrEmpty(_footstepPath))
                    WorldSfx.PlayFootstep(_footstepPath, _audioAnchor.transform, running, vol);
                else if (_footstepClip != null)
                    AudioSource.PlayClipAtPoint(_footstepClip, _proxyTransform.position, vol * (running ? 0.85f : 0.6f));
            }

            // Weapon swap: holster / draw. The first pose only sets the baseline; a swap out of range is not replayed later.
            if (state.Weapon != _lastWeapon)
            {
                if (_hasReceivedFirst && farRange)
                    PlayFMODAttached(state.Weapon == WeaponType.None ? _holsterSound : _drawSound, 0.3f);
                _lastWeapon = state.Weapon;
            }
            _hasReceivedFirst = true;

            // Ladder climb start: same 3D falloff pipeline as doors. The cue itself plays from LateTick.
            if (climbing && !_wasClimbing)
                _climbTimer = LadderCueDelay;
            else if (!climbing)
                _climbTimer = 0f;
            _wasClimbing = climbing;
        }

        /// <summary>Every frame (RemotePlayerProxy.LateTick): delayed cues on real time, packets or not.</summary>
        public void LateTick()
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            if (_climbTimer > 0f)
            {
                _climbTimer -= dt;
                if (_climbTimer <= 0f)
                    PlayLadderCue();
            }
            TickPendingSfx(dt);
        }

        /// <summary>
        /// Native Ladder.ClimbLadder: no sound for a hole drop or noClimbSFX, else StringLiteral_1607 when the ladder
        /// is not the top end, 1517 when it is (Ghidra Ladder.c). The climbing peer stands at the ladder it used.
        /// Climbing without a ladder nearby (crawl spaces) has no cue.
        /// </summary>
        private void PlayLadderCue()
        {
            Ladder best = null;
            try
            {
                var ladders = WorldLookup.All<Ladder>();
                Vector3 p = _proxyTransform.position;
                float bestD = LadderCueRange * LadderCueRange;
                for (int i = 0; ladders != null && i < ladders.Length; i++)
                {
                    var l = ladders[i];
                    if (l == null) continue;
                    Vector3 d = l.transform.position - p;
                    d.z = 0f; // planar distance (up is -Z)
                    float sq = d.sqrMagnitude;
                    if (sq < bestD) { bestD = sq; best = l; }
                }
                if (best == null || best.hole || best.noClimbSFX) return;
                WorldSfx.Play(best.top ? LadderDownPath : LadderUpPath, _audioAnchor.transform, 0.5f);
            }
            catch (Exception e) { Guard.Swallow(e); }
        }

        public void OnWeaponShot(WeaponType weapon)
        {
            if (!_combatSfxCached) BuildCombatSfxCache();
            QueueSfx(CaseEvent(weapon), CaseLandDelay);
            if (weapon == WeaponType.Shotgun)
                QueueSfx(_shotgunPumpPath, 0.45f);
        }

        private void QueueSfx(string path, float delay)
        {
            if (string.IsNullOrEmpty(path)) return;
            for (int i = 0; i < PendingCap; i++)
            {
                if (_pendingP[i] != null) continue;
                _pendingT[i] = delay;
                _pendingP[i] = path;
                return;
            }
        }

        private void TickPendingSfx(float dt)
        {
            for (int i = 0; i < PendingCap; i++)
            {
                if (_pendingP[i] == null) continue;
                _pendingT[i] -= dt;
                if (_pendingT[i] > 0f) continue;
                PlayFMODAttached(_pendingP[i], 0.35f);
                _pendingP[i] = null;
            }
        }

        private static string CaseEvent(WeaponType weapon)
        {
            switch (weapon)
            {
                case WeaponType.Pistol: return "event:/Elster/Weapons/Pistol/Case";
                case WeaponType.Revolver: return "event:/Elster/Weapons/Revolver/Case";
                case WeaponType.Rifle: return "event:/Elster/Weapons/Rifle/Case";
                case WeaponType.SMG: return "event:/Elster/Weapons/SMG/Case";
                case WeaponType.Shotgun: return "event:/Elster/Weapons/Shotgun/Shell";
                case WeaponType.Flare:
                case WeaponType.CAR: return "event:/Elster/Weapons/FlareGun/Case";
                default: return null;
            }
        }

        private void PlayShootSound(WeaponType weapon)
        {
            string path;
            if (_shootFMOD.TryGetValue(weapon, out path))
                PlayFMODAttached(path, 0.55f, WorldSfx.CombatRange);
        }

        private void PlayEmptySound(WeaponType weapon)
        {
            string path;
            if (_emptyFMOD.TryGetValue(weapon, out path))
                PlayFMODAttached(path, 0.4f);
        }

        private void PlayReloadSound(WeaponType weapon)
        {
            string path;
            if (_reloadFMOD.TryGetValue(weapon, out path))
                PlayFMODAttached(path, 0.4f);
            else
                PlayFMODAttached(_reloadFMODPath, 0.4f);
        }

        private void PlayFMODAttached(string path, float volume, float range = WorldSfx.Range)
        {
            if (string.IsNullOrEmpty(path) || _audioAnchor == null) return;
            WorldSfx.Play(path, _audioAnchor.transform, volume, range);
        }

        private static void BuildWeaponCache()
        {
            _weaponCacheBuilt = true;
            try
            {
                var weapons = Resources.FindObjectsOfTypeAll<AnWeapon>();
                if (weapons == null) return;

                int shotCount = 0, reloadCount = 0;
                foreach (var w in weapons)
                {
                    if (w == null || w.parentItem == null) continue;
                    WeaponType wt = WeaponUtils.ItemToWeaponType(w.parentItem._item);
                    if (wt == WeaponType.None) continue;
                    if (!string.IsNullOrEmpty(w.shotMod) && !_shootFMOD.ContainsKey(wt))
                    {
                        _shootFMOD[wt] = w.shotMod;
                        shotCount++;
                    }
                    if (!string.IsNullOrEmpty(w.reloadMod) && !_reloadFMOD.ContainsKey(wt))
                    {
                        _reloadFMOD[wt] = w.reloadMod;
                        reloadCount++;
                    }
                    if (!string.IsNullOrEmpty(w.emptyMod) && !_emptyFMOD.ContainsKey(wt))
                        _emptyFMOD[wt] = w.emptyMod;
                }

                PlaytestLog.Event("Audio", "weapon FMOD shot=" + shotCount + " reload=" + reloadCount);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[Audio] BuildWeaponCache failed: " + ex.Message);
            }
        }

        private static void BuildCombatSfxCache()
        {
            _combatSfxCached = true;
            try
            {
                var all = Resources.FindObjectsOfTypeAll<CombatSfxManager>();
                if (all == null || all.Length == 0)
                {
                    PlaytestLog.Verbose("Audio", "CombatSfxManager not found");
                    return;
                }
                _shotgunPumpPath = all[0].ShotgunPump;
                PlaytestLog.Verbose("Audio", "CombatSfx shotgunPump=" + (_shotgunPumpPath ?? "null"));
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[Audio] BuildCombatSfxCache failed: " + ex.Message);
            }
        }
    }
}
