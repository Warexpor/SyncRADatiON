// SyncRADation — wrapper per remote player: owns AnimDriver, AudioSync, WeaponSync
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

        public void ApplyState(PlayerStateMessage state)
        {
            AnimDriver.ApplyState(state);
            AudioSync.Tick(state, state.AnimBools, state.AnimTriggers);
            WeaponSync.Tick(state, state.AnimBools, state.AnimTriggers, GameObject.transform.position, state.RotY);
            if (state.Weapon != _lastWeapon)
            {
                ModRuntime.Log?.Msg("[WeaponSync] Proxy " + PlayerId + " weapon: " + _lastWeapon + " -> " + state.Weapon);
                WeaponSync.ApplyWeapon(state.Weapon);
                _lastWeapon = state.Weapon;
            }
        }
    }
}
