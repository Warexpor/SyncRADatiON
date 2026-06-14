using SyncRADation.Networking;
using UnityEngine;

namespace SyncRADation.Players
{
    public sealed class RemotePlayerProxy
    {
        public static RemotePlayerProxy Instance { get; private set; }

        public GameObject GameObject { get; }
        public RemoteAnimatorDriver AnimDriver { get; }
        public ProxyAudioSync AudioSync { get; }

        public RemotePlayerProxy(GameObject go)
        {
            GameObject = go;
            AnimDriver = new RemoteAnimatorDriver(go);
            AnimDriver.Initialize(go);
            AudioSync = new ProxyAudioSync(go);
            ModRuntime.RegisterAnimDriver(AnimDriver);
            Instance = this;
            var arms = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var armList = new System.Collections.Generic.List<string>();
            for (int i = 0; i < arms.Length; i++)
            {
                if (arms[i] != null && arms[i].rootBone != null && arms[i].rootBone.parent != null)
                    armList.Add(arms[i].rootBone.parent.name);
            }
            ModRuntime.Log?.Msg("[DRV] Init: " + arms.Length + " SMRs, " + armList.Count + " armatures: " + string.Join(", ", armList) + " | rootY=" + go.transform.eulerAngles.y.ToString("F1"));
        }

        public void Destroy()
        {
            ModRuntime.UnregisterAnimDriver(AnimDriver);
            Instance = null;
        }

        public void ApplyState(PlayerStateMessage state)
        {
            AnimDriver.ApplyState(state);
            AudioSync.Tick(state, state.AnimBools, state.AnimTriggers);
        }
    }
}