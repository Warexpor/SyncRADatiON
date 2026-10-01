// SyncRADation � wrapper per remote player: owns AnimDriver, AudioSync, WeaponSync
using SyncRADation.Networking;
using SyncRADation.Sync;
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

        private readonly ProxyModelVariants _variants;
        private WeaponType _lastWeapon;
        private byte _lastModelState = 255;
        private bool _lastWearHat;
        private PlayerStateMessage _fxState;
        private AnimTriggers _fxTriggers;
        private bool _fxPending;
        private bool _hasFxState;

        public bool LastDead { get; private set; }
        /// <summary>Unscaled time of the last PlayerState applied (stale-proxy cleanup).</summary>
        public float LastStateAt { get; private set; }
        // Reliable AvatarOneShot(Die) and PlayerVital(dead) both announce a death and race each other.
        private float _dieShotAt = -99f;
        private const float DieDedupe = 2f;

        public RemotePlayerProxy(GameObject go, int playerId, ProxyModelVariants variants)
        {
            PlayerId = playerId;
            GameObject = go;
            _variants = variants;
            LastStateAt = Time.unscaledTime;
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
            PlaytestLog.Event("DRV", "proxy p" + playerId + " SMRs=" + arms.Length
                + " armatures=" + armList.Count
                + (armList.Count > 0 ? " " + string.Join(",", armList) : "")
                + " rootY=" + go.transform.eulerAngles.y.ToString("F1"));
        }

        public void Destroy()
        {
            WeaponSync?.Cleanup();
        }

        public void SetVital(bool dead)
        {
            bool wasDead = LastDead;
            LastDead = dead;
            bool revived = !dead && wasDead;
            if (revived)
            {
                // Drop anything the reliable one-shot path queued for the old life.
                _fxTriggers &= ~(AnimTriggers.Die | AnimTriggers.Hurt);
                AnimDriver?.DropPending(AnimTriggers.Die | AnimTriggers.Hurt);
                _dieShotAt = -99f;
            }
            // A one-shot Die that already fired owns this death: a second latched Die would replay the death clip
            // right after the revive.
            bool playDie = dead && !wasDead && Time.unscaledTime - _dieShotAt > DieDedupe;
            try { AnimDriver?.ApplyVital(dead, playDie, revived); }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        public void ApplyState(PlayerStateMessage state)
        {
            LastStateAt = Time.unscaledTime;
            // Weapon before FX tick so first-frame Fire after equip still has a clone
            if (state.Weapon != _lastWeapon)
            {
                PlaytestLog.Event("Weapon", "p" + PlayerId + " " + _lastWeapon + " -> " + state.Weapon);
                WeaponSync.ApplyWeapon(state.Weapon);
                _lastWeapon = state.Weapon;
            }

            AnimDriver.ApplyState(state);
            AudioSync.Tick(state, state.AnimBools, state.AnimTriggers);
            _fxState = state;
            _hasFxState = true;
            _fxTriggers |= state.AnimTriggers;
            _fxPending = true;

            if (state.ModelState != _lastModelState || state.WearHat != _lastWearHat)
            {
                ApplyModel(state.ModelState, state.WearHat);
                _lastModelState = state.ModelState;
                _lastWearHat = state.WearHat;
            }
        }

        /// <summary>Reliable one-shot triggers (AvatarOneShot): same consumers as the pose-carried flags.</summary>
        public void ApplyOneShot(AnimTriggers triggers)
        {
            if (LastDead) triggers &= ~(AnimTriggers.Die | AnimTriggers.Hurt); // already down: no second Die / Hurt
            if (triggers == AnimTriggers.None) return;
            if ((triggers & AnimTriggers.Die) != 0) _dieShotAt = Time.unscaledTime;
            AnimDriver.AddOneShot(triggers);
            _fxTriggers |= triggers;
            _fxPending = true;
            // Audio keys off the triggers only (footstep / ladder / swap state stays with the pose stream).
            if (_hasFxState)
                AudioSync.OnTriggers(_fxState.Weapon, triggers);
        }

        public void LateFxTick()
        {
            if (GameObject == null) return;
            AudioSync?.LateTick();
            if (WeaponSync == null) return;
            Vector3 dir = AnimDriver != null ? AnimDriver.AimDirection : GameObject.transform.forward;
            AnimTriggers trig = _fxPending ? _fxTriggers : 0;
            WeaponSync.Tick(_fxState, _fxState.AnimBools, trig, GameObject.transform.position, dir);
            _fxTriggers = 0;
            _fxPending = false;
        }

        /// <summary>
        /// The sender's outfit: what native CharacterModelType.ApplyType does for the local Elster (one model object
        /// per ElsterType active, hat by wearHat), applied to the objects the builder read from the clone.
        /// </summary>
        private void ApplyModel(byte modelState, bool wearHat)
        {
            if (_variants == null) return;
            try
            {
                var models = _variants.Models;
                for (int i = 0; i < models.Length; i++)
                {
                    if (models[i] != null)
                        models[i].SetActive(i == modelState);
                }
                if (_variants.Hat != null)
                    _variants.Hat.SetActive(wearHat);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[Proxy] ApplyModel failed: " + ex.Message);
            }
        }
    }
}
