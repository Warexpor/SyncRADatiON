// A proxy's held weapon: script-free clones of the local Elster's weapon objects (IL2CPP mesh / material fix-up),
// one per weapon type, toggled by the sender's equipped weapon; plus the shared AnWeapon.Damage table.
using System.Collections.Generic;
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Players
{
    public sealed class RemoteWeaponSync
    {
        // Game data, intentionally persistent: AnWeapon.Damage per type does not change between sessions.
        private static bool _damageCacheBuilt;
        private static readonly Dictionary<WeaponType, float> _weaponDamageCache = new Dictionary<WeaponType, float>();

        private static void BuildDamageCache()
        {
            if (_damageCacheBuilt) return;
            _damageCacheBuilt = true;
            try
            {
                foreach (var w in Resources.FindObjectsOfTypeAll<AnWeapon>())
                {
                    if (w == null || w.parentItem == null) continue;
                    var wt = WeaponUtils.ItemToWeaponType(w.parentItem._item);
                    if (wt != WeaponType.None && !_weaponDamageCache.ContainsKey(wt))
                        _weaponDamageCache[wt] = w.Damage;
                }
                PlaytestLog.Verbose("Weapon", "damage cache " + _weaponDamageCache.Count);
            }
            catch (System.Exception e) { Guard.Swallow("Weapon.damageCache", e); }
        }

        public static float GetDamage(WeaponType wt)
        {
            BuildDamageCache();
            return _weaponDamageCache.TryGetValue(wt, out float d) ? d : 30f;
        }

        // Fallback muzzle: chest height above the proxy's feet (SIGNALIS walks the XY plane, up is -Z).
        private static readonly Vector3 MuzzleHeight = new Vector3(0f, 0f, -0.95f);

        private readonly GameObject _proxy;
        private readonly Dictionary<WeaponType, GameObject> _weapons = new Dictionary<WeaponType, GameObject>();
        private readonly Dictionary<WeaponType, RemoteWeaponEffects> _effects = new Dictionary<WeaponType, RemoteWeaponEffects>();
        private readonly int _targetLayer;
        private WeaponType _currentWeapon = WeaponType.None;

        /// <summary>Secondary shot sounds (case land, shotgun pump) once the muzzle FX played.</summary>
        public System.Action<WeaponType> OnShotFired;

        public RemoteWeaponSync(GameObject proxy)
        {
            _proxy = proxy;
            foreach (var smr in proxy.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr != null && smr.gameObject.layer != 0)
                {
                    _targetLayer = smr.gameObject.layer;
                    break;
                }
            }
        }

        public void ApplyWeapon(WeaponType weapon)
        {
            if (weapon == _currentWeapon) return;
            if (_currentWeapon != WeaponType.None && _weapons.TryGetValue(_currentWeapon, out var old) && old != null)
                old.SetActive(false);
            _currentWeapon = weapon;
            if (weapon == WeaponType.None) return;

            if (!_weapons.TryGetValue(weapon, out var go) || go == null)
            {
                float t0 = Time.realtimeSinceStartup;
                go = CreateFromSource(weapon);
                HitchTrace.Cost("weaponClone", (Time.realtimeSinceStartup - t0) * 1000f);
                if (go == null) return;
                _weapons[weapon] = go;
            }
            go.SetActive(true);
            // Stop the particles PlayOnAwake starts on activation.
            if (_effects.TryGetValue(weapon, out var fx))
                fx.ResetAll();
        }

        /// <summary>Every frame after the body pose: FX timers, the shot / reload cues and the laser of the drawn weapon.</summary>
        public void Tick(bool aiming, AvatarCue cues, Vector3 proxyPos, Vector3 aimDir)
        {
            if (_currentWeapon == WeaponType.None || !_effects.TryGetValue(_currentWeapon, out var fx)) return;

            // Shot ray from the posed muzzle (before the slide kick moves it), along the laser / barrel.
            Vector3 muzzle = default, dir = default;
            bool fire = (cues & AvatarCue.Fire) != 0;
            if (fire)
            {
                dir = aimDir.sqrMagnitude > 0.0001f ? aimDir.normalized : Vector3.forward;
                if (!fx.TryGetMuzzleWorldPos(out muzzle))
                    muzzle = proxyPos + MuzzleHeight + dir * 0.35f;
                if (fx.TryGetLaserForward(out var d) || fx.TryGetMuzzleForward(out d))
                    dir = d;
            }

            fx.Tick(Time.unscaledDeltaTime);
            if (fire)
            {
                fx.OnShot();
                fx.DoImpactRaycast(muzzle, dir);
                OnShotFired?.Invoke(_currentWeapon);
            }
            if ((cues & AvatarCue.Reload) != 0)
                fx.OnReload();
            fx.UpdateLaser(aiming);
        }

        private GameObject CreateFromSource(WeaponType weapon)
        {
            Transform source = FindSourceWeapon(weapon);
            if (source == null)
            {
                PlaytestLog.Warn("Weapon", "no source for " + weapon);
                return null;
            }

            // Same mount as on the local Elster (found by name in the clone), else the proxy root.
            Transform parent = FindByName(_proxy.transform, source.parent.name) ?? _proxy.transform;
            GameObject clone;
            try { clone = Object.Instantiate(source.gameObject, parent, false); }
            catch (System.Exception ex)
            {
                PlaytestLog.Warn("Weapon", "instantiate " + weapon + " failed: " + ex.Message);
                return null;
            }
            clone.name = weapon.ToString();
            CopyRenderAssets(source, clone.transform);
            SetLayerRecursive(clone.transform, _targetLayer);

            // Effects first: they read the source's (and clone's) components before the clone's scripts go.
            _effects[weapon] = new RemoteWeaponEffects(clone, source.gameObject);
            // The clone's scripts would read null serialized fields and fight the manual effect driving.
            foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb != null) Object.DestroyImmediate(mb, true);
            }
            clone.SetActive(false);
            PlaytestLog.Verbose("Weapon", "cloned " + weapon + " from '" + source.name + "' under '" + parent.name + "'");
            return clone;
        }

        /// <summary>
        /// IL2CPP Instantiate drops sharedMesh / materials: copy them from the source by index per renderer type, then
        /// by path below the weapon root (index order can diverge).
        /// </summary>
        private static void CopyRenderAssets(Transform src, Transform dst)
        {
            var srcSmrs = src.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var dstSmrs = dst.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < srcSmrs.Length && i < dstSmrs.Length; i++)
            {
                var s = srcSmrs[i];
                var d = dstSmrs[i];
                if (s == null || d == null) continue;
                if (d.sharedMesh == null && s.sharedMesh != null) d.sharedMesh = s.sharedMesh;
                if (d.sharedMaterial == null && s.sharedMaterial != null) d.sharedMaterial = s.sharedMaterial;
            }
            var srcMfs = src.GetComponentsInChildren<MeshFilter>(true);
            var dstMfs = dst.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < srcMfs.Length && i < dstMfs.Length; i++)
            {
                var s = srcMfs[i];
                var d = dstMfs[i];
                if (s == null || d == null) continue;
                if (d.sharedMesh == null && s.sharedMesh != null) d.sharedMesh = s.sharedMesh;
            }
            // Includes renderers without a MeshFilter (Quad, MuzzleFlash).
            var srcMrs = src.GetComponentsInChildren<MeshRenderer>(true);
            var dstMrs = dst.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < srcMrs.Length && i < dstMrs.Length; i++)
            {
                var s = srcMrs[i];
                var d = dstMrs[i];
                if (s == null || d == null) continue;
                if (d.sharedMaterial == null && s.sharedMaterial != null) d.sharedMaterial = s.sharedMaterial;
            }
            var srcLrs = src.GetComponentsInChildren<LineRenderer>(true);
            var dstLrs = dst.GetComponentsInChildren<LineRenderer>(true);
            for (int i = 0; i < srcLrs.Length && i < dstLrs.Length; i++)
            {
                var s = srcLrs[i];
                var d = dstLrs[i];
                if (s == null || d == null) continue;
                var mats = s.sharedMaterials;
                if (mats != null && mats.Length > 0) d.sharedMaterials = mats;
                else if (s.sharedMaterial != null) d.sharedMaterial = s.sharedMaterial;
                d.useWorldSpace = false;
                d.enabled = false;
            }
            var srcPsrs = src.GetComponentsInChildren<ParticleSystemRenderer>(true);
            var dstPsrs = dst.GetComponentsInChildren<ParticleSystemRenderer>(true);
            for (int i = 0; i < srcPsrs.Length && i < dstPsrs.Length; i++)
            {
                var s = srcPsrs[i];
                var d = dstPsrs[i];
                if (s == null || d == null) continue;
                var mats = s.sharedMaterials;
                if (mats != null && mats.Length > 0) d.sharedMaterials = mats;
                else if (d.sharedMaterial == null && s.sharedMaterial != null) d.sharedMaterial = s.sharedMaterial;
            }

            var dstByPath = new Dictionary<string, Renderer>();
            foreach (var r in dst.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                string rel = RelativePath(r.transform, dst);
                if (!dstByPath.ContainsKey(rel)) dstByPath[rel] = r;
                if (!dstByPath.ContainsKey(r.name)) dstByPath[r.name] = r; // leaf-name fallback
            }
            foreach (var s in src.GetComponentsInChildren<Renderer>(true))
            {
                if (s == null || s.sharedMaterial == null) continue;
                if (!dstByPath.TryGetValue(RelativePath(s.transform, src), out var d) && !dstByPath.TryGetValue(s.name, out d))
                    continue;
                if (d.sharedMaterial == null) d.sharedMaterial = s.sharedMaterial;
                var mats = s.sharedMaterials;
                if (mats != null && mats.Length > 0) d.sharedMaterials = mats;
            }
        }

        private static string RelativePath(Transform t, Transform root)
        {
            if (t == root) return "";
            string p = t.name;
            for (var a = t.parent; a != null && a != root; a = a.parent)
                p = a.name + "/" + p;
            return p;
        }

        private static Transform FindByName(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t != null && t.name == name) return t;
            }
            return null;
        }

        /// <summary>The local Elster's object for this weapon type: an exact name ("Pistol", "Pistol(Clone)") wins.</summary>
        private static Transform FindSourceWeapon(WeaponType weapon)
        {
            var player = PlayerState.player;
            if (player == null) return null;
            Transform best = null;
            string wepLow = weapon.ToString().ToLowerInvariant();
            foreach (var t in player.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || !MatchesWeapon(t.name, weapon)) continue;
                string low = t.name.ToLowerInvariant();
                if (low == wepLow || low.StartsWith(wepLow + "("))
                    return t;
                if (best == null) best = t;
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

        private static void SetLayerRecursive(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++)
                SetLayerRecursive(t.GetChild(i), layer);
        }
    }
}
