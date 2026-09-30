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
        static readonly System.Collections.Generic.Dictionary<ulong, float> _lastPlayAt
            = new System.Collections.Generic.Dictionary<ulong, float>();
        static readonly System.Collections.Generic.HashSet<ulong> _skipIds
            = new System.Collections.Generic.HashSet<ulong>();
        static readonly System.Collections.Generic.Dictionary<ulong, StudioEventEmitter> _byId
            = new System.Collections.Generic.Dictionary<ulong, StudioEventEmitter>();

        // Misses are remembered so a Play/Stop for an emitter that is not in this scene
        // (or a cold cache) does not FindObjectsOfType the whole scene on every message.
        static readonly System.Collections.Generic.Dictionary<ulong, float> _missUntil
            = new System.Collections.Generic.Dictionary<ulong, float>();
        static float _lastRebuildAt = -999f;
        const float RebuildMinInterval = 1f;
        const float MissTtl = 3f;

        static StudioEventEmitter FindCached(ulong id)
        {
            StudioEventEmitter e;
            if (_byId.TryGetValue(id, out e) && e != null)
                return e;
            float now = Time.unscaledTime;
            float until;
            if (_missUntil.TryGetValue(id, out until) && now < until)
                return null;
            // Throttled: a burst of unknown ids shares one rebuild.
            if (now - _lastRebuildAt < RebuildMinInterval)
                return null;
            RebuildCache();
            if (_byId.TryGetValue(id, out e) && e != null)
                return e;
            _missUntil[id] = now + MissTtl;
            return null;
        }

        static void RebuildCache()
        {
            _byId.Clear();
            _missUntil.Clear();
            _lastRebuildAt = Time.unscaledTime;
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
            if (net != null && net.SceneMismatch) return;
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
                string path = "";
                try { path = e.Event; } catch { }
                if (IsDoorSfxPath(path)) return;
                if (IsSceneBed(path)) return;
                PlaytestLog.Verbose("FMOD", (msg.Play ? "Play" : "Stop")
                    + (string.IsNullOrEmpty(path) ? "" : " " + path)
                    + " id=" + id.ToString("X16"));
                // DoorNative already distance-gates; non-door emitter Play() can still
                // leak far one-shots / 2D-ish events — skip far Play, always allow Stop.
                if (msg.Play)
                {
                    try
                    {
                        float vol;
                        if (e.transform != null
                            && !WorldSfx.TryVolume(e.transform.position, out vol))
                        {
                            PlaytestLog.Verbose("FMOD", "skip far Play id=" + id.ToString("X16"));
                            return;
                        }
                    }
                    catch { }
                    e.Play();
                }
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
            // On the host, IsApplying is only ever a client-originated apply (puzzle/door/interaction).
            // Those world sounds must still fan out to every client, so the gate is not checked here.
            if (emitter == null || !NetGate.Host) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            ulong id = WorldId.FromGameObject(emitter.gameObject);
            if (id == 0) return;
            bool was;
            bool known = _sentPlaying.TryGetValue(id, out was);
            // Repeat Stop is redundant. Repeat Play is not: one-shot emitters must replay on clients.
            if (!play && !(known && was)) return;
            float now = Time.unscaledTime;
            if (play)
            {
                float last;
                // Same-frame duplicate Play (prefix/postfix re-entry) only.
                if (_lastPlayAt.TryGetValue(id, out last) && now - last < 0.05f) return;
                _lastPlayAt[id] = now;
                // Local/door/bed emitters stay local: skip the hierarchy walk on repeat Play.
                if (known && was && _skipIds.Contains(id)) return;
            }
            string path = "";
            try { path = emitter.Event; } catch { }
            if (IsLocalOnly(emitter.transform) || IsDoorEmitter(emitter) || IsSceneBed(path))
            {
                _skipIds.Add(id);
                _sentPlaying[id] = play;
                return;
            }
            _skipIds.Remove(id);
            _sentPlaying[id] = play;
            PlaytestLog.Verbose("FMOD", (play ? "Play" : "Stop")
                + (string.IsNullOrEmpty(path) ? "" : " " + path)
                + " id=" + id.ToString("X16"));
            net.SendFmodEmitter(new FmodEmitterMessage
            {
                WorldId = unchecked((long)id),
                Play = play,
                Kind = 0
            });
        }

        public static void HostOneShot(string path, Vector3 pos)
        {
            if (string.IsNullOrEmpty(path) || !NetGate.Host) return;
            if (IsLocalOneShot(path)) return;
            if (IsSceneBed(path)) return;
            if (IsDoorSfxPath(path)) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            PlaytestLog.Verbose("FMOD", "OneShot " + path);
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

        /// <summary>Resolve FMOD event path from Guid via StudioSystem.lookupPath.</summary>
        public static string PathFromGuid(Il2CppSystem.Guid guid)
        {
            try
            {
                string path;
                if (RuntimeManager.StudioSystem.lookupPath(guid, out path) == FMOD.RESULT.OK
                    && !string.IsNullOrEmpty(path))
                    return path;
            }
            catch { }
            try { return guid.ToString(); }
            catch { return ""; }
        }

        public static string PathFromGuid(System.Guid guid)
        {
            try
            {
                var ig = new Il2CppSystem.Guid(guid.ToByteArray());
                return PathFromGuid(ig);
            }
            catch
            {
                return guid.ToString("N");
            }
        }

        /// <summary>Shared host relay for world one-shots (string / Guid / Attached).</summary>
        public static void TryHostWorldOneShot(string path, Vector3 position)
        {
            if (!NetGate.Host) return;
            if (string.IsNullOrEmpty(path)) return;
            if (IsLocalOneShot(path)) return;
            if (IsDoorSfxPath(path)) return;
            // Host's own nearby sounds stay local; a host-applied client action is relayed even next to the host.
            var player = PlayerState.player;
            if (!NetGate.IsApplying && player != null
                && (player.transform.position - position).sqrMagnitude < 4f)
                return;
            HostOneShot(path, position);
        }

        public static bool ShouldBlockDoorOneShot(string path)
        {
            return NetGate.IsApplying && IsDoorSfxPath(path);
        }

        public static bool IsSceneBed(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (path.StartsWith("event:/Music/")) return true;
            if (path.StartsWith("event:/Cutscenes/")) return true;
            if (path.StartsWith("event:/Ambience/")) return true;
            return false;
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
                var rms = WorldLookup.All<RadioManager>();
                if (rms != null)
                {
                    for (int i = 0; i < rms.Length; i++)
                    {
                        var rm = rms[i];
                        if (rm == null) continue;
                        if (rm.switchFX != null && t == rm.switchFX.transform) return true;
                        if (rm.fallBackStatic != null && t == rm.fallBackStatic.transform) return true;
                    }
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
                var titles = WorldLookup.All<PEN_Titles>();
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
            _lastPlayAt.Clear();
            _skipIds.Clear();
            _byId.Clear();
            _missUntil.Clear();
            _lastRebuildAt = -999f;
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
                string path = "";
                try { path = e.Event; } catch { }
                if (IsSceneBed(path)) continue;
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
            if (emitter == null) return false;
            try
            {
                string path = emitter.Event;
                if (IsDoorSfxPath(path)) return true;
            }
            catch { }
            Transform t = emitter.transform;
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

        /// <summary>Door open/close one-shots — never world-relay; DoorNative distance-gates them.</summary>
        public static bool IsDoorSfxPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (path.StartsWith("event:/Environment/Doors/")) return true;
            return IsSlidingDoorSfxPath(path);
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
