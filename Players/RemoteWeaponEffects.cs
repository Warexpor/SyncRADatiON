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
        private float _laserMaxDist = 30f;
        private string _ricochetPath;
        private Transform _slide;
        private float _slideRestPos;
        private float _flashTimer;
        private float _ejectStopTimer;
        private float _smokeStopTimer;
        private int _wallMask = ~0;

        // Native muzzleCycle is ~1–2 frames; 80ms looked like a stuck flash on the clone.
        private const float FlashDuration = 0.02f;
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

        public bool TryGetLaserForward(out Vector3 dir)
        {
            if (_laser != null)
            {
                dir = _laser.transform.forward;
                if (dir.sqrMagnitude > 0.0001f)
                {
                    dir.Normalize();
                    return true;
                }
            }
            dir = default;
            return false;
        }

        public bool TryGetMuzzleForward(out Vector3 dir)
        {
            if (_muzzleOrigin != null)
            {
                dir = _muzzleOrigin.forward;
                if (dir.sqrMagnitude > 0.0001f)
                {
                    dir.Normalize();
                    return true;
                }
            }
            dir = default;
            return false;
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
                if (pa != null)
                {
                    _wallMask = pa.WallMask;
                    if (!string.IsNullOrEmpty(pa.ricochetSound))
                        _ricochetPath = pa.ricochetSound;
                }
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
            LineRenderer srcLine = null;
            try
            {
                var srcAl = source != null ? source.GetComponentInChildren<AimLaser>(true) : null;
                if (srcAl != null)
                {
                    srcLine = srcAl.line;
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
                        var srcSr = srcAl.laserPoint.GetComponent<SpriteRenderer>();
                        foreach (var t in allTransforms)
                        {
                            if (t != null && t.name == srcAl.laserPoint.name)
                            {
                                _laserPoint = t.GetComponent<SpriteRenderer>();
                                break;
                            }
                        }
                        if (_laserPoint != null && srcSr != null)
                        {
                            _laserPoint.sprite = srcSr.sprite;
                            if (srcSr.sharedMaterial != null)
                                _laserPoint.sharedMaterial = srcSr.sharedMaterial;
                            if (srcSr.sharedMaterials != null && srcSr.sharedMaterials.Length > 0)
                                _laserPoint.sharedMaterials = srcSr.sharedMaterials;
                            _laserPoint.color = srcSr.color;
                        }
                    }
                    if (srcAl.missedShot != null)
                        _missedShot = FindMatchingPs(allTransforms, srcAl.missedShot.gameObject.name);
                    if (srcAl.ricochet != null)
                        _ricochet = FindMatchingPs(allTransforms, srcAl.ricochet.gameObject.name);
                    if (srcAl.laserMaxDist > 0.5f)
                        _laserMaxDist = srcAl.laserMaxDist;
                }
            }
            catch { }

            if (_laser == null)
                _laser = FindLineRenderer(allTransforms);
            if (_laser != null)
            {
                _laser.useWorldSpace = false;
                _laser.enabled = false;
                try
                {
                    if (srcLine != null)
                    {
                        _laser.startColor = srcLine.startColor;
                        _laser.endColor = srcLine.endColor;
                        if (srcLine.sharedMaterial != null)
                            _laser.sharedMaterial = srcLine.sharedMaterial;
                    }
                    if (_laser.startWidth <= 0f && _laser.endWidth <= 0f)
                    {
                        _laser.startWidth = 0.01f;
                        _laser.endWidth = 0.01f;
                    }
                }
                catch { }
            }
            ModRuntime.Log?.Msg("[FX] Laser " + (_laser != null ? "FOUND mat=" + (_laser.sharedMaterial != null ? _laser.sharedMaterial.name : "NULL") : "NOT FOUND"));
            PrepareLaserPoint();

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

        private static void PlayBurst(ParticleSystem ps)
        {
            if (ps == null) return;
            try
            {
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                ps.Play(true);
                ps.Emit(1);
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
                var flashPs = _muzzleFlash.GetComponentsInChildren<ParticleSystem>(true);
                for (int i = 0; i < flashPs.Length; i++)
                    HardenParticle(flashPs[i]);
            }
            PlayBurst(_muzzleSmoke);
            _smokeStopTimer = 0.2f;
            PlayBurst(_caseEject);
            _ejectStopTimer = 0.2f;
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
            _ejectStopTimer = 0.2f;
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
                        PlayAt(_ricochet, hit.point, hit.normal);
                    return;
                }

                PlayAt(_ricochet, hit.point, hit.normal);
                if (!string.IsNullOrEmpty(_ricochetPath))
                    WorldSfx.Play(_ricochetPath, hit.point, 0.4f, WorldSfx.CombatRange);
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

        public void UpdateLaser(bool aiming)
        {
            if (_laser == null)
            {
                if (_laserPoint != null) _laserPoint.enabled = false;
                return;
            }

            _laser.useWorldSpace = false;
            _laser.enabled = aiming;
            if (_laserPoint != null) _laserPoint.enabled = aiming;
            if (!aiming) return;

            Transform t = _laser.transform;
            Vector3 origin = t.position;
            Vector3 dir = t.forward;
            if (dir.sqrMagnitude < 0.0001f)
                dir = Vector3.forward;
            else
                dir.Normalize();

            float dist = _laserMaxDist;
            RaycastHit hit;
            if (Physics.Raycast(origin, dir, out hit, _laserMaxDist, _wallMask))
            {
                dist = hit.distance;
                if (_laserPoint != null)
                {
                    _laserPoint.transform.position = hit.point;
                    _laserPoint.enabled = true;
                }
            }
            else if (_laserPoint != null)
            {
                _laserPoint.transform.position = origin + dir * dist;
            }

            _laser.positionCount = 2;
            _laser.SetPosition(0, Vector3.zero);
            _laser.SetPosition(1, new Vector3(0f, 0f, dist));
            BillboardLaserPoint();
        }

        private void PrepareLaserPoint()
        {
            if (_laserPoint == null && _laser != null)
            {
                var srs = _weapon.GetComponentsInChildren<SpriteRenderer>(true);
                for (int i = 0; i < srs.Length; i++)
                {
                    var sr = srs[i];
                    if (sr == null) continue;
                    string n = sr.name.ToLowerInvariant();
                    if (n.IndexOf("point", StringComparison.OrdinalIgnoreCase) < 0
                        && n.IndexOf("decal", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    _laserPoint = sr;
                    break;
                }
            }
            if (_laserPoint == null) return;
            _laserPoint.transform.SetParent(_weaponRoot.transform, true);
            _laserPoint.transform.localScale = Vector3.one;
            if (_laserPoint.sharedMaterial == null)
            {
                Shader sh = Shader.Find("Sprites/Default");
                if (sh == null) sh = Shader.Find("Unlit/Color");
                if (sh != null)
                    _laserPoint.sharedMaterial = new Material(sh);
            }
            Color c = _laserPoint.color;
            if (c.r < 0.5f || c.g > 0.4f || c.b > 0.4f)
                _laserPoint.color = Color.red;
            _laserPoint.enabled = false;
        }

        private void BillboardLaserPoint()
        {
            if (_laserPoint == null) return;
            Camera cam = Camera.main;
            if (cam == null) return;
            _laserPoint.transform.rotation = Quaternion.LookRotation(cam.transform.forward, cam.transform.up);
        }

        public void Tick(float dt)
        {
            if (_flashTimer > 0f)
            {
                _flashTimer -= dt;
                if (_flashTimer <= 0f && _muzzleFlash != null)
                {
                    _muzzleFlash.SetActive(false);
                    var rs = _muzzleFlash.GetComponentsInChildren<Renderer>(true);
                    for (int i = 0; i < rs.Length; i++)
                        if (rs[i] != null) rs[i].enabled = false;
                }
            }

            if (_ejectStopTimer > 0f)
            {
                _ejectStopTimer -= dt;
                if (_ejectStopTimer <= 0f)
                    StopEmitKeep(_caseEject);
            }
            if (_smokeStopTimer > 0f)
            {
                _smokeStopTimer -= dt;
                if (_smokeStopTimer <= 0f)
                    StopEmitKeep(_muzzleSmoke);
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

        private static void StopEmitKeep(ParticleSystem ps)
        {
            if (ps == null) return;
            try { ps.Stop(true, ParticleSystemStopBehavior.StopEmitting); }
            catch { }
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
