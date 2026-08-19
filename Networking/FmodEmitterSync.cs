// World StudioEventEmitter Play/Stop by WorldId. Skip Elster + radio UI.
using FMODUnity;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    public static class FmodEmitterSync
    {
        static readonly System.Collections.Generic.Dictionary<ulong, bool> _sentPlaying
            = new System.Collections.Generic.Dictionary<ulong, bool>();
        static readonly System.Collections.Generic.Dictionary<ulong, StudioEventEmitter> _byId
            = new System.Collections.Generic.Dictionary<ulong, StudioEventEmitter>();

        static StudioEventEmitter FindCached(ulong id)
        {
            StudioEventEmitter e;
            if (_byId.TryGetValue(id, out e) && e != null)
                return e;
            RebuildCache();
            _byId.TryGetValue(id, out e);
            return e;
        }

        static void RebuildCache()
        {
            _byId.Clear();
            var all = WorldLookup.All<StudioEventEmitter>();
            if (all == null) return;
            for (int i = 0; i < all.Length; i++)
            {
                var e = all[i];
                if (e == null) continue;
                ulong id = WorldId.FromGameObject(e.gameObject);
                if (id == 0 || _byId.ContainsKey(id)) continue;
                _byId[id] = e;
            }
        }

        public static void Handle(FmodEmitterMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (net != null && net.Role == NetworkRole.Host) return;
            if (SceneFollowService.LocalIsTransient()) return;

            NetGate.BeginApply();
            try
            {
                if (msg.Kind == 1)
                {
                    PlaytestLog.Verbose("FMOD", "apply OneShot " + msg.Path);
                    WorldSfx.Play(msg.Path, new Vector3(msg.PosX, msg.PosY, msg.PosZ));
                    return;
                }

                ulong id = unchecked((ulong)msg.WorldId);
                if (id == 0) return;
                var e = FindCached(id);
                if (e == null)
                {
                    PlaytestLog.Miss("FMOD", "StudioEventEmitter", id);
                    return;
                }
                if (IsDoorEmitter(e)) return;
                PlaytestLog.Event("FMOD", (msg.Play ? "Play" : "Stop") + " id=" + id.ToString("X16"));
                if (msg.Play) e.Play();
                else e.Stop();
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
            if (IsDoorEmitter(emitter)) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            ulong id = WorldId.FromGameObject(emitter.gameObject);
            bool was;
            if (_sentPlaying.TryGetValue(id, out was) && was == play) return;
            if (!play && !was) return;
            _sentPlaying[id] = play;
            PlaytestLog.Event("FMOD", (play ? "host Play" : "host Stop") + " id=" + id.ToString("X16"));
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
            if (IsLocalOneShot(path)) return;
            if (IsSlidingDoorSfxPath(path)) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            PlaytestLog.Event("FMOD", "host OneShot " + path);
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

        public static bool IsLocalOneShot(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            if (path.StartsWith("event:/Elster/")) return true;
            if (path.StartsWith("event:/UI/")) return true;
            return false;
        }

        public static bool IsLocalOnly(Transform t)
        {
            if (t == null) return true;
            try
            {
                if (LocalInspect.Cinematic(t.gameObject)) return true;
            }
            catch { }
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
            Transform p = t;
            int hops = 0;
            while (p != null && hops++ < 16)
            {
                try
                {
                    if (p.GetComponent<PEN_Titles>() != null) return true;
                    if (p.GetComponent<PEN_Airlock>() != null) return true;
                    if (p.GetComponent<PenroseAirlock>() != null) return true;
                    if (p.GetComponent<PenroseAirlockNew>() != null) return true;
                    if (p.GetComponent<EventOnlyRoom>() != null) return true;
                    if (p.GetComponent<EventScreen>() != null) return true;
                    if (p.GetComponent<EventScreen3DCam>() != null) return true;
                }
                catch { }
                p = p.parent;
            }
            try
            {
                var titles = Object.FindObjectsOfType<PEN_Titles>();
                if (titles != null)
                {
                    for (int i = 0; i < titles.Length; i++)
                    {
                        var title = titles[i];
                        if (title == null) continue;
                        if (TitleEmitter(title.PCSound, t) || TitleEmitter(title.SuitSceneSound, t)
                            || TitleEmitter(title.DoorSound, t) || TitleEmitter(title.LiftSound, t)
                            || TitleEmitter(title.HatchSound, t))
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }

        static bool TitleEmitter(StudioEventEmitter e, Transform t)
        {
            try { return e != null && t != null && e.transform == t; }
            catch { return false; }
        }

        public static void Reset()
        {
            _sentPlaying.Clear();
            _byId.Clear();
        }

        public static void DumpPlaying()
        {
            var net = LanNetworkManager.Instance;
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected) return;
            RebuildCache();
            foreach (var kvp in _byId)
            {
                var e = kvp.Value;
                if (e == null || kvp.Key == 0) continue;
                if (IsLocalOnly(e.transform) || IsDoorEmitter(e)) continue;
                bool playing = false;
                try { playing = e.IsPlaying(); } catch { }
                if (!playing) continue;
                _sentPlaying[kvp.Key] = true;
                net.SendFmodEmitter(new FmodEmitterMessage
                {
                    WorldId = unchecked((long)kvp.Key),
                    Play = true,
                    Kind = 0
                });
            }
        }

        public static bool IsDoorEmitter(StudioEventEmitter emitter)
        {
            Transform t = emitter != null ? emitter.transform : null;
            int hops = 0;
            while (t != null && hops++ < 16)
            {
                try
                {
                    if (t.GetComponent<Doorway_Double>() != null) return true;
                    if (t.GetComponent<EventSlidingDoor>() != null) return true;
                }
                catch { }
                t = t.parent;
            }
            return false;
        }

        public static bool IsSlidingDoorSfxPath(string path)
        {
            EventSlidingDoor sd;
            return TryGetSlidingDoorForSfx(path, out sd);
        }

        public static bool TryGetSlidingDoorForSfx(string path, out EventSlidingDoor door)
        {
            door = null;
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                foreach (var kvp in WorldRegistry.AllSlidingDoors())
                {
                    var sd = kvp.Value;
                    if (sd == null) continue;
                    if (sd.openSFX == path || sd.closeSFX == path)
                    {
                        door = sd;
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }
    }
}
