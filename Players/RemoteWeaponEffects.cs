// SyncRADation — per-weapon FX: muzzle flash, smoke, case eject, laser, ricochet, slide
using System;
using System.Collections.Generic;
using SyncRADation.Networking;
using UnityEngine;

namespace SyncRADation.Players
{
    public sealed class RemoteWeaponEffects
    {
        private readonly GameObject _weapon;
        private GameObject _muzzleFlash;
        private ParticleSystem _muzzleSmoke;
        private ParticleSystem _caseEject;
        private ParticleSystem _missedShot;
        private ParticleSystem _ricochet;
        private LineRenderer _laser;
        private Transform _slide;
        private float _slideRestPos;
        private float _flashTimer;
        private int _wallMask = ~0;

        private const float FlashDuration = 0.06f;
        private const float SlideTravel = 0.02f;
        private const float SlideReturn = 0.08f;

        public RemoteWeaponEffects(GameObject weapon, GameObject sourceWeapon)
        {
            _weapon = weapon;
            CacheEffects(sourceWeapon);
            TryReadWallMask();
        }

        public void SetWallMask(int mask)
        {
            _wallMask = mask;
        }

        private void TryReadWallMask()
        {
            try
            {
                var pa = PlayerState.player?.GetComponentInChildren<PlayerAttack>(true);
                if (pa != null) _wallMask = pa.WallMask;
            }
            catch { }
        }

        private void CacheEffects(GameObject source)
        {
            // IL2CPP-proof: serialized fields (muzzle, particles, line, slide) are NULL in IL2CPP.
            // We find components by TYPE on source, then match children by NAME on clone.

            try
            {
                var mf = source.GetComponentInChildren<MuzzleFlash>(true);
                if (mf != null)
                {
                    string goName = mf.gameObject.name;
                    var go = FindChildRecursive(_weapon.transform, goName);
                    if (go != null)
                    {
                        var mr = go.GetComponentInChildren<MeshRenderer>(true);
                        _muzzleFlash = mr != null ? mr.gameObject : go.gameObject;
                    }
                    ModRuntime.Log?.Msg("[FX] MuzzleFlash '" + goName + "' " + (_muzzleFlash != null ? "FOUND" : "NOT FOUND"));
                }
            }
            catch { }

            try
            {
                var ms = source.GetComponentInChildren<MuzzleSmoke>(true);
                if (ms != null)
                {
                    string goName = ms.gameObject.name;
                    var go = FindChildRecursive(_weapon.transform, goName);
                    if (go != null)
                    {
                        _muzzleSmoke = go.GetComponentInChildren<ParticleSystem>(true);
                    }
                    ModRuntime.Log?.Msg("[FX] MuzzleSmoke '" + goName + "' " + (_muzzleSmoke != null ? "FOUND" : "NOT FOUND"));
                }
            }
            catch { }

            try
            {
                var al = source.GetComponentInChildren<AimLaser>(true);
                if (al != null)
                {
                    string goName = al.gameObject.name;
                    var go = FindChildRecursive(_weapon.transform, goName);
                    if (go != null)
                    {
                        _laser = go.GetComponentInChildren<LineRenderer>(true);
                        if (_laser != null) _laser.enabled = false;

                        var allPS = go.GetComponentsInChildren<ParticleSystem>(true);
                        foreach (var ps in allPS)
                        {
                            if (ps == null) continue;
                            string low = ps.name.ToLowerInvariant();
                            if (low.Contains("miss") || low.Contains("missed"))
                                _missedShot = ps;
                            if (low.Contains("ricochet"))
                                _ricochet = ps;
                        }
                    }
                    ModRuntime.Log?.Msg("[FX] AimLaser '" + goName + "' laser=" + (_laser != null ? "FOUND" : "NOT FOUND")
                        + " missed=" + (_missedShot != null ? "FOUND" : "NOT FOUND")
                        + " ricochet=" + (_ricochet != null ? "FOUND" : "NOT FOUND"));
                }
            }
            catch { }

            try
            {
                var ce = source.GetComponentInChildren<ReloadCaseEject>(true);
                if (ce != null)
                {
                    string goName = ce.gameObject.name;
                    var go = FindChildRecursive(_weapon.transform, goName);
                    if (go != null)
                    {
                        _caseEject = go.GetComponentInChildren<ParticleSystem>(true);
                    }
                    ModRuntime.Log?.Msg("[FX] CaseEject '" + goName + "' " + (_caseEject != null ? "FOUND" : "NOT FOUND"));
                }
            }
            catch { }

            try
            {
                var ps = source.GetComponentInChildren<PistolSlide>(true);
                if (ps != null)
                {
                    string goName = ps.gameObject.name;
                    var go = FindChildRecursive(_weapon.transform, goName);
                    if (go != null)
                    {
                        _slide = go;
                        _slideRestPos = _slide.localPosition.z;
                    }
                    ModRuntime.Log?.Msg("[FX] PistolSlide '" + goName + "' " + (_slide != null ? "FOUND" : "NOT FOUND"));
                }
            }
            catch { }
        }

        public void OnShot()
        {
            if (_muzzleFlash != null)
            {
                _muzzleFlash.SetActive(true);
                _flashTimer = FlashDuration;
            }
            if (_muzzleSmoke != null)
                _muzzleSmoke.Play();
            if (_caseEject != null)
                _caseEject.Play();
            if (_slide != null)
            {
                Vector3 p = _slide.localPosition;
                p.z = _slideRestPos - SlideTravel;
                _slide.localPosition = p;
            }
        }

        public void OnReload()
        {
            if (_caseEject != null)
                _caseEject.Play();
        }

        public void DoImpactRaycast(Vector3 origin, Vector3 direction, float damage)
        {
            RaycastHit hit;
            if (Physics.Raycast(origin, direction, out hit, 50f, _wallMask))
            {
                bool hitPlayer = IsPlayerHit(hit.collider);
                if (hitPlayer)
                {
                    ModRuntime.Log?.Msg("[FX] Friendly fire! Hit local player at " + hit.point.ToString("F1") + " dmg=" + damage.ToString("F0"));
                    NetworkDamageSystem.ApplyDamage(damage, hit.point, direction);
                    if (_ricochet != null)
                    {
                        _ricochet.transform.position = hit.point;
                        _ricochet.transform.rotation = Quaternion.LookRotation(hit.normal);
                        _ricochet.Play();
                    }
                    return;
                }

                if (_ricochet != null)
                {
                    _ricochet.transform.position = hit.point;
                    _ricochet.transform.rotation = Quaternion.LookRotation(hit.normal);
                    _ricochet.Play();
                }
            }
            else
            {
                if (_missedShot != null)
                {
                    Vector3 farPoint = origin + direction * 30f;
                    _missedShot.transform.position = farPoint;
                    _missedShot.Play();
                }
            }
        }

        private static bool IsPlayerHit(Collider col)
        {
            if (col == null) return false;
            if (col.CompareTag("Player")) return true;
            try
            {
                var local = PlayerState.player;
                if (local != null && col.transform.IsChildOf(local.transform))
                    return true;
            }
            catch { }
            return false;
        }

        public void UpdateLaser(bool aiming, Vector3 origin, Vector3 direction, float maxDist)
        {
            if (_laser == null) return;
            _laser.enabled = aiming;
            if (!aiming) return;

            _laser.SetPosition(0, origin);
            RaycastHit hit;
            if (Physics.Raycast(origin, direction, out hit, maxDist, _wallMask))
                _laser.SetPosition(1, hit.point);
            else
                _laser.SetPosition(1, origin + direction * maxDist);
        }

        public void Tick(float dt)
        {
            if (_flashTimer > 0f)
            {
                _flashTimer -= dt;
                if (_flashTimer <= 0f && _muzzleFlash != null)
                    _muzzleFlash.SetActive(false);
            }

            if (_slide != null)
            {
                float z = _slide.localPosition.z;
                if (z < _slideRestPos - 0.001f)
                {
                    z += (SlideTravel + 0.005f) * (dt / SlideReturn);
                    if (z > _slideRestPos) z = _slideRestPos;
                    Vector3 p = _slide.localPosition;
                    p.z = z;
                    _slide.localPosition = p;
                }
            }
        }

        public void Cleanup()
        {
            if (_laser != null) _laser.enabled = false;
            _muzzleFlash = null;
            _muzzleSmoke = null;
            _caseEject = null;
            _laser = null;
            _slide = null;
        }

        private static Transform FindChildRecursive(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.name == name) return child;
                var found = FindChildRecursive(child, name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
