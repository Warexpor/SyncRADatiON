// SyncRADation � weapon model clone with mesh/material fix, damage cache from AnWeapon.Damage
using SyncRADation.Networking;
using SyncRADation.Sync;
using System.Collections.Generic;
using UnityEngine;

namespace SyncRADation.Players
{
    public sealed class RemoteWeaponSync
    {
        private static bool _damageCacheBuilt;
        private static readonly Dictionary<WeaponType, float> _weaponDamageCache = new Dictionary<WeaponType, float>();

        private static void BuildDamageCache()
        {
            if (_damageCacheBuilt) return;
            _damageCacheBuilt = true;
            try
            {
                var allWeapons = Resources.FindObjectsOfTypeAll<AnWeapon>();
                foreach (var w in allWeapons)
                {
                    if (w == null || w.parentItem == null) continue;
                    var wt = WeaponUtils.ItemToWeaponType(w.parentItem._item);
                    if (wt != WeaponType.None && !_weaponDamageCache.ContainsKey(wt))
                        _weaponDamageCache[wt] = w.Damage;
                }
                ModRuntime.Log?.Msg("[WeaponSync] Damage cache built: " + _weaponDamageCache.Count + " weapons");
            }
            catch { }
        }

        public static float GetDamage(WeaponType wt)
        {
            BuildDamageCache();
            if (_weaponDamageCache.TryGetValue(wt, out float d)) return d;
            return 30f; // fallback
        }

        private readonly GameObject _proxy;
        private readonly Dictionary<WeaponType, GameObject> _weapons = new Dictionary<WeaponType, GameObject>();
        private readonly Dictionary<WeaponType, RemoteWeaponEffects> _effects = new Dictionary<WeaponType, RemoteWeaponEffects>();
        private readonly Dictionary<WeaponType, Transform> _sourceWeaponCache = new Dictionary<WeaponType, Transform>();
        private WeaponType _currentWeapon = WeaponType.None;
        private int _targetLayer;
        private GameObject _source;
        private AnimBools _lastBools;
        private Vector3 _facingDir = Vector3.forward;
        private Vector3 _muzzlePos;

        public RemoteWeaponSync(GameObject proxy)
        {
            _proxy = proxy;
            FindTargetLayer();
            _source = FindSourcePlayer();
        }

        private void FindTargetLayer()
        {
            var smrs = _proxy.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var smr in smrs)
                if (smr != null && smr.gameObject.layer != 0) { _targetLayer = smr.gameObject.layer; return; }
        }

        public void ApplyWeapon(WeaponType weapon)
        {
            if (weapon == _currentWeapon) return;

            HideCurrent();

            if (weapon == WeaponType.None) { _currentWeapon = WeaponType.None; return; }

            if (!_weapons.TryGetValue(weapon, out var go) || go == null)
            {
                float t0 = Time.realtimeSinceStartup;
                go = CreateFromSource(weapon);
                HitchTrace.Cost("weaponClone", (Time.realtimeSinceStartup - t0) * 1000f);
                if (go != null) _weapons[weapon] = go;
            }

            if (go != null)
            {
                go.SetActive(true);
                // Stop auto-playing particles (PlayOnAwake on weapon switch)
                if (_effects.TryGetValue(weapon, out var fx))
                    fx.ResetAll();
            }
            _currentWeapon = weapon;
        }

        public System.Action<WeaponType> OnShotFired; // callback for secondary sounds (pump, eject)

        public void Tick(PlayerStateMessage state, AnimBools bools, AnimTriggers triggers, Vector3 proxyPos, Vector3 aimDir)
        {
            _facingDir = aimDir.sqrMagnitude > 0.0001f ? aimDir.normalized : Vector3.forward;

            if (_currentWeapon != WeaponType.None && _effects.TryGetValue(_currentWeapon, out var fxMuzzle))
            {
                Vector3 m;
                if (fxMuzzle.TryGetMuzzleWorldPos(out m))
                    _muzzlePos = m;
                else
                    _muzzlePos = proxyPos + Vector3.up * 0.95f + _facingDir * 0.35f;
                Vector3 mdir;
                if (fxMuzzle.TryGetLaserForward(out mdir) || fxMuzzle.TryGetMuzzleForward(out mdir))
                    _facingDir = mdir;
            }
            else
                _muzzlePos = proxyPos + Vector3.up * 0.95f + _facingDir * 0.35f;

            if (_currentWeapon != WeaponType.None && _effects.TryGetValue(_currentWeapon, out var fx))
            {
                fx.Tick(Time.deltaTime);

                // Only the ammo-spent Fire pulse. Held Fire1 used to retrigger FX every dropped packet.
                if (triggers.HasFlag(AnimTriggers.Fire))
                {
                    fx.OnShot();
                    DoImpactRaycast(GetDamage(_currentWeapon));
                    var cb = OnShotFired;
                    if (cb != null) cb(_currentWeapon);
                }

                if (triggers.HasFlag(AnimTriggers.ReloadTrigger))
                    fx.OnReload();

                bool aiming = bools.HasFlag(AnimBools.Aiming) || state.AimingTime > 0.5f;
                fx.UpdateLaser(aiming);
            }

            _lastBools = bools;
        }

        private void DoImpactRaycast(float damage)
        {
            if (_currentWeapon == WeaponType.None) return;
            if (!_effects.TryGetValue(_currentWeapon, out var fx)) return;
            fx.DoImpactRaycast(_muzzlePos, _facingDir, damage);
        }

        private void HideCurrent()
        {
            if (_currentWeapon != WeaponType.None && _weapons.TryGetValue(_currentWeapon, out var old) && old != null)
                old.SetActive(false);
        }

        private GameObject CreateFromSource(WeaponType weapon)
        {
            if (_source == null) _source = FindSourcePlayer();
            if (_source == null) return null;

            Transform sourceWeaponTransform = FindSourceWeapon(weapon);

            if (sourceWeaponTransform == null)
            {
                ModRuntime.Log?.Msg("[WeaponSync] No source weapon found for " + weapon);
                return null;
            }

            Transform proxyParent = FindMatchingBone(sourceWeaponTransform.parent);
            if (proxyParent == null)
            {
                ModRuntime.Log?.Msg("[WeaponSync] No matching parent on proxy for " + sourceWeaponTransform.parent.name);
                proxyParent = _proxy.transform;
            }

            GameObject clone;
            try { clone = Object.Instantiate(sourceWeaponTransform.gameObject, proxyParent, false); }
            catch (System.Exception ex) { ModRuntime.Log?.Msg("[WeaponSync] Instantiate failed: " + ex.Message); return null; }
            clone.name = weapon.ToString();

            var srcSmrs = sourceWeaponTransform.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var dstSmrs = clone.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var srcMfs = sourceWeaponTransform.GetComponentsInChildren<MeshFilter>(true);
            var dstMfs = clone.GetComponentsInChildren<MeshFilter>(true);
            int fixedCount = 0;
            for (int i = 0; i < srcSmrs.Length && i < dstSmrs.Length; i++)
            {
                if (srcSmrs[i] == null || dstSmrs[i] == null) continue;
                if (dstSmrs[i].sharedMesh == null && srcSmrs[i].sharedMesh != null)
                { dstSmrs[i].sharedMesh = srcSmrs[i].sharedMesh; fixedCount++; }
                if (dstSmrs[i].sharedMaterial == null && srcSmrs[i].sharedMaterial != null)
                    dstSmrs[i].sharedMaterial = srcSmrs[i].sharedMaterial;
            }
            for (int i = 0; i < srcMfs.Length && i < dstMfs.Length; i++)
            {
                if (srcMfs[i] == null || dstMfs[i] == null) continue;
                if (dstMfs[i].sharedMesh == null && srcMfs[i].sharedMesh != null)
                { dstMfs[i].sharedMesh = srcMfs[i].sharedMesh; fixedCount++; }
                var mr = dstMfs[i].GetComponent<MeshRenderer>();
                var srcMr = srcMfs[i].GetComponent<MeshRenderer>();
                if (mr != null && srcMr != null && mr.sharedMaterial == null && srcMr.sharedMaterial != null)
                    mr.sharedMaterial = srcMr.sharedMaterial;
            }
            // Fix MeshRenderers without MeshFilter (Quad, MuzzleFlash — IL2CPP nulls sharedMaterial)
            var srcMrs = sourceWeaponTransform.GetComponentsInChildren<MeshRenderer>(true);
            var dstMrs = clone.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < srcMrs.Length && i < dstMrs.Length; i++)
            {
                if (srcMrs[i] == null || dstMrs[i] == null) continue;
                if (dstMrs[i].sharedMaterial == null && srcMrs[i].sharedMaterial != null)
                    dstMrs[i].sharedMaterial = srcMrs[i].sharedMaterial;
            }
            // Fix LineRenderer + ParticleSystemRenderer materials (null after IL2CPP Instantiate)
            var srcLrs = sourceWeaponTransform.GetComponentsInChildren<LineRenderer>(true);
            var dstLrs = clone.GetComponentsInChildren<LineRenderer>(true);
            for (int i = 0; i < srcLrs.Length && i < dstLrs.Length; i++)
            {
                if (srcLrs[i] == null || dstLrs[i] == null) continue;
                if (srcLrs[i].sharedMaterial != null)
                    dstLrs[i].sharedMaterial = srcLrs[i].sharedMaterial;
                if (srcLrs[i].materials != null && srcLrs[i].materials.Length > 0)
                {
                    try
                    {
                        var mats = srcLrs[i].sharedMaterials;
                        if (mats != null && mats.Length > 0)
                            dstLrs[i].sharedMaterials = mats;
                    }
                    catch { }
                }
                dstLrs[i].useWorldSpace = false;
                dstLrs[i].enabled = false;
            }
            var srcPsrs = sourceWeaponTransform.GetComponentsInChildren<ParticleSystemRenderer>(true);
            var dstPsrs = clone.GetComponentsInChildren<ParticleSystemRenderer>(true);
            for (int i = 0; i < srcPsrs.Length && i < dstPsrs.Length; i++)
            {
                if (srcPsrs[i] == null || dstPsrs[i] == null) continue;
                if (dstPsrs[i].sharedMaterial == null && srcPsrs[i].sharedMaterial != null)
                    dstPsrs[i].sharedMaterial = srcPsrs[i].sharedMaterial;
                if (srcPsrs[i].sharedMaterials != null && srcPsrs[i].sharedMaterials.Length > 0)
                {
                    try { dstPsrs[i].sharedMaterials = srcPsrs[i].sharedMaterials; } catch { }
                }
            }
            // Path-matched renderer material pass (index order can diverge after IL2CPP Instantiate)
            CopyMaterialsByPath(sourceWeaponTransform, clone.transform);
            ModRuntime.Log?.Msg("[WeaponSync] Cloned " + weapon + " (from '" + sourceWeaponTransform.name + "') fixed " + fixedCount + " meshes");

            SetLayerRecursive(clone, _targetLayer);

            // Create effects BEFORE destroying MBs (needs component refs for precise finding)
            var fx = new RemoteWeaponEffects(clone, sourceWeaponTransform.gameObject);

            // Destroy all MBs on weapon clone — IL2CPP native methods (Awake/Start/Update)
            // would try to read null serialized fields and interfere with manual effect driving
#if true
            int mbsKilled = DestroyAllMBs(clone);
            ModRuntime.Log?.Msg("[WeaponSync] Destroyed " + mbsKilled + " MBs on " + weapon + " clone");
#endif
            _effects[weapon] = fx;

            clone.SetActive(false);
            return clone;
        }

        private Transform FindMatchingBone(Transform sourceBone)
        {
            var all = _proxy.GetComponentsInChildren<Transform>(true);
            string name = sourceBone.name;
            foreach (var t in all)
                if (t != null && t.name == name) return t;
            return null;
        }

        private Transform FindSourceWeapon(WeaponType weapon)
        {
            if (_sourceWeaponCache.TryGetValue(weapon, out var cached) && cached != null)
                return cached;
            if (_source == null) return null;
            var all = _source.GetComponentsInChildren<Transform>(true);
            Transform best = null;
            string wepLow = weapon.ToString().ToLowerInvariant();
            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i];
                if (t == null || !MatchesWeapon(t.name, weapon)) continue;
                string low = t.name.ToLowerInvariant();
                bool exact = low == wepLow || low == wepLow + "(clone)" || low.StartsWith(wepLow + "(");
                if (best == null || exact)
                {
                    best = t;
                    if (exact) break;
                }
            }
            if (best != null)
            {
                _sourceWeaponCache[weapon] = best;
                ModRuntime.Log?.Msg("[WeaponSync] source '" + best.name + "' for " + weapon);
            }
            return best;
        }

        private static bool MatchesWeapon(string name, WeaponType weapon)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var low = name.ToLowerInvariant();
            switch (weapon)
            {
                case WeaponType.Handgun: return low.Contains("handgun");
                case WeaponType.Pistol: return low.Contains("pistol");
                case WeaponType.Revolver: return low.Contains("revolver");
                case WeaponType.Shotgun: return low.Contains("shotgun");
                case WeaponType.Rifle: return low.Contains("rifle");
                case WeaponType.SMG: return low.Contains("smg") || low.Contains("machine");
                case WeaponType.Flare: return low.Contains("flaregun");
                case WeaponType.CAR: return low.Contains("car") && !low.Contains("flare");
                case WeaponType.Melee: return low.Contains("machete") || low.Contains("melee");
                default: return false;
            }
        }

        private static void SetLayerRecursive(GameObject obj, int layer)
        {
            obj.layer = layer;
            for (int i = 0; i < obj.transform.childCount; i++)
                SetLayerRecursive(obj.transform.GetChild(i).gameObject, layer);
        }

        private static void CopyMaterialsByPath(Transform srcRoot, Transform dstRoot)
        {
            if (srcRoot == null || dstRoot == null) return;
            var srcAll = srcRoot.GetComponentsInChildren<Renderer>(true);
            var dstAll = dstRoot.GetComponentsInChildren<Renderer>(true);
            var dstByRel = new Dictionary<string, Renderer>();
            string dstRootPath = GetPath(dstRoot);
            foreach (var r in dstAll)
            {
                if (r == null) continue;
                string rel = GetPath(r.transform);
                if (rel.StartsWith(dstRootPath))
                    rel = rel.Length > dstRootPath.Length ? rel.Substring(dstRootPath.Length).TrimStart('/') : "";
                if (!dstByRel.ContainsKey(rel))
                    dstByRel[rel] = r;
                // also key by leaf name as fallback
                if (!dstByRel.ContainsKey(r.name))
                    dstByRel[r.name] = r;
            }
            string srcRootPath = GetPath(srcRoot);
            int copied = 0;
            foreach (var sr in srcAll)
            {
                if (sr == null || sr.sharedMaterial == null) continue;
                string rel = GetPath(sr.transform);
                if (rel.StartsWith(srcRootPath))
                    rel = rel.Length > srcRootPath.Length ? rel.Substring(srcRootPath.Length).TrimStart('/') : "";
                Renderer dr = null;
                if (!dstByRel.TryGetValue(rel, out dr))
                    dstByRel.TryGetValue(sr.name, out dr);
                if (dr == null) continue;
                try
                {
                    if (dr.sharedMaterial == null)
                        dr.sharedMaterial = sr.sharedMaterial;
                    if (sr.sharedMaterials != null && sr.sharedMaterials.Length > 0)
                        dr.sharedMaterials = sr.sharedMaterials;
                    copied++;
                }
                catch { }
            }
            if (copied > 0)
                ModRuntime.Log?.Msg("[WeaponSync] Path-matched materials: " + copied);
        }

        private static int DestroyAllMBs(GameObject obj)
        {
            int count = 0;
            var mbs = obj.GetComponentsInChildren<MonoBehaviour>(true);
            foreach (var mb in mbs)
            {
                if (mb == null) continue;
                Object.DestroyImmediate(mb, true);
                count++;
            }
            return count;
        }

        private static string GetPath(Transform t)
        {
            if (t == null) return "";
            string p = t.name;
            while (t.parent != null) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }

        private static GameObject FindSourcePlayer()
        {
            try
            {
                var p = PlayerState.player;
                if (p != null) return p;
            }
            catch { }
            return null;
        }

        public void Cleanup()
        {
            foreach (var fx in _effects.Values)
                fx.Cleanup();
            _effects.Clear();
            _weapons.Clear();
            _sourceWeaponCache.Clear();
        }
    }
}
