using System;
using System.Collections.Generic;
using FMODUnity;
using SyncRADation.Networking;
using UnityEngine;

namespace SyncRADation.Players
{
    public sealed class ProxyAudioSync
    {
        private Transform _proxyTransform;
        private bool _lastShooting;
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
        private const float LadderClimbInterval = 0.55f;
        private const string LadderUpPath = "event:/Elster/Ladder/Up";
        private const string LadderDownPath = "event:/Elster/Ladder/Down";

        private float _climbTimer;
        private Vector3 _lastPos;
        private bool _wasClimbing;

        private static bool _weaponCacheBuilt;
        private static readonly Dictionary<WeaponType, string> _shootFMOD = new Dictionary<WeaponType, string>();
        private static readonly Dictionary<WeaponType, string> _reloadFMOD = new Dictionary<WeaponType, string>();

        public ProxyAudioSync(GameObject proxy)
        {
            _proxyTransform = proxy.transform;
            ReadFMODPaths();
        }

        private void ReadFMODPaths()
        {
            GameObject player = null;
            try { player = LanNetworkManager.Instance?.GetLocalPlayer(); } catch { }
            if (player == null) try { player = PlayerState.player; } catch { }

            if (player == null)
            {
                ModRuntime.Log?.Msg("[Audio] Cannot read FMOD paths: no local player");
                return;
            }

            var sb = new System.Text.StringBuilder("[Audio] Scanning player '" + player.name + "':");

            // List all components on player
            sb.Append("\n  Components on root:");
            foreach (var c in player.GetComponents<Component>())
            {
                if (c != null) sb.Append("\n    ").Append(c.GetType().Name);
            }
            sb.Append("\n  Components in children:");
            foreach (var c in player.GetComponentsInChildren<Component>(true))
            {
                if (c != null) sb.Append("\n    ").Append(c.GetType().Name);
            }

            // Find footstep AudioClip from ElsterFootstepSFX
            try
            {
                var efs = player.GetComponentInChildren<ElsterFootstepSFX>(true);
                if (efs != null && efs.stepSource != null && efs.stepSource.clip != null)
                {
                    _footstepClip = efs.stepSource.clip;
                    sb.Append("\n  ElsterFootstepSFX.stepSource.clip = \"").Append(_footstepClip.name).Append("\"");
                }
                else sb.Append("\n  ElsterFootstepSFX: NOT FOUND or no clip");
            }
            catch (Exception ex) { sb.Append("\n  ElsterFootstepSFX error: ").Append(ex.Message); }

            // Find specific FMOD sound components
            try
            {
                var hs = player.GetComponentInChildren<ElsterHurtSound>(true);
                if (hs != null) { _hurtPath = hs.HurtSound; sb.Append("\n  ElsterHurtSound.HurtSound = \"").Append(_hurtPath).Append("\""); }
                else sb.Append("\n  ElsterHurtSound: NOT FOUND");
            }
            catch (Exception ex) { sb.Append("\n  ElsterHurtSound error: ").Append(ex.Message); }

            try
            {
                var pa = player.GetComponentInChildren<PlayerAttack>(true);
                if (pa != null)
                {
                    _drawSound = pa.drawSound; _holsterSound = pa.holsterSound;
                    sb.Append("\n  PlayerAttack.drawSound = \"").Append(_drawSound).Append("\"");
                    sb.Append("\n  PlayerAttack.holsterSound = \"").Append(_holsterSound).Append("\"");
                }
                else sb.Append("\n  PlayerAttack: NOT FOUND");
            }
            catch (Exception ex) { sb.Append("\n  PlayerAttack error: ").Append(ex.Message); }

            try
            {
                var sc = player.GetComponentInChildren<StepSoundClass>(true);
                if (sc != null) { _footstepPath = sc.audioStep; sb.Append("\n  StepSoundClass.audioStep = \"").Append(_footstepPath).Append("\""); }
                else sb.Append("\n  StepSoundClass: NOT FOUND");
            }
            catch (Exception ex) { sb.Append("\n  StepSoundClass error: ").Append(ex.Message); }

            try
            {
                var inv = player.GetComponentInChildren<InventoryBase>(true);
                if (inv != null) { _reloadFMODPath = inv.reloadSound; sb.Append("\n  InventoryBase.reloadSound = \"").Append(_reloadFMODPath).Append("\""); }
                else sb.Append("\n  InventoryBase: NOT FOUND");
            }
            catch (Exception ex) { sb.Append("\n  InventoryBase error: ").Append(ex.Message); }

            // AudioSources on player
            sb.Append("\n  --- AudioSources ---");
            foreach (var src in player.GetComponentsInChildren<AudioSource>(true))
            {
                if (src == null) continue;
                string clipName = src.clip != null ? src.clip.name : "null";
                sb.Append("\n  ").Append(src.gameObject.name).Append(" clip=").Append(clipName).Append(" isPlaying=").Append(src.isPlaying);
            }

            sb.Append("\n  --- Cached ---");
            sb.Append("\n  footstep=").Append(_footstepPath ?? "null");
            sb.Append("\n  hurt=").Append(_hurtPath ?? "null");
            sb.Append("\n  draw=").Append(_drawSound ?? "null");
            sb.Append("\n  holster=").Append(_holsterSound ?? "null");
            sb.Append("\n  reloadFMOD=").Append(_reloadFMODPath ?? "null");

            ModRuntime.Log?.Msg(sb.ToString());

            // Test FMOD PlayOneShot with a known path if available
            if (!string.IsNullOrEmpty(_footstepPath))
            {
                try
                {
                    RuntimeManager.PlayOneShot(_footstepPath, player.transform.position);
                    ModRuntime.Log?.Msg("[Audio] FMOD test PlayOneShot(" + _footstepPath + ") succeeded");
                }
                catch
                {
                    ModRuntime.Log?.Warning("[Audio] FMOD test PlayOneShot(" + _footstepPath + ") FAILED!");
                }
            }
        }

        private int _tickCount;
        public void Tick(PlayerStateMessage state, AnimBools bools, AnimTriggers triggers)
        {
            if (_proxyTransform == null) return;

            if (!_weaponCacheBuilt) BuildWeaponCache();

            // Retry reading FMOD paths once if all are null (player might not be ready yet)
            if (!_pathsRetried && string.IsNullOrEmpty(_footstepPath) && string.IsNullOrEmpty(_hurtPath) && _footstepClip == null)
            {
                ReadFMODPaths();
                _pathsRetried = true;
            }

            _tickCount++;
            bool shooting = bools.HasFlag(AnimBools.Shooting);

            // Distance check: only play proxy sounds if within hearing range of local player
            float distToLocal = float.MaxValue;
            GameObject localPlayer = null;
            try { localPlayer = LanNetworkManager.Instance?.GetLocalPlayer(); } catch { }
            if (localPlayer == null) try { localPlayer = PlayerState.player; } catch { }
            if (localPlayer != null)
                distToLocal = Vector3.Distance(_proxyTransform.position, localPlayer.transform.position);

            // Log every ~500 ticks
            if (_tickCount % 500 == 0)
                ModRuntime.Log?.Msg("[Audio] Tick#" + _tickCount + " step=" + state.StepHappened
                    + " dist=" + distToLocal.ToString("F1"));

            // Hearing range per sound type
            bool nearby = distToLocal < 40f;
            bool farRange = distToLocal < 75f;

            // Shooting — far range (gunshots are loud)
            if (farRange && shooting && !_lastShooting)
                PlayShootSound(state.Weapon);
            _lastShooting = shooting;

            // Footsteps — synced with animation via StepHappened flag, nearby only (<40m)
            if (nearby && state.StepHappened)
            {
                float vol = 0.4f + Mathf.Abs(state.Forward) * 0.2f;
                if (vol > 1f) vol = 1f;
                if (!string.IsNullOrEmpty(_footstepPath))
                    PlayFMOD(_footstepPath, _proxyTransform.position, vol);
                else if (_footstepClip != null)
                    AudioSource.PlayClipAtPoint(_footstepClip, _proxyTransform.position, vol * 0.6f);
            }

            // Reload — medium range
            if (farRange && triggers.HasFlag(AnimTriggers.ReloadTrigger) && Time.time - _lastReloadTime > ReloadCooldown)
            {
                PlayReloadSound(state.Weapon);
                _lastReloadTime = Time.time;
            }

            // Hurt — nearby
            if (nearby && triggers.HasFlag(AnimTriggers.Hurt) && Time.time - _lastHurtTime > HurtCooldown)
            {
                PlayFMOD(_hurtPath, _proxyTransform.position, 0.5f);
                _lastHurtTime = Time.time;
            }

            // Weapon swap — medium range
            if (farRange && state.Weapon != _lastWeapon)
            {
                if (_hasReceivedFirst)
                {
                    if (state.Weapon == WeaponType.None)
                        PlayFMOD(_holsterSound, _proxyTransform.position, 0.3f);
                    else
                        PlayFMOD(_drawSound, _proxyTransform.position, 0.3f);
                }
                _lastWeapon = state.Weapon;
                _hasReceivedFirst = true;
            }

            // Ladder climbing — rhythmic footsteps at proxy position
            if (state.Climbing)
            {
                _climbTimer -= Time.deltaTime;
                if (_climbTimer <= 0f)
                {
                    if (nearby)
                    {
                        bool goingUp = (_proxyTransform.position.y - _lastPos.y) >= -0.01f;
                        PlayFMOD(goingUp ? LadderUpPath : LadderDownPath, _proxyTransform.position, 0.5f);
                    }
                    _climbTimer = LadderClimbInterval;
                }
                _wasClimbing = true;
            }
            else if (_wasClimbing)
            {
                _climbTimer = 0f;
                _wasClimbing = false;
            }
            _lastPos = _proxyTransform.position;
        }

        private void PlayShootSound(WeaponType weapon)
        {
            string path;
            if (_shootFMOD.TryGetValue(weapon, out path))
                PlayFMOD(path, _proxyTransform.position, 0.5f);
        }

        private void PlayReloadSound(WeaponType weapon)
        {
            string path;
            if (_reloadFMOD.TryGetValue(weapon, out path))
                PlayFMOD(path, _proxyTransform.position, 0.4f);
            else
                PlayFMOD(_reloadFMODPath, _proxyTransform.position, 0.4f);
        }

        private static void PlayFMOD(string path, Vector3 pos, float volume)
        {
            if (string.IsNullOrEmpty(path)) return;
            ModRuntime.Log?.Msg("[Audio] PlayFMOD(" + path + ")");
            try
            {
                RuntimeManager.PlayOneShot(path, pos);
            }
            catch
            {
                ModRuntime.Log?.Warning("[Audio] FMOD PlayOneShot(" + path + ") FAILED (no exception detail)");
            }
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
                    try
                    {
                        WeaponType wt = ItemToWeaponType(w.parentItem._item);
                        if (wt == WeaponType.None) continue;

                        // Read FMOD event paths (string fields — no IL2CPP serialization bug)
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

                        ModRuntime.Log?.Msg("[Audio] Weapon " + wt + ": shotMod=" + (w.shotMod ?? "null") + " reloadMod=" + (w.reloadMod ?? "null"));
                    }
                    catch { }
                }

                ModRuntime.Log?.Msg("[Audio] Cached " + shotCount + " weapon shot FMOD paths, " + reloadCount + " reload FMOD paths");
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[Audio] BuildWeaponCache failed: " + ex.Message);
            }
        }

        private static WeaponType ItemToWeaponType(Items.itemlist item)
        {
            switch (item)
            {
                case Items.itemlist.Pistol: return WeaponType.Pistol;
                case Items.itemlist.Revolver: return WeaponType.Revolver;
                case Items.itemlist.Shotgun: return WeaponType.Shotgun;
                case Items.itemlist.Rifle: return WeaponType.Rifle;
                case Items.itemlist.SMG: return WeaponType.SMG;
                case Items.itemlist.FlareGun: return WeaponType.Flare;
                case Items.itemlist.FlakGun: return WeaponType.CAR;
                case Items.itemlist.Machete: return WeaponType.Melee;
                case Items.itemlist.Taser: return WeaponType.Handgun;
                default: return WeaponType.None;
            }
        }
    }
}
