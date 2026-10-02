// One remote player: body pose, outfit, weapon model / FX and positional sounds on its model-only clone.
using SyncRADation.Networking;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Players
{
    public sealed class RemotePlayerProxy
    {
        public GameObject GameObject { get; }
        public int PlayerId { get; }
        public ProxyMotion Motion { get; }
        public ProxyPose Pose { get; }

        private readonly ProxyAudioSync _audio;
        private readonly RemoteWeaponSync _weapon;
        private readonly ProxyRig _rig;
        private WeaponType _lastWeapon;
        private int _lastOutfit = -1;
        private bool _aiming;
        private AvatarCue _pendingFx;

        public bool LastDead { get; private set; }
        /// <summary>Unscaled time of the last PlayerState applied (stale-proxy cleanup).</summary>
        public float LastStateAt { get; private set; }

        public RemotePlayerProxy(GameObject go, int playerId, ProxyRig rig)
        {
            PlayerId = playerId;
            GameObject = go;
            _rig = rig;
            LastStateAt = Time.unscaledTime;
            Motion = new ProxyMotion(go.transform);
            Pose = new ProxyPose(rig.Model, rig.Hips);
            _audio = new ProxyAudioSync(go);
            _weapon = new RemoteWeaponSync(go);
            _weapon.OnShotFired = _audio.OnWeaponShot;
        }

        /// <summary>PlayerVital: the downed flag (the death pose itself arrives with the sender's bones).</summary>
        public void SetVital(bool dead)
        {
            LastDead = dead;
        }

        public void ApplyState(PlayerStateMessage state)
        {
            LastStateAt = Time.unscaledTime;
            // Weapon before the FX tick so a Fire right after an equip already has a clone.
            if (state.Weapon != _lastWeapon)
            {
                PlaytestLog.Event("Weapon", "p" + PlayerId + " " + _lastWeapon + " -> " + state.Weapon);
                _weapon.ApplyWeapon(state.Weapon);
                _lastWeapon = state.Weapon;
            }

            Motion.OnState(new Vector3(state.PosX, state.PosY, state.PosZ), new Vector3(state.VelX, state.VelY, 0f),
                state.GetFacingWorld(), (state.Flags & (PoseFlags.Climbing | PoseFlags.Scripted)) != 0);
            Pose.OnState(state.BoneRotations, (state.Flags & PoseFlags.HasHips) != 0,
                new Vector3(state.HipsX, state.HipsY, state.HipsZ));
            _aiming = (state.Flags & PoseFlags.Aiming) != 0;
            _audio.Tick(state);

            int outfit = state.ModelState | ((state.Flags & PoseFlags.WearHat) != 0 ? 0x100 : 0);
            if (outfit != _lastOutfit)
            {
                ApplyOutfit(state.ModelState, (outfit & 0x100) != 0);
                _lastOutfit = outfit;
            }
        }

        /// <summary>Reliable edge cues (AvatarOneShot): sounds now, weapon FX on the next LateTick.</summary>
        public void ApplyOneShot(AvatarCue cues)
        {
            if (LastDead) cues &= ~AvatarCue.Hurt; // already down: no hurt cue
            if (cues == AvatarCue.None) return;
            _pendingFx |= cues;
            _audio.OnCues(_lastWeapon, cues);
        }

        /// <summary>Every frame at the delayed render time: root, body pose, then weapon FX at the posed hand.</summary>
        public void LateTick(float renderTime)
        {
            Motion.LateTick(renderTime);
            Pose.LateTick(renderTime);
            _audio.LateTick();
            _weapon.Tick(_aiming, _pendingFx, GameObject.transform.position, Pose.AimDirection);
            _pendingFx = AvatarCue.None;
        }

        /// <summary>
        /// The sender's outfit: what native CharacterModelType.ApplyType does for the local Elster (one model object per
        /// ElsterType active, hat by wearHat), applied to the clone's objects.
        /// </summary>
        private void ApplyOutfit(byte modelState, bool wearHat)
        {
            var outfits = _rig.Outfits;
            for (int i = 0; i < outfits.Length; i++)
            {
                if (outfits[i] != null) outfits[i].SetActive(i == modelState);
            }
            if (_rig.Hat != null) _rig.Hat.SetActive(wearHat);
        }
    }
}
