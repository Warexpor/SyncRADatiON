// World StudioEventEmitter Play/Stop by WorldId. Skip Elster + radio UI.
using System;
using System.Collections.Generic;
using FMODUnity;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    public static class FmodEmitterSync
    {
        /// <summary>
        /// One StudioEventEmitter: WorldId of its GameObject + its index among the StudioEventEmitters on that
        /// object (serialized order, identical on every peer). Two emitters on one object are two keys.
        /// </summary>
        readonly struct EmitKey : IEquatable<EmitKey>
        {
            public readonly ulong Id;
            public readonly byte Comp;
            public EmitKey(ulong id, byte comp) { Id = id; Comp = comp; }
            public bool Equals(EmitKey o) => Id == o.Id && Comp == o.Comp;
            public override bool Equals(object obj) => obj is EmitKey && Equals((EmitKey)obj);
            public override int GetHashCode() => unchecked((int)(Id ^ (Id >> 32)) * 31 + Comp);
        }

        struct Bucket
        {
            public float Tokens;
            public float At;
        }

        static readonly Dictionary<EmitKey, bool> _sentPlaying = new Dictionary<EmitKey, bool>();
        // Same-frame duplicate guard (Time.frameCount), not a wall-clock window: a one-shot emitter replayed on
        // consecutive frames still goes out.
        static readonly Dictionary<EmitKey, int> _lastPlayFrame = new Dictionary<EmitKey, int>();
        static readonly HashSet<EmitKey> _skipIds = new HashSet<EmitKey>();
        static readonly Dictionary<EmitKey, StudioEventEmitter> _byId = new Dictionary<EmitKey, StudioEventEmitter>();
        // Host: when a Play for a key last went out, so a client request for the same sound is not doubled.
        static readonly Dictionary<EmitKey, float> _lastHostPlayAt = new Dictionary<EmitKey, float>();

        // Local-only caches (Unity instance id is never sent): WorldId hashing and GetComponents are not free.
        static readonly Dictionary<int, ulong> _idCache = new Dictionary<int, ulong>();
        static readonly Dictionary<int, byte> _compCache = new Dictionary<int, byte>();

        // Misses are remembered so a Play/Stop for an emitter that is not in this scene
        // (or a cold cache) does not FindObjectsOfType the whole scene on every message.
        static readonly Dictionary<EmitKey, float> _missUntil = new Dictionary<EmitKey, float>();
        static float _lastRebuildAt = -999f;
        const float RebuildMinInterval = 1f;
        const float MissTtl = 3f;

        // Client-originated world sound (client -> host -> everyone but the sender).
        static float _sceneStartAt;
        const float ClientSettleSeconds = 4f;     // native start-up Play() storms on scene load are not player actions
        const float ClientNearMeters = 12f;       // something the player just did sounds near the player
        static Bucket _clientBucket;
        const float ClientRatePerSec = 30f, ClientBurst = 20f;
        static readonly Dictionary<int, Bucket> _hostBuckets = new Dictionary<int, Bucket>();
        const float HostRatePerSec = 60f, HostBurst = 40f;
        const float HostDupWindow = 0.35f;
        // >0 while the host plays a client's request locally: the Play postfix must not re-broadcast it
        // (the handler relays it to everyone except the sender itself).
        static int _suppressEmit;

        static bool TakeToken(ref Bucket b, float rate, float burst, float now)
        {
            if (b.At == 0f) { b.Tokens = burst; b.At = now; }
            b.Tokens = Mathf.Min(burst, b.Tokens + (now - b.At) * rate);
            b.At = now;
            if (b.Tokens < 1f) return false;
            b.Tokens -= 1f;
            return true;
        }

        static ulong IdOf(StudioEventEmitter e)
        {
            int iid = e.GetInstanceID();
            ulong id;
            if (_idCache.TryGetValue(iid, out id)) return id;
            id = WorldId.FromGameObject(e.gameObject);
            if (_idCache.Count > 4096) _idCache.Clear();
            _idCache[iid] = id;
            return id;
        }

        static byte CompIndex(StudioEventEmitter e)
        {
            int iid = e.GetInstanceID();
            byte idx;
            if (_compCache.TryGetValue(iid, out idx)) return idx;
            idx = 0;
            try
            {
                var all = e.GetComponents<StudioEventEmitter>();
                if (all != null)
                {
                    for (int i = 0; i < all.Length && i < 255; i++)
                    {
                        if (all[i] == e) { idx = (byte)i; break; }
                    }
                }
            }
            catch (Exception ex) { Guard.Swallow(ex); }
            if (_compCache.Count > 4096) _compCache.Clear();
            _compCache[iid] = idx;
            return idx;
        }

        static StudioEventEmitter FindCached(EmitKey key)
        {
            StudioEventEmitter e;
            if (_byId.TryGetValue(key, out e) && e != null)
                return e;
            float now = Time.unscaledTime;
            float until;
            if (_missUntil.TryGetValue(key, out until) && now < until)
                return null;
            // Throttled: a burst of unknown ids shares one rebuild.
            if (now - _lastRebuildAt < RebuildMinInterval)
                return null;
            // The cached scan predates emitters instantiated since the scene loaded: rescan on a real miss.
            WorldLookup.Invalidate<StudioEventEmitter>();
            RebuildCache();
            if (_byId.TryGetValue(key, out e) && e != null)
                return e;
            _missUntil[key] = now + MissTtl;
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
                ulong id = IdOf(e);
                if (id == 0) continue;
                var key = new EmitKey(id, CompIndex(e));
                if (_byId.ContainsKey(key)) continue;
                _byId[key] = e;
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
                    if (ModRuntime.VerboseLogging)
                        PlaytestLog.Verbose("FMOD", "apply OneShot " + msg.Path);
                    WorldSfx.Play(msg.Path, new Vector3(msg.PosX, msg.PosY, msg.PosZ));
                    return;
                }

                ulong id = unchecked((ulong)msg.WorldId);
                if (id == 0) return;
                var e = FindCached(new EmitKey(id, msg.Comp));
                if (e == null)
                {
                    PlaytestLog.Miss("FMOD", "StudioEventEmitter", id);
                    return;
                }
                if (IsDoorEmitter(e)) return;
                string path = "";
                try { path = e.Event; } catch (System.Exception ex) { Guard.Swallow(ex); }
                if (IsDoorSfxPath(path)) return;
                if (IsSceneBed(path)) return;
                if (ModRuntime.VerboseLogging)
                    PlaytestLog.Verbose("FMOD", (msg.Play ? "Play" : "Stop")
                        + (string.IsNullOrEmpty(path) ? "" : " " + path)
                        + " id=" + id.ToString("X16") + "/" + msg.Comp);
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
                            if (ModRuntime.VerboseLogging)
                                PlaytestLog.Verbose("FMOD", "skip far Play id=" + id.ToString("X16"));
                            return;
                        }
                    }
                    catch (System.Exception ex) { Guard.Swallow(ex); }
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

        /// <summary>StudioEventEmitter.Play/Stop postfix: host fans out, a client forwards its own world sounds to the host.</summary>
        public static void EmitterChanged(StudioEventEmitter emitter, bool play)
        {
            if (emitter == null) return;
            if (NetGate.Host) HostEmit(emitter, play);
            else if (NetGate.Client && !NetGate.IsApplying) ClientEmit(emitter, play);
        }

        static void HostEmit(StudioEventEmitter emitter, bool play)
        {
            // On the host, IsApplying is only ever a client-originated apply (puzzle/door/interaction).
            // Those world sounds must still fan out to every client, so the gate is not checked here.
            if (_suppressEmit > 0) return; // HandleRequest relays it (except the sender)
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            ulong id = IdOf(emitter);
            if (id == 0) return;
            var key = new EmitKey(id, CompIndex(emitter));
            bool was;
            bool known = _sentPlaying.TryGetValue(key, out was);
            // Repeat Stop is redundant. Repeat Play is not: one-shot emitters must replay on clients.
            if (!play && !(known && was)) return;
            if (play)
            {
                int last;
                // Same-frame duplicate Play (prefix/postfix re-entry) only.
                int frame = Time.frameCount;
                if (_lastPlayFrame.TryGetValue(key, out last) && last == frame) return;
                _lastPlayFrame[key] = frame;
                // Local/door/bed emitters stay local: skip the hierarchy walk on repeat Play.
                if (known && was && _skipIds.Contains(key)) return;
            }
            string path = "";
            try { path = emitter.Event; } catch (System.Exception e) { Guard.Swallow(e); }
            if (IsLocalOnly(emitter.transform) || IsDoorEmitter(emitter) || IsSceneBed(path))
            {
                _skipIds.Add(key);
                _sentPlaying[key] = play;
                return;
            }
            _skipIds.Remove(key);
            _sentPlaying[key] = play;
            if (play) _lastHostPlayAt[key] = Time.unscaledTime;
            if (ModRuntime.VerboseLogging)
                PlaytestLog.Verbose("FMOD", (play ? "Play" : "Stop")
                    + (string.IsNullOrEmpty(path) ? "" : " " + path)
                    + " id=" + id.ToString("X16") + "/" + key.Comp);
            net.SendFmodEmitter(new FmodEmitterMessage
            {
                WorldId = unchecked((long)id),
                Play = play,
                Kind = 0,
                Comp = key.Comp
            });
        }

        /// <summary>
        /// Client: a world emitter started/stopped by something the client did locally (switch, prop, machine) and
        /// that the host did not author. Sent to the host, which plays it and relays it to the other clients.
        /// Same skip rules as the host path (Elster, radio UI, door, Music/Cutscenes/Ambience beds) plus: nothing
        /// near enemies/bosses (host-authored), nothing while loading/settling, nothing far from the player.
        /// </summary>
        static void ClientEmit(StudioEventEmitter emitter, bool play)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected || !net.HandshakeComplete) return;
            if (net.SceneMismatch || SceneFollowService.LocalIsTransient()) return;
            if (Time.unscaledTime - _sceneStartAt < ClientSettleSeconds) return;
            ulong id = IdOf(emitter);
            if (id == 0) return;
            var key = new EmitKey(id, CompIndex(emitter));
            bool was;
            bool known = _sentPlaying.TryGetValue(key, out was);
            if (!play && !(known && was)) return; // Stop only for something this client started
            if (_skipIds.Contains(key)) return;
            if (play)
            {
                var player = PlayerState.player;
                if (player == null) return;
                try
                {
                    if ((player.transform.position - emitter.transform.position).sqrMagnitude
                        > ClientNearMeters * ClientNearMeters)
                        return;
                }
                catch (Exception ex) { Guard.Swallow(ex); return; }
                int last;
                int frame = Time.frameCount;
                if (_lastPlayFrame.TryGetValue(key, out last) && last == frame) return;
                _lastPlayFrame[key] = frame;
            }
            string path = "";
            try { path = emitter.Event; } catch (Exception e) { Guard.Swallow(e); }
            if (IsClientLocalOnly(emitter, path))
            {
                _skipIds.Add(key);
                return;
            }
            if (!TakeToken(ref _clientBucket, ClientRatePerSec, ClientBurst, Time.unscaledTime)) return;
            _sentPlaying[key] = play;
            if (ModRuntime.VerboseLogging)
                PlaytestLog.Verbose("FMOD", "client " + (play ? "Play" : "Stop")
                    + (string.IsNullOrEmpty(path) ? "" : " " + path)
                    + " id=" + id.ToString("X16") + "/" + key.Comp);
            net.FmodHandlers.SendFmodEmitterRequest(new FmodEmitterRequestMessage
            {
                WorldId = unchecked((long)id),
                Play = play,
                Comp = key.Comp
            });
        }

        static bool IsClientLocalOnly(StudioEventEmitter emitter, string path)
        {
            if (IsLocalOnly(emitter.transform) || IsDoorEmitter(emitter) || IsSceneBed(path)) return true;
            return UnderHostDrivenActor(emitter);
        }

        /// <summary>Enemy / boss sounds are host-authored (snapshots + native host logic): a client copy would double them.</summary>
        static bool UnderHostDrivenActor(StudioEventEmitter emitter)
        {
            try
            {
                if (emitter.GetComponentInParent<EnemyController>() != null) return true;
                if (emitter.GetComponentInParent<END_Boss>() != null) return true;
                if (emitter.GetComponentInParent<BOS_Adler>() != null) return true;
                if (emitter.GetComponentInParent<LAB_ChimeraBoss>() != null) return true;
                if (emitter.GetComponentInParent<MED_MynahBoss>() != null) return true;
            }
            catch (Exception ex) { Guard.Swallow(ex); }
            return false;
        }

        /// <summary>
        /// Host: a client triggered a world emitter. Validate, play it here (distance-gated like any relayed
        /// emitter) and relay it to every other client. Relayed even when the emitter is not in the host's scene.
        /// </summary>
        public static void HandleRequest(FmodEmitterRequestMessage req, int senderId)
        {
            if (!NetGate.Host) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            ulong id = unchecked((ulong)req.WorldId);
            if (id == 0) return;
            float now = Time.unscaledTime;
            Bucket b;
            _hostBuckets.TryGetValue(senderId, out b);
            bool ok = TakeToken(ref b, HostRatePerSec, HostBurst, now);
            _hostBuckets[senderId] = b;
            if (!ok) return;

            var key = new EmitKey(id, req.Comp);
            bool was;
            bool known = _sentPlaying.TryGetValue(key, out was);
            if (!req.Play && !(known && was)) return;
            if (req.Play)
            {
                float at;
                if (_lastHostPlayAt.TryGetValue(key, out at) && now - at < HostDupWindow) return; // host just authored it
                int frame = Time.frameCount;
                int last;
                if (_lastPlayFrame.TryGetValue(key, out last) && last == frame) return;
                _lastPlayFrame[key] = frame;
            }

            var e = FindCached(key);
            if (e != null)
            {
                string path = "";
                try { path = e.Event; } catch (Exception ex) { Guard.Swallow(ex); }
                if (IsClientLocalOnly(e, path)) return;
                bool playing = false;
                if (req.Play)
                {
                    try { playing = e.IsPlaying(); } catch (Exception ex) { Guard.Swallow(ex); }
                    if (playing) return; // already audible on the host: loops / machine hum the host runs itself
                }
                bool near = true;
                if (req.Play)
                {
                    try
                    {
                        float vol;
                        near = e.transform == null || WorldSfx.TryVolume(e.transform.position, out vol);
                    }
                    catch (Exception ex) { Guard.Swallow(ex); }
                }
                if (near)
                {
                    _suppressEmit++;
                    NetGate.BeginApply();
                    try
                    {
                        if (req.Play) e.Play();
                        else e.Stop();
                    }
                    catch (Exception ex) { Guard.Swallow(ex); }
                    finally
                    {
                        NetGate.EndApply();
                        _suppressEmit--;
                    }
                }
            }

            _sentPlaying[key] = req.Play;
            if (req.Play) _lastHostPlayAt[key] = now;
            net.FmodHandlers.SendFmodEmitterExcept(new FmodEmitterMessage
            {
                WorldId = req.WorldId,
                Play = req.Play,
                Kind = 0,
                Comp = req.Comp
            }, senderId);
        }

        public static void HostOneShot(string path, Vector3 pos)
        {
            if (string.IsNullOrEmpty(path) || !NetGate.Host) return;
            if (IsLocalOneShot(path)) return;
            if (IsSceneBed(path)) return;
            if (IsDoorSfxPath(path)) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            if (ModRuntime.VerboseLogging)
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
            catch (System.Exception e) { Guard.Swallow(e); }
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
            catch (System.Exception e) { Guard.Swallow(e); }
            try
            {
                var player = PlayerState.player;
                if (player != null && (t == player.transform || t.IsChildOf(player.transform)))
                    return true;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
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
            catch (System.Exception e) { Guard.Swallow(e); }
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
                catch (System.Exception e) { Guard.Swallow(e); }
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
            catch (System.Exception e) { Guard.Swallow(e); }
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
            _lastPlayFrame.Clear();
            _lastHostPlayAt.Clear();
            _skipIds.Clear();
            _byId.Clear();
            _missUntil.Clear();
            _idCache.Clear();
            _compCache.Clear();
            _hostBuckets.Clear();
            _clientBucket = default(Bucket);
            _suppressEmit = 0;
            _lastRebuildAt = -999f;
            _sceneStartAt = Time.unscaledTime;
        }

        public static void DumpPlaying()
        {
            var net = LanNetworkManager.Instance;
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected) return;
            WorldLookup.Invalidate<StudioEventEmitter>();
            RebuildCache();
            foreach (var kvp in _byId)
            {
                var e = kvp.Value;
                if (e == null || kvp.Key.Id == 0) continue;
                if (IsLocalOnly(e.transform) || IsDoorEmitter(e)) continue;
                string path = "";
                try { path = e.Event; } catch (System.Exception ex) { Guard.Swallow(ex); }
                if (IsSceneBed(path)) continue;
                bool playing = false;
                try { playing = e.IsPlaying(); } catch (System.Exception ex) { Guard.Swallow(ex); }
                if (!playing) continue;
                _sentPlaying[kvp.Key] = true;
                net.SendFmodEmitter(new FmodEmitterMessage
                {
                    WorldId = unchecked((long)kvp.Key.Id),
                    Play = true,
                    Kind = 0,
                    Comp = kvp.Key.Comp
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
            catch (System.Exception e) { Guard.Swallow(e); }
            Transform t = emitter.transform;
            int hops = 0;
            while (t != null && hops++ < 16)
            {
                try
                {
                    if (t.GetComponent<Doorway_Double>() != null) return true;
                    if (t.GetComponent<EventSlidingDoor>() != null) return true;
                }
                catch (System.Exception e) { Guard.Swallow(e); }
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
            catch (System.Exception e) { Guard.Swallow(e); }
            return false;
        }
    }
}
