// SyncRADation � per-weapon FX: muzzle flash, smoke, case eject, laser, ricochet, slide
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

        private const float FlashDuration = 0.03f;
        private const float SlideTravel = 0.02f;
        private const float SlideReturn = 0.08f;
        private readonly GameObject _weaponRoot;

        public RemoteWeaponEffects(GameObject weapon, GameObject sourceWeapon)
        {
            _weapon = weapon;
            _weaponRoot = weapon;
            CacheEffects(sourceWeapon);
            ResetAll(); // prevent PlayOnAwake before first activation
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
            // Pure name/hierarchy search — no dependency on MonoBehaviour components.
            // After this, all MBs can be destroyed without losing effect references.
            // Runs BEFORE MB destruction (component-based search still works but we don't rely on it).

            // DIAG: dump clone hierarchy
            ModRuntime.Log?.Msg("[FX] === CLONE HIERARCHY: " + _weapon.name + " ===");
            DumpHierarchy(_weapon.transform, 0);

            // Gather all transforms once for name-based search
            var allTransforms = _weapon.GetComponentsInChildren<Transform>(true);

            // Muzzle flash: Quad MeshRenderer is the actual visual
            _muzzleFlash = FindMuzzleVisualTarget(allTransforms, _weaponRoot);
            ModRuntime.Log?.Msg("[FX] MuzzleFlash " + (_muzzleFlash != null ? "FOUND at " + GetPath(_muzzleFlash.transform) : "NOT FOUND"));

            // Muzzle smoke: ParticleSystem named "Smoke" under Muzzle subtree
            _muzzleSmoke = FindParticleSystemByName(allTransforms, "Smoke");
            ModRuntime.Log?.Msg("[FX] MuzzleSmoke " + (_muzzleSmoke != null ? "FOUND" : "NOT FOUND"));

            // Laser: LineRenderer (on TestLaser for Pistol)
            _laser = FindLineRenderer(allTransforms);
            if (_laser != null) _laser.enabled = false;
            ModRuntime.Log?.Msg("[FX] Laser " + (_laser != null ? "FOUND" : "NOT FOUND"));

            // Missed shot / ricochet: ParticleSystems under TestLaser
            _missedShot = FindParticleSystemByName(allTransforms, "Miss");
            _ricochet = FindParticleSystemByName(allTransforms, "Ricochet");
            ModRuntime.Log?.Msg("[FX] MissedShot " + (_missedShot != null ? "FOUND" : "NOT FOUND")
                + " Ricochet " + (_ricochet != null ? "FOUND" : "NOT FOUND"));

            // Case eject: ParticleSystem named "Case" (Pistol has this without ReloadCaseEject component)
            _caseEject = FindParticleSystemByName(allTransforms, "Case");
            ModRuntime.Log?.Msg("[FX] CaseEject " + (_caseEject != null ? "FOUND" : "NOT FOUND"));

            // Slide: Transform named "Slide"
            _slide = FindTransformByName(allTransforms, "Slide");
            if (_slide != null) _slideRestPos = _slide.localPosition.z;
            ModRuntime.Log?.Msg("[FX] PistolSlide " + (_slide != null ? "FOUND" : "NOT FOUND"));
        }

        private static void DumpHierarchy(Transform t, int depth)
        {
            if (t == null) return;
            string indent = new string(' ', depth * 2);
            string tags = "";
            if (t.GetComponent<MuzzleFlash>() != null) tags += " MF";
            if (t.GetComponent<AimLaser>() != null) tags += " AL";
            if (t.GetComponent<ReloadCaseEject>() != null) tags += " CE";
            if (t.GetComponent<PistolSlide>() != null) tags += " PS";
            if (t.GetComponent<MuzzleSmoke>() != null) tags += " MS";
            if (t.GetComponent<LineRenderer>() != null) tags += " LR";
            if (t.GetComponent<ParticleSystem>() != null) tags += " PSys";
            if (t.GetComponent<Renderer>() != null) tags += " Rend";
            if (t.GetComponent<MeshRenderer>() != null) tags += " MR";
            ModRuntime.Log?.Msg("[FX] " + indent + t.name + " children=" + t.childCount + tags);
            for (int i = 0; i < t.childCount; i++)
                DumpHierarchy(t.GetChild(i), depth + 1);
        }

        // Muzzle flash: find the Quad MeshRenderer that MuzzleFlash.muzzle normally points to.
        // Quad (1) path: .../Muzzle/Halo/Quad (1). Score by name to prefer Quad over MuzzleFlash's own MR.
        private static GameObject FindMuzzleVisualTarget(Transform[] allTransforms, GameObject weaponRoot)
        {
            GameObject best = null;
            int bestScore = -1;
            foreach (var t in allTransforms)
            {
                if (t == null) continue;
                var mr = t.GetComponent<MeshRenderer>();
                if (mr == null || mr.gameObject == weaponRoot) continue;
                int score = 0;
                string n = t.name.ToLowerInvariant();
                if (n.Contains("quad")) score += 30;
                if (n.Contains("flash")) score += 5;
                if (n.Contains("muzzle")) score += 2;
                if (n.Contains("halo")) score -= 1;
                if (score > bestScore) { bestScore = score; best = mr.gameObject; }
            }
            return best;
        }

        // Find first LineRenderer in the hierarchy
        private static LineRenderer FindLineRenderer(Transform[] allTransforms)
        {
            foreach (var t in allTransforms)
            {
                if (t == null) continue;
                var lr = t.GetComponent<LineRenderer>();
                if (lr != null) return lr;
            }
            return null;
        }

        // Find first ParticleSystem whose name (or parent's name) contains the hint
        private static ParticleSystem FindParticleSystemByName(Transform[] allTransforms, string hint)
        {
            if (string.IsNullOrEmpty(hint)) return null;
            // First pass: exact name match on the PS's own transform or parent
            foreach (var t in allTransforms)
            {
                var ps = t.GetComponent<ParticleSystem>();
                if (ps == null) continue;
                if (t.name.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0) return ps;
                if (t.parent != null && t.parent.name.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0) return ps;
            }
            // Second pass: any ParticleSystem
            foreach (var t in allTransforms)
            {
                var ps = t.GetComponent<ParticleSystem>();
                if (ps != null) return ps;
            }
            return null;
        }

        // Find first Transform whose name matches (case-insensitive)
        private static Transform FindTransformByName(Transform[] allTransforms, string name)
        {
            foreach (var t in allTransforms)
            {
                if (t == null) continue;
                if (t.name.Equals(name, StringComparison.OrdinalIgnoreCase)) return t;
            }
            return null;
        }

        private static string GetPath(Transform t)
        {
            if (t == null) return "";
            string p = t.name;
            while (t.parent != null) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }

        public void OnShot()
        {
            if (_muzzleFlash != null)
            {
                _muzzleFlash.SetActive(true);
                _flashTimer = FlashDuration;
            }
            if (_muzzleSmoke != null)
            {
                _muzzleSmoke.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                _muzzleSmoke.Play();
            }
            if (_caseEject != null)
            {
                _caseEject.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                _caseEject.Play();
            }
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

        // Stop all particles (prevents PlayOnAwake from causing infinite emission on weapon switch)
        public void ResetAll()
        {
            StopPS(ref _muzzleSmoke);
            StopPS(ref _caseEject);
            StopPS(ref _missedShot);
            StopPS(ref _ricochet);
        }

        private static void StopPS(ref ParticleSystem ps)
        {
            if (ps != null)
            {
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }

        public void Cleanup()
        {
            if (_laser != null) _laser.enabled = false;
            ResetAll();
            _muzzleFlash = null;
            _muzzleSmoke = null;
            _caseEject = null;
            _laser = null;
            _slide = null;
        }


    }
}
