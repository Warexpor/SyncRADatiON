using SyncRADation.Networking;
using UnityEngine;

namespace SyncRADation.Players
{
    public sealed class RemotePlayerProxy
    {
        public static RemotePlayerProxy Instance { get; private set; }

        public GameObject GameObject { get; }
        public RemoteAnimatorDriver AnimDriver { get; }

        public Vector3 LastVelocity { get; private set; }
        public bool LastAiming { get; private set; }
        public bool LastShooting { get; private set; }
        public bool LastRunning { get; private set; }
        public byte LastFacing { get; private set; }
        public byte LastCharState { get; private set; }

        public RemotePlayerProxy(GameObject go)
        {
            GameObject = go;
            AnimDriver = new RemoteAnimatorDriver(go);
            AnimDriver.Initialize(go);
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
            LastVelocity = new Vector3(state.VelX, 0, state.VelZ);
            LastAiming = state.Aiming;
            LastShooting = state.Shooting;
            LastRunning = state.Running;
            LastFacing = state.Facing;
            LastCharState = state.CharState;

            float rawSpeed = LastVelocity.magnitude;
            float speed;
            if (LastRunning)
                speed = 1.0f;
            else if (rawSpeed > 0.5f)
                speed = 0.4f;
            else
                speed = 0f;
            AnimDriver.ApplyState(speed, LastAiming, LastShooting, LastRunning, LastFacing, LastCharState, state.RotY);
        }
    }
}
