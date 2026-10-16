// Per-weapon FX on a proxy's script-free weapon clone: muzzle flash, smoke, case eject, laser + dot, impact, slide kick.
using System;
using SyncRADation.Networking;
using SyncRADation.Sync;
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
        private float _ricochetChance = 30f;
        private bool _flare;
        // Native HitMissSounds: WeaponHit / WeaponMiss 0.1 s after PlayerAttack's hitEvent / missEvent.
        private string _hitPath = "event:/Elster/Weapons/WeaponHit";
        private string _missPath = "event:/Elster/Weapons/WeaponMiss";
        private const float ImpactSoundDelay = 0.1f;
        private const int EnemyHitboxLayer = 12; // "Hitbox/Enemy"
        private string _impactPath;
        private Vector3 _impactPos;
        private float _impactTimer;
        private Transform _slide;
        private float _slideRestPos;
        private float _flashTimer;
        private float _ejectStopTimer;
        private float _smokeStopTimer;
        private int _wallMask = ~0;
        // PlayerAttack.TargetMask (walls + enemy hitboxes: what a shot stops at) and AimLaser.mask (what the laser stops at).
        private int _targetMask;
        private int _laserMask;

        // Native muzzleCycle is ~1–2 frames; 80ms looked like a stuck flash on the clone.
        private const float FlashDuration = 0.02f;
        private const float SlideTravel = 0.02f;
        private const float SlideReturn = 0.08f;

        /// <summary>
        /// Called before the clone's MonoBehaviours are stripped: the source weapon's own MuzzleFlash / AimLaser /
        /// ReloadCaseEject name the exact children to drive; name heuristics only fill what they do not.
        /// </summary>
        public RemoteWeaponEffects(GameObject weapon, GameObject sourceWeapon)
        {
            _weapon = weapon;
            var all = weapon.GetComponentsInChildren<Transform>(true);
            CacheMuzzle(sourceWeapon, all);
            CacheLaser(sourceWeapon, all);
            CacheCaseEject(sourceWeapon, all);
            _slide = FindTransformByName(all, "Slide");
            if (_slide != null) _slideRestPos = _slide.localPosition.z;
            ReadLocalAttackSettings();
            ResetAll();
            PlaytestLog.Verbose("FX", "muzzle=" + (_muzzleFlash != null ? _muzzleFlash.name : "no")
                + " smoke=" + (_muzzleSmoke != null ? "yes" : "no")
                + " laser=" + (_laser != null ? "yes" : "no")
                + " miss=" + (_missedShot != null ? "yes" : "no")
                + " ricochet=" + (_ricochet != null ? "yes" : "no")
                + " eject=" + (_caseEject != null ? "yes" : "no")
                + " slide=" + (_slide != null ? "yes" : "no"));
        }

        public bool TryGetLaserForward(out Vector3 dir) => TryForward(_laser != null ? _laser.transform : null, out dir);

        public bool TryGetMuzzleForward(out Vector3 dir) => TryForward(_muzzleOrigin, out dir);

        private static bool TryForward(Transform t, out Vector3 dir)
        {
            dir = t != null ? t.forward : default;
            if (dir.sqrMagnitude <= 0.0001f) return false;
            dir.Normalize();
            return true;
        }

        public bool TryGetMuzzleWorldPos(out Vector3 pos)
        {
            Transform t = _muzzleOrigin != null ? _muzzleOrigin : (_muzzleFlash != null ? _muzzleFlash.transform : null);
            pos = t != null ? t.position : default;
            return t != null;
        }

        /// <summary>Masks and hit / miss sounds of the local Elster (same weapons, same walls, same enemies).</summary>
        private void ReadLocalAttackSettings()
        {
            var player = PlayerState.player;
            if (player == null) return;
            try
            {
                var pa = player.GetComponentInChildren<PlayerAttack>(true);
                if (pa != null)
                {
                    _wallMask = pa.WallMask;
                    _targetMask = pa.TargetMask;
                }
                var hms = player.GetComponentInChildren<HitMissSounds>(true);
                if (hms != null)
                {
                    if (!string.IsNullOrEmpty(hms.hitMod)) _hitPath = hms.hitMod;
                    if (!string.IsNullOrEmpty(hms.missMod)) _missPath = hms.missMod;
                }
            }
            catch (Exception e) { Guard.Swallow("WeaponFx.attack", e); }
        }

        private void CacheMuzzle(GameObject source, Transform[] all)
        {
            Transform pivot = null;
            try
            {
                var srcMf = source != null ? source.GetComponentInChildren<MuzzleFlash>(true) : null;
                if (srcMf != null)
                {
                    if (srcMf.muzzle != null)
                    {
                        var t = FindByExactName(all, srcMf.muzzle.name);
                        if (t != null) _muzzleFlash = t.gameObject;
                    }
                    pivot = srcMf.pivot;
                }
            }
            catch (Exception e) { Guard.Swallow("WeaponFx.muzzle", e); }

            if (_muzzleFlash == null)
                _muzzleFlash = FindMuzzleVisualTarget(all, _weapon);
            if (_muzzleFlash != null)
            {
                _muzzleFlash.SetActive(false);
                SetRenderers(_muzzleFlash, true);
            }

            // Origin for laser / impact ray: the Muzzle node, else the flash, else the source's pivot by name.
            _muzzleOrigin = FindTransformByNameContains(all, "muzzle");
            if (_muzzleOrigin == null && _muzzleFlash != null)
                _muzzleOrigin = _muzzleFlash.transform;
            if (_muzzleOrigin == null && pivot != null)
                _muzzleOrigin = FindByExactName(all, pivot.name);

            // Smoke is often under Muzzle.
            _muzzleSmoke = FindParticleSystemByName(all, "Smoke") ?? FindParticleSystemNearName(all, "muzzle");
            HardenParticle(_muzzleSmoke);
        }

        private void CacheLaser(GameObject source, Transform[] all)
        {
            LineRenderer srcLine = null;
            SpriteRenderer srcPoint = null;
            try
            {
                var srcAl = source != null ? source.GetComponentInChildren<AimLaser>(true) : null;
                if (srcAl != null)
                {
                    srcLine = srcAl.line;
                    if (srcLine != null)
                    {
                        string ln = srcLine.gameObject.name;
                        foreach (var t in all)
                        {
                            if (t == null) continue;
                            var lr = t.GetComponent<LineRenderer>();
                            if (lr != null && (t.name == ln || t.name.IndexOf("laser", StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                _laser = lr;
                                break;
                            }
                        }
                    }
                    if (srcAl.laserPoint != null)
                    {
                        srcPoint = srcAl.laserPoint.GetComponent<SpriteRenderer>();
                        var t = FindByExactName(all, srcAl.laserPoint.name);
                        if (t != null) _laserPoint = t.GetComponent<SpriteRenderer>();
                    }
                    if (srcAl.missedShot != null)
                        _missedShot = FindMatchingPs(all, srcAl.missedShot.gameObject.name);
                    if (srcAl.ricochet != null)
                        _ricochet = FindMatchingPs(all, srcAl.ricochet.gameObject.name);
                    if (srcAl.laserMaxDist > 0.5f)
                        _laserMaxDist = srcAl.laserMaxDist;
                    _laserMask = srcAl.mask;
                    _ricochetChance = srcAl.ricochetChance;
                    _flare = srcAl.flare;
                }
            }
            catch (Exception e) { Guard.Swallow("WeaponFx.laser", e); }

            // IL2CPP Instantiate drops sprite / materials on the clone: copy them from the source laser.
            if (_laserPoint != null && srcPoint != null)
            {
                _laserPoint.sprite = srcPoint.sprite;
                var mats = srcPoint.sharedMaterials;
                if (mats != null && mats.Length > 0) _laserPoint.sharedMaterials = mats;
                else if (srcPoint.sharedMaterial != null) _laserPoint.sharedMaterial = srcPoint.sharedMaterial;
                _laserPoint.color = srcPoint.color;
            }

            if (_laser == null)
                _laser = FindLineRenderer(all);
            if (_laser != null)
            {
                _laser.useWorldSpace = false;
                _laser.enabled = false;
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
            PrepareLaserPoint();

            if (_missedShot == null)
                _missedShot = FindParticleSystemByName(all, "Miss");
            if (_ricochet == null)
                _ricochet = FindParticleSystemByName(all, "Ricochet");
            HardenParticle(_missedShot);
            HardenParticle(_ricochet);
        }

        /// <summary>Case eject: the source ReloadCaseEject's particles, else a Case / Shell / Eject system (never any PS).</summary>
        private void CacheCaseEject(GameObject source, Transform[] all)
        {
            try
            {
                var srcCe = source != null ? source.GetComponentInChildren<ReloadCaseEject>(true) : null;
                if (srcCe != null && srcCe.particles != null)
                    _caseEject = FindMatchingPs(all, srcCe.particles.gameObject.name);
            }
            catch (Exception e) { Guard.Swallow("WeaponFx.eject", e); }
            if (_caseEject == null)
                _caseEject = FindParticleSystemByName(all, "Case")
                    ?? FindParticleSystemByName(all, "Shell")
                    ?? FindParticleSystemByName(all, "Eject");
            HardenParticle(_caseEject);
        }

        private static void SetRenderers(GameObject go, bool enabled)
        {
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r != null) r.enabled = enabled;
            }
        }

        private static void HardenParticle(ParticleSystem ps)
        {
            if (ps == null) return;
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var psr = ps.GetComponent<ParticleSystemRenderer>();
            if (psr != null) psr.enabled = true;
        }

        private static void PlayBurst(ParticleSystem ps)
        {
            if (ps == null) return;
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ps.Play(true);
            ps.Emit(1);
        }

        private static void Stop(ParticleSystem ps, ParticleSystemStopBehavior how)
        {
            if (ps != null) ps.Stop(true, how);
        }

        private static Transform FindByExactName(Transform[] all, string name)
        {
            foreach (var t in all)
            {
                if (t != null && t.name == name) return t;
            }
            return null;
        }

        private static ParticleSystem FindMatchingPs(Transform[] all, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var t in all)
            {
                if (t == null || t.name != name) continue;
                var ps = t.GetComponent<ParticleSystem>();
                if (ps != null) return ps;
            }
            return FindParticleSystemByName(all, name);
        }

        private static GameObject FindMuzzleVisualTarget(Transform[] all, GameObject weaponRoot)
        {
            GameObject best = null;
            int bestScore = -1;
            foreach (var t in all)
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
                if (t.parent != null)
                {
                    string pn = t.parent.name.ToLowerInvariant();
                    if (pn.Contains("muzzle") || pn.Contains("flash") || pn.Contains("halo"))
                        score += 15;
                }
                if (score > bestScore) { bestScore = score; best = mr.gameObject; }
            }
            // Accept only if we scored something muzzle-related.
            return bestScore < 10 ? null : best;
        }

        private static LineRenderer FindLineRenderer(Transform[] all)
        {
            LineRenderer fallback = null;
            foreach (var t in all)
            {
                if (t == null) continue;
                var lr = t.GetComponent<LineRenderer>();
                if (lr == null) continue;
                string n = t.name.ToLowerInvariant();
                if (n.Contains("laser") || n.Contains("aim"))
                    return lr;
                if (fallback == null) fallback = lr;
            }
            return fallback;
        }

        /// <summary>First particle system whose own or parent name contains <paramref name="hint"/>.</summary>
        private static ParticleSystem FindParticleSystemByName(Transform[] all, string hint)
        {
            foreach (var t in all)
            {
                if (t == null) continue;
                var ps = t.GetComponent<ParticleSystem>();
                if (ps == null) continue;
                if (t.name.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0) return ps;
                if (t.parent != null && t.parent.name.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0) return ps;
            }
            return null;
        }

        private static ParticleSystem FindParticleSystemNearName(Transform[] all, string parentHint)
        {
            foreach (var t in all)
            {
                if (t == null) continue;
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

        private static Transform FindTransformByName(Transform[] all, string name)
        {
            foreach (var t in all)
            {
                if (t != null && t.name.Equals(name, StringComparison.OrdinalIgnoreCase)) return t;
            }
            return null;
        }

        /// <summary>A transform whose name contains <paramref name="hint"/>, an exact match preferred.</summary>
        private static Transform FindTransformByNameContains(Transform[] all, string hint)
        {
            Transform best = null;
            foreach (var t in all)
            {
                if (t == null) continue;
                string n = t.name.ToLowerInvariant();
                if (n == hint) return t;
                if (best == null && n.IndexOf(hint, StringComparison.Ordinal) >= 0) best = t;
            }
            return best;
        }

        public void OnShot()
        {
            if (_muzzleFlash != null)
            {
                _muzzleFlash.SetActive(true);
                SetRenderers(_muzzleFlash, true);
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

        /// <summary>
        /// What the sender's shot did, replayed from the proxy's muzzle (native PlayerAttack.RaycastShot + its
        /// hitEvent / missEvent listeners): an enemy hitbox in the way is a hit (HitMissSounds hitMod), anything else a
        /// miss (missMod, and AimLaser.miss: 6 missedShot particles at the wall, a ricochet spark at ricochetChance %).
        /// PlayerAttack.ricochetSound is the shield clang (RaycastShot plays it on a "Shield" collider only), not a
        /// wall impact.
        /// </summary>
        public void DoImpactRaycast(Vector3 origin, Vector3 direction)
        {
            // Melee / thrown: no barrel, nothing to trace.
            if (_muzzleOrigin == null && _laser == null) return;
            int mask = _targetMask != 0 ? _targetMask : _wallMask;
            RaycastHit hit;
            bool hitSomething = Physics.Raycast(origin, direction, out hit, 50f, mask);
            if (hitSomething && IsPlayerHit(hit.collider)) return;
            if (hitSomething && IsEnemyHit(hit.collider))
            {
                QueueImpactSound(_hitPath, hit.point);
                return;
            }
            QueueImpactSound(_missPath, hitSomething ? hit.point : origin);
            if (!hitSomething || _missedShot == null) return;
            var mt = _missedShot.transform;
            mt.position = hit.point;
            mt.LookAt(hit.point + hit.normal);
            _missedShot.Emit(6);
            if (_flare || _ricochet == null || UnityEngine.Random.Range(0f, 100f) >= _ricochetChance) return;
            if (!_ricochet.transform.IsChildOf(mt))
            {
                _ricochet.transform.position = hit.point;
                _ricochet.transform.LookAt(hit.point + hit.normal);
            }
            _ricochet.Emit(1);
        }

        private void QueueImpactSound(string path, Vector3 pos)
        {
            _impactPath = path;
            _impactPos = pos;
            _impactTimer = ImpactSoundDelay;
        }

        private static bool IsEnemyHit(Collider col)
        {
            if (col == null) return false;
            if (col.gameObject.layer == EnemyHitboxLayer) return true;
            return col.GetComponentInParent<EnemyController>() != null;
        }

        private static bool IsPlayerHit(Collider col)
        {
            if (col == null) return false;
            if (col.CompareTag("Player")) return true;
            var local = PlayerState.player;
            return local != null && col.transform.IsChildOf(local.transform);
        }

        public void UpdateLaser(bool aiming)
        {
            if (_laser == null)
            {
                if (_laserPoint != null) _laserPoint.enabled = false;
                return;
            }

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
            if (Physics.Raycast(origin, dir, out RaycastHit hit, _laserMaxDist, _laserMask != 0 ? _laserMask : _wallMask))
            {
                dist = hit.distance;
                if (_laserPoint != null) _laserPoint.transform.position = hit.point;
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
                foreach (var sr in _weapon.GetComponentsInChildren<SpriteRenderer>(true))
                {
                    if (sr == null) continue;
                    string n = sr.name.ToLowerInvariant();
                    if (n.Contains("point") || n.Contains("decal"))
                    {
                        _laserPoint = sr;
                        break;
                    }
                }
            }
            if (_laserPoint == null) return;
            _laserPoint.transform.SetParent(_weapon.transform, true);
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

        // Camera.main per frame per aiming proxy: cached, refetched when destroyed (scene load) or switched off (cutscene camera).
        // Pure cache: intentionally persistent (self-heals on the null / disabled check).
        // persistent: camera cache, self-heals on the null / disabled check
        private static Camera _cam;

        private void BillboardLaserPoint()
        {
            if (_laserPoint == null) return;
            if (_cam == null || !_cam.isActiveAndEnabled) _cam = Camera.main;
            if (_cam == null) return;
            _laserPoint.transform.rotation = Quaternion.LookRotation(_cam.transform.forward, _cam.transform.up);
        }

        public void Tick(float dt)
        {
            if (_impactTimer > 0f)
            {
                _impactTimer -= dt;
                if (_impactTimer <= 0f && !string.IsNullOrEmpty(_impactPath))
                    WorldSfx.Play(_impactPath, _impactPos, 0.4f, WorldSfx.CombatRange);
            }
            if (_flashTimer > 0f)
            {
                _flashTimer -= dt;
                if (_flashTimer <= 0f && _muzzleFlash != null)
                {
                    _muzzleFlash.SetActive(false);
                    SetRenderers(_muzzleFlash, false);
                }
            }
            if (_ejectStopTimer > 0f)
            {
                _ejectStopTimer -= dt;
                if (_ejectStopTimer <= 0f)
                    Stop(_caseEject, ParticleSystemStopBehavior.StopEmitting);
            }
            if (_smokeStopTimer > 0f)
            {
                _smokeStopTimer -= dt;
                if (_smokeStopTimer <= 0f)
                    Stop(_muzzleSmoke, ParticleSystemStopBehavior.StopEmitting);
            }
            if (_slide != null)
            {
                Vector3 p = _slide.localPosition;
                if (p.z < _slideRestPos - 0.001f)
                {
                    p.z = Mathf.Min(_slideRestPos, p.z + (SlideTravel + 0.005f) * (dt / SlideReturn));
                    _slide.localPosition = p;
                }
            }
        }

        /// <summary>Everything off: on build and whenever this weapon is drawn again (clones PlayOnAwake on activation).</summary>
        public void ResetAll()
        {
            Stop(_muzzleSmoke, ParticleSystemStopBehavior.StopEmittingAndClear);
            Stop(_caseEject, ParticleSystemStopBehavior.StopEmittingAndClear);
            Stop(_missedShot, ParticleSystemStopBehavior.StopEmittingAndClear);
            Stop(_ricochet, ParticleSystemStopBehavior.StopEmittingAndClear);
            if (_muzzleFlash != null) _muzzleFlash.SetActive(false);
            if (_laser != null) _laser.enabled = false;
            if (_laserPoint != null) _laserPoint.enabled = false;
        }
    }
}
