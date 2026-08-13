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
        private Transform _muzzleOrigin;
        private ParticleSystem _muzzleSmoke;
        private ParticleSystem _caseEject;
        private ParticleSystem _missedShot;
        private ParticleSystem _ricochet;
        private LineRenderer _laser;
        private SpriteRenderer _laserPoint;
        private Transform _slide;
        private float _slideRestPos;
        private float _flashTimer;
        private int _wallMask = ~0;

        // Visible long enough at ~30 Hz state + remote render (~2 frames minimum)
        private const float FlashDuration = 0.08f;
        private const float SlideTravel = 0.02f;
        private const float SlideReturn = 0.08f;
        private readonly GameObject _weaponRoot;

        public RemoteWeaponEffects(GameObject weapon, GameObject sourceWeapon)
        {
            _weapon = weapon;
            _weaponRoot = weapon;
            CacheEffects(sourceWeapon);
            ResetAll();
            TryReadWallMask();
        }

        public void SetWallMask(int mask)
        {
            _wallMask = mask;
        }

        public bool TryGetMuzzleWorldPos(out Vector3 pos)
        {
            if (_muzzleOrigin != null)
            {
                pos = _muzzleOrigin.position;
                return true;
            }
            if (_muzzleFlash != null)
            {
                pos = _muzzleFlash.transform.position;
                return true;
            }
            pos = default;
            return false;
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
            var allTransforms = _weapon.GetComponentsInChildren<Transform>(true);

            // Prefer serialized MuzzleFlash.muzzle from SOURCE before clone MBs die
            GameObject srcMuzzleGo = null;
            Transform srcMuzzleTf = null;
            try
            {
                var srcMf = source != null ? source.GetComponentInChildren<MuzzleFlash>(true) : null;
                if (srcMf != null && srcMf.muzzle != null)
                {
                    srcMuzzleGo = srcMf.muzzle;
                    string leaf = srcMf.muzzle.name;
                    // Match same leaf name under clone
                    foreach (var t in allTransforms)
                    {
                        if (t != null && t.name == leaf)
                        {
                            _muzzleFlash = t.gameObject;
                            break;
                        }
                    }
                }
                if (srcMf != null && srcMf.pivot != null)
                    srcMuzzleTf = srcMf.pivot;
            }
            catch { }

            if (_muzzleFlash == null)
                _muzzleFlash = FindMuzzleVisualTarget(allTransforms, _weaponRoot);

            if (_muzzleFlash != null)
            {
                _muzzleFlash.SetActive(false);
                EnsureRendererVisible(_muzzleFlash);
            }
            ModRuntime.Log?.Msg("[FX] MuzzleFlash " + (_muzzleFlash != null ? "FOUND at " + GetPath(_muzzleFlash.transform) : "NOT FOUND"));

            // Origin for laser/ray: Muzzle node / flash / pivot
            _muzzleOrigin = FindTransformByNameContains(allTransforms, "muzzle");
            if (_muzzleOrigin == null && _muzzleFlash != null)
                _muzzleOrigin = _muzzleFlash.transform;
            if (_muzzleOrigin == null && srcMuzzleTf != null)
            {
                foreach (var t in allTransforms)
                {
                    if (t != null && t.name == srcMuzzleTf.name)
                    {
                        _muzzleOrigin = t;
                        break;
                    }
                }
            }

            _muzzleSmoke = FindParticleSystemByName(allTransforms, "Smoke", allowAnyFallback: false);
            // Smoke is often under Muzzle
            if (_muzzleSmoke == null)
                _muzzleSmoke = FindParticleSystemNearName(allTransforms, "muzzle");
            ModRuntime.Log?.Msg("[FX] MuzzleSmoke " + (_muzzleSmoke != null ? "FOUND" : "NOT FOUND"));
            HardenParticle(_muzzleSmoke);

            // Laser from AimLaser on source if present
            try
            {
                var srcAl = source != null ? source.GetComponentInChildren<AimLaser>(true) : null;
                if (srcAl != null)
                {
                    if (srcAl.line != null)
                    {
                        string ln = srcAl.line.gameObject.name;
                        foreach (var t in allTransforms)
                        {
                            if (t == null) continue;
                            var lr = t.GetComponent<LineRenderer>();
                            if (lr != null && (t.name == ln || t.name.IndexOf("laser", StringComparison.OrdinalIgnoreCase) >= 0
                                || t.name.IndexOf("testlaser", StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                _laser = lr;
                                break;
                            }
                        }
                        if (_laser == null)
                            _laser = FindLineRenderer(allTransforms);
                    }
                    if (srcAl.laserPoint != null)
                    {
                        foreach (var t in allTransforms)
                        {
                            if (t != null && t.name == srcAl.laserPoint.name)
                            {
                                _laserPoint = t.GetComponent<SpriteRenderer>();
                                break;
                            }
                        }
                    }
                    if (srcAl.missedShot != null)
                        _missedShot = FindMatchingPs(allTransforms, srcAl.missedShot.gameObject.name);
                    if (srcAl.ricochet != null)
                        _ricochet = FindMatchingPs(allTransforms, srcAl.ricochet.gameObject.name);
                }
            }
            catch { }

            if (_laser == null)
                _laser = FindLineRenderer(allTransforms);
            if (_laser != null)
            {
                _laser.useWorldSpace = true;
                _laser.enabled = false;
                // Ensure width isn't zero after clone
                try
                {
                    if (_laser.startWidth <= 0f && _laser.endWidth <= 0f)
                    {
                        _laser.startWidth = 0.01f;
                        _laser.endWidth = 0.01f;
                    }
                }
                catch { }
            }
            ModRuntime.Log?.Msg("[FX] Laser " + (_laser != null ? "FOUND mat=" + (_laser.sharedMaterial != null ? _laser.sharedMaterial.name : "NULL") : "NOT FOUND"));

            if (_missedShot == null)
                _missedShot = FindParticleSystemByName(allTransforms, "Miss", allowAnyFallback: false);
            if (_ricochet == null)
                _ricochet = FindParticleSystemByName(allTransforms, "Ricochet", allowAnyFallback: false);
            ModRuntime.Log?.Msg("[FX] MissedShot " + (_missedShot != null ? "FOUND" : "NOT FOUND")
                + " Ricochet " + (_ricochet != null ? "FOUND" : "NOT FOUND"));
            HardenParticle(_missedShot);
            HardenParticle(_ricochet);

            // Case eject — never fall back to random PS
            try
            {
                var srcCe = source != null ? source.GetComponentInChildren<ReloadCaseEject>(true) : null;
                if (srcCe != null && srcCe.particles != null)
                    _caseEject = FindMatchingPs(allTransforms, srcCe.particles.gameObject.name);
            }
            catch { }
            if (_caseEject == null)
                _caseEject = FindParticleSystemByName(allTransforms, "Case", allowAnyFallback: false);
            if (_caseEject == null)
                _caseEject = FindParticleSystemByName(allTransforms, "Shell", allowAnyFallback: false);
            if (_caseEject == null)
                _caseEject = FindParticleSystemByName(allTransforms, "Eject", allowAnyFallback: false);
            ModRuntime.Log?.Msg("[FX] CaseEject " + (_caseEject != null ? "FOUND at " + _caseEject.gameObject.name : "NOT FOUND"));
            HardenParticle(_caseEject);

            _slide = FindTransformByName(allTransforms, "Slide");
            if (_slide != null) _slideRestPos = _slide.localPosition.z;
            ModRuntime.Log?.Msg("[FX] PistolSlide " + (_slide != null ? "FOUND" : "NOT FOUND"));
        }

        private static void EnsureRendererVisible(GameObject go)
        {
            if (go == null) return;
            var rends = go.GetComponentsInChildren<Renderer>(true);
            foreach (var r in rends)
            {
                if (r == null) continue;
                r.enabled = true;
            }
        }

        private static void HardenParticle(ParticleSystem ps)
        {
            if (ps == null) return;
            try
            {
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var psr = ps.GetComponent<ParticleSystemRenderer>();
                if (psr != null) psr.enabled = true;
            }
            catch { }
        }

        private static ParticleSystem FindMatchingPs(Transform[] all, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var t in all)
            {
                if (t == null) continue;
                if (t.name != name) continue;
                var ps = t.GetComponent<ParticleSystem>();
                if (ps != null) return ps;
            }
            return FindParticleSystemByName(all, name, allowAnyFallback: false);
        }

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
                if (n.Contains("flash")) score += 20;
                if (n.Contains("muzzle")) score += 10;
                if (n.Contains("halo")) score += 5;
                // Prefer under a Muzzle parent
                if (t.parent != null)
                {
                    string pn = t.parent.name.ToLowerInvariant();
                    if (pn.Contains("muzzle") || pn.Contains("flash") || pn.Contains("halo"))
                        score += 15;
                }
                if (score > bestScore) { bestScore = score; best = mr.gameObject; }
            }
            // Accept only if we scored something muzzle-related
            if (bestScore < 10) return null;
            return best;
        }

        private static LineRenderer FindLineRenderer(Transform[] allTransforms)
        {
            LineRenderer fallback = null;
            foreach (var t in allTransforms)
            {
                if (t == null) continue;
                var lr = t.GetComponent<LineRenderer>();
                if (lr == null) continue;
                string n = t.name.ToLowerInvariant();
                if (n.Contains("laser") || n.Contains("aim") || n.Contains("testlaser"))
                    return lr;
                if (fallback == null) fallback = lr;
            }
            return fallback;
        }

        private static ParticleSystem FindParticleSystemByName(Transform[] allTransforms, string hint, bool allowAnyFallback)
        {
            if (string.IsNullOrEmpty(hint)) return null;
            foreach (var t in allTransforms)
            {
                var ps = t.GetComponent<ParticleSystem>();
                if (ps == null) continue;
                if (t.name.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0) return ps;
                if (t.parent != null && t.parent.name.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0) return ps;
            }
            if (!allowAnyFallback) return null;
            foreach (var t in allTransforms)
            {
                var ps = t.GetComponent<ParticleSystem>();
                if (ps != null) return ps;
            }
            return null;
        }

        private static ParticleSystem FindParticleSystemNearName(Transform[] allTransforms, string parentHint)
        {
            foreach (var t in allTransforms)
            {
                var ps = t.GetComponent<ParticleSystem>();
                if (ps == null) continue;
                Transform p = t;
                for (int d = 0; d < 4 && p != null; d++)
                {
                    if (p.name.IndexOf(parentHint, StringComparison.OrdinalIgnoreCase) >= 0)
                        return ps;
                    p = p.parent;
                }
            }
            return null;
        }

        private static Transform FindTransformByName(Transform[] allTransforms, string name)
        {
            foreach (var t in allTransforms)
            {
                if (t == null) continue;
                if (t.name.Equals(name, StringComparison.OrdinalIgnoreCase)) return t;
            }
            return null;
        }

        private static Transform FindTransformByNameContains(Transform[] allTransforms, string hint)
        {
            Transform best = null;
            int bestScore = -1;
            foreach (var t in allTransforms)
            {
                if (t == null) continue;
                string n = t.name.ToLowerInvariant();
                if (n.IndexOf(hint, StringComparison.OrdinalIgnoreCase) < 0) continue;
                int score = n == hint ? 10 : 5;
                if (score > bestScore) { bestScore = score; best = t; }
            }
            return best;
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
                EnsureRendererVisible(_muzzleFlash);
                _flashTimer = FlashDuration;
            }
            PlayBurst(_muzzleSmoke);
            PlayBurst(_caseEject);
            if (_slide != null)
            {
                Vector3 p = _slide.localPosition;
                p.z = _slideRestPos - SlideTravel;
                _slide.localPosition = p;
            }
        }

        public void OnReload()
        {
            PlayBurst(_caseEject);
        }

        private static void PlayBurst(ParticleSystem ps)
        {
            if (ps == null) return;
            try
            {
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                ps.Play(true);
            }
            catch { }
        }

        public void DoImpactRaycast(Vector3 origin, Vector3 direction, float damage)
        {
            RaycastHit hit;
            if (Physics.Raycast(origin, direction, out hit, 50f, _wallMask))
            {
                bool hitPlayer = IsPlayerHit(hit.collider);
                if (hitPlayer)
                {
                    if (Config.ModConfig.FriendlyFire?.Value == true)
                    {
                        ModRuntime.Log?.Msg("[FX] Friendly fire! Hit local player at " + hit.point.ToString("F1") + " dmg=" + damage.ToString("F0"));
                        NetworkDamageSystem.ApplyDamage(damage, hit.point, direction);
                    }
                    PlayAt(_ricochet, hit.point, hit.normal);
                    return;
                }

                PlayAt(_ricochet, hit.point, hit.normal);
            }
            else
            {
                if (_missedShot != null)
                {
                    Vector3 farPoint = origin + direction * 30f;
                    _missedShot.transform.position = farPoint;
                    PlayBurst(_missedShot);
                }
            }
        }

        private static void PlayAt(ParticleSystem ps, Vector3 point, Vector3 normal)
        {
            if (ps == null) return;
            ps.transform.position = point;
            if (normal.sqrMagnitude > 0.001f)
                ps.transform.rotation = Quaternion.LookRotation(normal);
            PlayBurst(ps);
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
            if (_laser == null)
            {
                if (_laserPoint != null) _laserPoint.enabled = false;
                return;
            }

            _laser.useWorldSpace = true;
            _laser.enabled = aiming;
            if (_laserPoint != null) _laserPoint.enabled = aiming;
            if (!aiming) return;

            if (direction.sqrMagnitude < 0.0001f)
                direction = Vector3.forward;
            direction.Normalize();

            Vector3 end = origin + direction * maxDist;
            RaycastHit hit;
            if (Physics.Raycast(origin, direction, out hit, maxDist, _wallMask))
            {
                end = hit.point;
                if (_laserPoint != null)
                {
                    _laserPoint.transform.position = hit.point;
                    _laserPoint.enabled = true;
                }
            }
            else if (_laserPoint != null)
            {
                _laserPoint.transform.position = end;
            }

            _laser.positionCount = 2;
            _laser.SetPosition(0, origin);
            _laser.SetPosition(1, end);
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

        public void ResetAll()
        {
            StopPS(ref _muzzleSmoke);
            StopPS(ref _caseEject);
            StopPS(ref _missedShot);
            StopPS(ref _ricochet);
            if (_muzzleFlash != null)
                _muzzleFlash.SetActive(false);
            if (_laser != null)
                _laser.enabled = false;
            if (_laserPoint != null)
                _laserPoint.enabled = false;
        }

        private static void StopPS(ref ParticleSystem ps)
        {
            if (ps != null)
            {
                try { ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); }
                catch { }
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
            _laserPoint = null;
            _slide = null;
            _muzzleOrigin = null;
        }
    }
}
