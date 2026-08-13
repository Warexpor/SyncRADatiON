// World StudioEventEmitter Play/Stop by WorldId. Skip Elster + radio UI.
using FMODUnity;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    public static class FmodEmitterSync
    {
        public static void Handle(FmodEmitterMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (net != null && net.Role == NetworkRole.Host) return;

            NetGate.BeginApply();
            try
            {
                if (msg.Kind == 1)
                {
                    PlaytestLog.Verbose("FMOD", "apply OneShot " + msg.Path);
                    if (!string.IsNullOrEmpty(msg.Path))
                        RuntimeManager.PlayOneShot(msg.Path, new Vector3(msg.PosX, msg.PosY, msg.PosZ));
                    return;
                }

                ulong id = unchecked((ulong)msg.WorldId);
                if (id == 0) return;
                var all = Object.FindObjectsOfType<StudioEventEmitter>();
                if (all == null) return;
                for (int i = 0; i < all.Length; i++)
                {
                    var e = all[i];
                    if (e == null) continue;
                    if (WorldId.FromGameObject(e.gameObject) != id) continue;
                    PlaytestLog.Event("FMOD", (msg.Play ? "Play" : "Stop") + " id=" + id.ToString("X16"));
                    if (msg.Play) e.Play();
                    else e.Stop();
                    return;
                }
                PlaytestLog.Miss("FMOD", "StudioEventEmitter", id);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[FMOD] apply: " + ex.Message);
            }
            finally
            {
                NetGate.EndApply();
            }
        }

        public static void HostEmit(StudioEventEmitter emitter, bool play)
        {
            if (emitter == null || !NetGate.Host || NetGate.IsApplying) return;
            if (IsLocalOnly(emitter.transform)) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            ulong id = WorldId.FromGameObject(emitter.gameObject);
            PlaytestLog.Verbose("FMOD", (play ? "host Play" : "host Stop") + " id=" + id.ToString("X16"));
            net.SendFmodEmitter(new FmodEmitterMessage
            {
                WorldId = unchecked((long)id),
                Play = play,
                Kind = 0
            });
        }

        public static void HostOneShot(string path, Vector3 pos)
        {
            if (string.IsNullOrEmpty(path) || !NetGate.Host || NetGate.IsApplying) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            PlaytestLog.Verbose("FMOD", "host OneShot " + path);
            net.SendFmodEmitter(new FmodEmitterMessage
            {
                Play = true,
                Kind = 1,
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                Path = path
            });
        }

        public static bool IsLocalOnly(Transform t)
        {
            if (t == null) return true;
            try
            {
                var player = PlayerState.player;
                if (player != null && (t == player.transform || t.IsChildOf(player.transform)))
                    return true;
            }
            catch { }
            try
            {
                var rm = Object.FindObjectOfType<RadioManager>();
                if (rm != null)
                {
                    if (rm.switchFX != null && t == rm.switchFX.transform) return true;
                    if (rm.fallBackStatic != null && t == rm.fallBackStatic.transform) return true;
                }
            }
            catch { }
            return false;
        }
    }
}
