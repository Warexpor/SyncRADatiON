// SyncRADation � wrapper per remote player: owns AnimDriver, AudioSync, WeaponSync
using SyncRADation.Networking;
using UnityEngine;

namespace SyncRADation.Players
{
    public sealed class RemotePlayerProxy
    {
        public GameObject GameObject { get; }
        public RemoteAnimatorDriver AnimDriver { get; }
        public ProxyAudioSync AudioSync { get; }
        public RemoteWeaponSync WeaponSync { get; }
        public int PlayerId { get; }

        private WeaponType _lastWeapon;
        private byte _lastModelState = 255;
        private bool _lastWearHat;
        private PlayerStateMessage _fxState;
        private AnimTriggers _fxTriggers;
        private bool _fxPending;

        public bool LastDead { get; private set; }

        public RemotePlayerProxy(GameObject go, int playerId)
        {
            PlayerId = playerId;
            GameObject = go;
            AnimDriver = new RemoteAnimatorDriver(go);
            AnimDriver.Initialize(go);
            AudioSync = new ProxyAudioSync(go);
            WeaponSync = new RemoteWeaponSync(go);
            WeaponSync.OnShotFired = (wpn) => AudioSync.OnWeaponShot(wpn);
            var arms = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var armList = new System.Collections.Generic.List<string>();
            for (int i = 0; i < arms.Length; i++)
            {
                if (arms[i] != null && arms[i].rootBone != null && arms[i].rootBone.parent != null)
                    armList.Add(arms[i].rootBone.parent.name);
            }
            ModRuntime.Log?.Msg("[DRV] Proxy " + playerId + ": " + arms.Length + " SMRs, " + armList.Count + " armatures: " + string.Join(", ", armList) + " | rootY=" + go.transform.eulerAngles.y.ToString("F1"));

            var rends = go.GetComponentsInChildren<Renderer>(true);
            int smrCount = 0, smrValidMesh = 0, nullMat = 0;
            for (int ri = 0; ri < rends.Length; ri++)
            {
                var r = rends[ri];
                if (r == null) continue;
                if (!r.enabled) continue;
                var rType = r.GetType().Name;
                if (rType == "SkinnedMeshRenderer")
                {
                    smrCount++;
                    SkinnedMeshRenderer smr = (SkinnedMeshRenderer)r;
                    if (smr.sharedMesh != null) smrValidMesh++;
                    if (smr.sharedMaterial == null) nullMat++;
                }
            }
            ModRuntime.Log?.Msg("[DRV] Proxy " + playerId + " renderers: " + rends.Length + " total, " + smrCount + " SMRs, " + smrValidMesh + " valid meshes, " + nullMat + " null mats");
        }

        public void Destroy()
        {
            WeaponSync?.Cleanup();
        }

        public void SetVital(bool dead)
        {
            bool wasDead = LastDead;
            LastDead = dead;

            try
            {
                var anim = GameObject != null ? GameObject.GetComponentInChildren<Animator>(true) : null;
                if (anim != null)
                {
                    anim.SetBool("Dead", dead);
                    if (dead && !wasDead)
                        anim.SetTrigger("Die");
                }
            }
            catch { }
        }

        public void ApplyState(PlayerStateMessage state)
        {
            // Weapon before FX tick so first-frame Fire after equip still has a clone
            if (state.Weapon != _lastWeapon)
            {
                ModRuntime.Log?.Msg("[WeaponSync] Proxy " + PlayerId + " weapon: " + _lastWeapon + " -> " + state.Weapon);
                WeaponSync.ApplyWeapon(state.Weapon);
                _lastWeapon = state.Weapon;
            }

            AnimDriver.ApplyState(state);
            AudioSync.Tick(state, state.AnimBools, state.AnimTriggers);
            _fxState = state;
            _fxTriggers |= state.AnimTriggers;
            _fxPending = true;

            if (state.ModelState != _lastModelState || state.WearHat != _lastWearHat)
            {
                ApplyModel(state.ModelState, state.WearHat);
                _lastModelState = state.ModelState;
                _lastWearHat = state.WearHat;
            }
        }

        public void LateFxTick()
        {
            if (WeaponSync == null || GameObject == null) return;
            Vector3 dir = AnimDriver != null ? AnimDriver.AimDirection : GameObject.transform.forward;
            AnimTriggers trig = _fxPending ? _fxTriggers : 0;
            WeaponSync.Tick(_fxState, _fxState.AnimBools, trig, GameObject.transform.position, dir);
            _fxTriggers = 0;
            _fxPending = false;
        }

        private static readonly object _modelApplyLock = new object();

        private void ApplyModel(byte modelState, bool wearHat)
        {
            try
            {
                var cmt = GameObject.GetComponentInChildren<CharacterModelType>(true);
                if (cmt == null) return;
                cmt.modelState = (CharacterModelType.ElsterType)modelState;
                lock (_modelApplyLock)
                {
                    var prev = CharacterModelType.instance;
                    var prevHat = CharacterModelType.wearHat;
                    CharacterModelType.instance = cmt;
                    CharacterModelType.wearHat = wearHat;
                    CharacterModelType.ApplyType();
                    CharacterModelType.instance = prev;
                    CharacterModelType.wearHat = prevHat;
                }
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[Proxy] ApplyModel failed: " + ex.Message);
            }
        }
    }
}

