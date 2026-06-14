// SyncRADation — weapon model clone with mesh/material fix, damage cache from AnWeapon.Damage
using SyncRADation.Networking;
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
                    var wt = MapItemToWeaponType(w.parentItem._item);
                    if (wt != WeaponType.None && !_weaponDamageCache.ContainsKey(wt))
                        _weaponDamageCache[wt] = w.Damage;
                }
                ModRuntime.Log?.Msg("[WeaponSync] Damage cache built: " + _weaponDamageCache.Count + " weapons");
            }
            catch { }
        }

        private static WeaponType MapItemToWeaponType(Items.itemlist item)
        {
            return WeaponUtils.ItemToWeaponType(item);
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
                go = CreateFromSource(weapon);
                if (go != null) _weapons[weapon] = go;
            }

            if (go != null) go.SetActive(true);
            _currentWeapon = weapon;
        }

        public System.Action<WeaponType> OnShotFired; // callback for secondary sounds (pump, eject)

        public void Tick(PlayerStateMessage state, AnimBools bools, AnimTriggers triggers, Vector3 proxyPos, float facingAngle)
        {
            // Update muzzle position from proxy + facing
            _muzzlePos = proxyPos + Vector3.up * 0.8f;
            _facingDir = Quaternion.Euler(0, facingAngle, 0) * Vector3.forward;

            // Tick current weapon effects
            if (_currentWeapon != WeaponType.None && _effects.TryGetValue(_currentWeapon, out var fx))
            {
                fx.Tick(Time.deltaTime);

                // Shot detection (leading edge)
                bool curShoot = bools.HasFlag(AnimBools.Shooting);
                if (curShoot && !_lastBools.HasFlag(AnimBools.Shooting))
                {
                    fx.OnShot();
                    DoImpactRaycast(GetDamage(_currentWeapon));
                    var cb = OnShotFired;
                    if (cb != null) cb(_currentWeapon);
                }

                // Reload trigger
                if (triggers.HasFlag(AnimTriggers.ReloadTrigger))
                    fx.OnReload();

                // Laser
                bool aiming = bools.HasFlag(AnimBools.Aiming);
                fx.UpdateLaser(aiming, _muzzlePos, _facingDir, 30f);
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

            var all = _source.GetComponentsInChildren<Transform>(true);
            Transform sourceWeaponTransform = null;
            int candidates = 0;
            foreach (var t in all)
            {
                if (t == null) continue;
                if (t.GetComponentsInChildren<Renderer>(true).Length == 0) continue;
                if (MatchesWeapon(t.name, weapon))
                {
                    candidates++;
                    string low = t.name.ToLowerInvariant();
                    string wepLow = weapon.ToString().ToLowerInvariant();
                    bool exact = low == wepLow || low == wepLow + "(clone)" || low.StartsWith(wepLow + "(");
                    if (sourceWeaponTransform == null || exact)
                    {
                        sourceWeaponTransform = t;
                        ModRuntime.Log?.Msg("[WeaponSync] " + (exact ? "EXACT" : "partial") + " match '" + t.name + "' at " + GetPath(t) + " for " + weapon);
                        if (exact) break;
                    }
                }
            }
            ModRuntime.Log?.Msg("[WeaponSync] Scan " + all.Length + " transforms, " + candidates + " match " + weapon);

            if (sourceWeaponTransform == null)
            {
                int dumped = 0;
                foreach (var t in all)
                {
                    if (t == null || dumped >= 20) continue;
                    if (t.GetComponentsInChildren<Renderer>(true).Length > 0)
                    {
                        ModRuntime.Log?.Msg("[WeaponSync]   (no match) '" + t.name + "' at " + GetPath(t));
                        dumped++;
                    }
                }
            }

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
            ModRuntime.Log?.Msg("[WeaponSync] Cloned " + weapon + " (from '" + sourceWeaponTransform.name + "') fixed " + fixedCount + " meshes");

            SetLayerRecursive(clone, _targetLayer);

            // Create effects for this weapon
            var fx = new RemoteWeaponEffects(clone, sourceWeaponTransform.gameObject);
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

        private static bool MatchesWeapon(string name, WeaponType weapon)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var low = name.ToLowerInvariant();
            switch (weapon)
            {
                case WeaponType.Handgun: return low.Contains("taser") || low.Contains("handgun");
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
        }
    }
}
