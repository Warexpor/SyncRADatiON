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
        // Client: keys that already passed the (hierarchy-walking) local-only check. Only the cheap dynamic
        // Cinematic test is repeated per Play. Cleared in Reset and when WorldRegistry rebuilds.
        static readonly HashSet<EmitKey> _okKeys = new HashSet<EmitKey>();
        // Host: same idea for the host's own relay check (IsLocalOnly / door / bed); a separate set because the
        // client check is stricter (enemy/boss emitters are host-authored and never client-sent).
        static readonly HashSet<EmitKey> _hostOkKeys = new HashSet<EmitKey>();
        // Sliding-door open/close SFX paths of this registry generation (IsSlidingDoorSfxPath read two IL2CPP
        // strings per sliding door on every Play / one-shot).
        static readonly Dictionary<string, EventSlidingDoor> _slidingSfx = new Dictionary<string, EventSlidingDoor>();
        static int _slidingSfxGeneration = -1;
        // Client: the local player's room and its floor-plan rectangle (XY of the room's mesh renderers), for the
        // room gate on relayed one-shots that only carry a position.
        static Room _boundsRoom;
        static Rect _roomRect;
        static bool _roomRectOk;
        static int _boundsGeneration = -1;
        // Host: when a Stop for a key last went out, so a client Play that crossed it on the wire does not restart the loop.
        static readonly Dictionary<EmitKey, float> _lastHostStopAt = new Dictionary<EmitKey, float>();
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
        // The full FindObjectsOfType rescan (Invalidate) is the expensive part: at most once per window. A persistent
        // unknown id (client-only emitter, runtime clone) must not turn every request into a scene scan.
        static float _lastInvalidateAt = -999f;
        const float InvalidateMinInterval = 5f;
        // WorldRegistry.Generation the id caches were built under (WorldId includes the sibling index, which drifts
        // when objects are added or destroyed; a registry rebuild is the point where the ids are recomputed).
        static int _idGeneration = -1;

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

        static void CheckGeneration()
        {
            int g = WorldRegistry.Generation;
            if (g == _idGeneration) return;
            _idGeneration = g;
            _idCache.Clear();
            _compCache.Clear();
            _byId.Clear();
            _missUntil.Clear();
            _skipIds.Clear();
            _okKeys.Clear();
            _hostOkKeys.Clear();
            // The scene registry was rebuilt (everything was invalidated): the next miss may scan right away.
            _lastRebuildAt = -999f;
            _lastInvalidateAt = -999f;
        }

        static ulong IdOf(StudioEventEmitter e)
        {
            CheckGeneration();
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
            CheckGeneration();
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
            CheckGeneration();
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
            // Rescanning without a fresh scan cannot find anything new: remember the miss and wait for the window.
            if (now - _lastInvalidateAt < InvalidateMinInterval)
            {
                if (_missUntil.Count > 2048) _missUntil.Clear();
                _missUntil[key] = now + MissTtl;
                return null;
            }
            // The cached scan predates emitters instantiated since the scene loaded: rescan on a real miss.
            _lastInvalidateAt = now;
            WorldLookup.Invalidate<StudioEventEmitter>();
            RebuildCache();
            if (_byId.TryGetValue(key, out e) && e != null)
                return e;
            _missUntil[key] = now + MissTtl;
            return null;
        }

        static void RebuildCache()
        {
            // _missUntil is kept: clearing it let every persistent unknown id force a rescan once per window.
            // The sibling-index based ids and the component index may have moved: recompute them.
            _byId.Clear();
            _idCache.Clear();
            _compCache.Clear();
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
                    var at = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                    // Same room gate as relayed emitters (AudibleHere): a one-shot from the next room is not heard
                    // through the wall just because it is within the distance falloff.
                    if (!AudibleAt(at))
                    {
                        if (ModRuntime.VerboseLogging)
                            PlaytestLog.Verbose("FMOD", "skip OneShot (other room) " + msg.Path);
                        return;
                    }
                    if (ModRuntime.VerboseLogging)
                        PlaytestLog.Verbose("FMOD", "apply OneShot " + msg.Path);
                    WorldSfx.Play(msg.Path, at);
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
            if (PuzzleFx.Active) return; // a peer's puzzle press replayed here: every peer replays it itself
            if (NetGate.Host) { if (NetGate.Party) HostEmit(emitter, play); }
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
            if (_hostOkKeys.Contains(key))
            {
                // Static checks passed before; Cinematic is the one dynamic part (airlock / event camera starts).
                bool cinematic = false;
                try { cinematic = LocalInspect.Cinematic(emitter.gameObject); } catch (System.Exception e) { Guard.Swallow(e); }
                if (cinematic)
                {
                    _sentPlaying[key] = play;
                    return;
                }
                if (ModRuntime.VerboseLogging)
                {
                    try { path = emitter.Event; } catch (System.Exception e) { Guard.Swallow(e); }
                }
            }
            else
            {
                try { path = emitter.Event; } catch (System.Exception e) { Guard.Swallow(e); }
                if (IsLocalOnly(emitter.transform) || IsDoorEmitter(emitter) || IsSceneBed(path))
                {
                    _skipIds.Add(key);
                    _sentPlaying[key] = play;
                    return;
                }
                if (_hostOkKeys.Count > 4096) _hostOkKeys.Clear();
                _hostOkKeys.Add(key);
            }
            _skipIds.Remove(key);
            _sentPlaying[key] = play;
            if (play) _lastHostPlayAt[key] = Time.unscaledTime;
            else _lastHostStopAt[key] = Time.unscaledTime;
            if (ModRuntime.VerboseLogging)
                PlaytestLog.Verbose("FMOD", (play ? "Play" : "Stop")
                    + (string.IsNullOrEmpty(path) ? "" : " " + path)
                    + " id=" + id.ToString("X16") + "/" + key.Comp);
            var outMsg = new FmodEmitterMessage
            {
                WorldId = unchecked((long)id),
                Play = play,
                Kind = 0,
                Comp = key.Comp
            };
            // A sound produced by applying a client's packet (its puzzle solve's onSolved) already played natively
            // on that client: relay to everyone else only, or it hears it twice.
            if (NetGate.IsApplying && NetGate.ApplySender >= 1)
                net.FmodHandlers.SendFmodEmitterExcept(outMsg, NetGate.ApplySender);
            else
                net.SendFmodEmitter(outMsg);
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
            if (_okKeys.Contains(key))
            {
                // Static checks passed before; Cinematic is the one dynamic part (airlock / event camera starts).
                try { if (LocalInspect.Cinematic(emitter.gameObject)) return; }
                catch (Exception e) { Guard.Swallow(e); }
                if (ModRuntime.VerboseLogging)
                {
                    try { path = emitter.Event; } catch (Exception e) { Guard.Swallow(e); }
                }
            }
            else
            {
                try { path = emitter.Event; } catch (Exception e) { Guard.Swallow(e); }
                if (IsClientLocalOnly(emitter, path))
                {
                    _skipIds.Add(key);
                    return;
                }
                if (_okKeys.Count > 4096) _okKeys.Clear();
                _okKeys.Add(key);
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
                // A client Play that crossed the host's Stop on the wire must not restart the loop for everyone.
                if (_lastHostStopAt.TryGetValue(key, out at) && now - at < HostDupWindow) return;
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
                    // Already audible on the host (loops / machine hum the host runs itself): do not restart it, but
                    // the other clients have not heard it yet, so the relay below still goes out.
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
                if (near && !playing)
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
            var msg = new FmodEmitterMessage
            {
                Play = true,
                Kind = 1,
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                Path = path
            };
            // Same rule as HostEmit: a one-shot produced by applying a client's packet already played natively on
            // that client, so it goes to everyone else only.
            if (NetGate.IsApplying && NetGate.ApplySender >= 1)
                net.FmodHandlers.SendFmodEmitterExcept(msg, NetGate.ApplySender);
            else
                net.SendFmodEmitter(msg);
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
            if (!NetGate.Host || !NetGate.Party) return; // a lone host has nobody to tell
            if (string.IsNullOrEmpty(path)) return;
            // A peer's puzzle press replayed here: every peer replays it from the puzzle state itself.
            if (PuzzleFx.Active) return;
            if (IsLocalOneShot(path)) return;
            if (IsDoorSfxPath(path)) return;
            // Host's own nearby sounds stay local; a host-applied client action is relayed even next to the host.
            var player = PlayerState.player;
            if (!NetGate.IsApplying && player != null
                && (player.transform.position - position).sqrMagnitude < 4f)
                return;
            HostOneShot(path, position);
        }

        /// <summary>
        /// Local playback gate for a sound a remote action triggers here: a source inside a Room plays only when
        /// that Room is the local player's current room (event-screen stages sit far from their room and play
        /// 2D, so distance alone lets them through); a source outside any Room falls back to the door distance.
        /// </summary>
        public static bool AudibleHere(GameObject go)
        {
            if (go == null) return true;
            try
            {
                Room room = null;
                Transform t = go.transform;
                while (t != null && room == null)
                {
                    room = t.GetComponent<Room>();
                    t = t.parent;
                }
                var here = PlayerState.currentRoom;
                if (room != null && here != null) return room == here;
                float vol;
                return WorldSfx.TryVolume(go.transform.position, out vol);
            }
            catch (System.Exception e) { Guard.Swallow(e); return true; }
        }

        /// <summary>
        /// Client gate for a relayed one-shot (position only, no emitter): inside the local player's room when that
        /// room's floor plan is known, else the door distance. The plan is the XY union of the room's active mesh
        /// renderers (its chunk geometry), measured once per room and registry generation.
        /// </summary>
        public static bool AudibleAt(Vector3 pos)
        {
            try
            {
                var player = PlayerState.player;
                if (player != null)
                {
                    // Right next to the player is audible whatever the floor plan says (doorways, rect edges).
                    Vector2 d = (Vector2)(player.transform.position - pos);
                    if (d.sqrMagnitude < RoomNearAlways * RoomNearAlways) return true;
                }
                var here = PlayerState.currentRoom;
                if (here != null)
                {
                    int g = WorldRegistry.Generation;
                    if (g != _boundsGeneration || _boundsRoom == null || _boundsRoom != here)
                    {
                        _boundsGeneration = g;
                        _boundsRoom = here;
                        _roomRectOk = TryRoomRect(here, out _roomRect);
                    }
                    if (_roomRectOk)
                        return _roomRect.Contains(new Vector2(pos.x, pos.y));
                }
                float vol;
                return WorldSfx.TryVolume(pos, out vol);
            }
            catch (System.Exception e) { Guard.Swallow(e); return true; }
        }

        const float RoomRectSlack = 1f;
        const float RoomNearAlways = 8f;

        static bool TryRoomRect(Room room, out Rect rect)
        {
            rect = default(Rect);
            bool any = false;
            float minX = 0f, minY = 0f, maxX = 0f, maxY = 0f;
            try
            {
                AddRenderers(room.gameObject, ref any, ref minX, ref minY, ref maxX, ref maxY);
                // The tile floor can live outside the Room's own hierarchy.
                var tiles = room.Tilesystem;
                if (tiles != null && !tiles.transform.IsChildOf(room.transform))
                    AddRenderers(tiles, ref any, ref minX, ref minY, ref maxX, ref maxY);
            }
            catch (System.Exception e) { Guard.Swallow(e); return false; }
            if (!any) return false;
            rect = Rect.MinMaxRect(minX - RoomRectSlack, minY - RoomRectSlack, maxX + RoomRectSlack, maxY + RoomRectSlack);
            return true;
        }

        static void AddRenderers(GameObject root, ref bool any, ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            var rends = root.GetComponentsInChildren<MeshRenderer>(false);
            if (rends == null) return;
            for (int i = 0; i < rends.Length; i++)
            {
                var r = rends[i];
                if (r == null) continue;
                var b = r.bounds;
                var mn = b.min;
                var mx = b.max;
                if (!any)
                {
                    minX = mn.x; minY = mn.y; maxX = mx.x; maxY = mx.y;
                    any = true;
                    continue;
                }
                if (mn.x < minX) minX = mn.x;
                if (mn.y < minY) minY = mn.y;
                if (mx.x > maxX) maxX = mx.x;
                if (mx.y > maxY) maxY = mx.y;
            }
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
            // UI emitters (dialogue text blips, menus) belong to whoever has the UI open.
            if (path.StartsWith("event:/UI/")) return true;
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
                    // Tarot klick: native Update plays it on every peer from the synced darkmode edge.
                    if (p.GetComponent<ROT_Tarot>() != null) return true;
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
            _lastHostStopAt.Clear();
            _skipIds.Clear();
            _okKeys.Clear();
            _hostOkKeys.Clear();
            _slidingSfx.Clear();
            _slidingSfxGeneration = -1;
            _boundsRoom = null;
            _roomRectOk = false;
            _boundsGeneration = -1;
            _byId.Clear();
            _missUntil.Clear();
            _idCache.Clear();
            _compCache.Clear();
            _hostBuckets.Clear();
            _clientBucket = default(Bucket);
            _suppressEmit = 0;
            _lastRebuildAt = -999f;
            _lastInvalidateAt = -999f;
            _idGeneration = -1;
            _sceneStartAt = Time.unscaledTime;
        }

        public static void DumpPlaying()
        {
            var net = LanNetworkManager.Instance;
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected) return;
            _lastInvalidateAt = Time.unscaledTime;
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
            int g = WorldRegistry.Generation;
            if (g != _slidingSfxGeneration)
            {
                _slidingSfxGeneration = g;
                _slidingSfx.Clear();
                try
                {
                    foreach (var kvp in WorldRegistry.AllSlidingDoors())
                    {
                        var sd = kvp.Value;
                        if (sd == null) continue;
                        string open = sd.openSFX, close = sd.closeSFX;
                        // First registered door wins, as the old registry walk did.
                        if (!string.IsNullOrEmpty(open) && !_slidingSfx.ContainsKey(open)) _slidingSfx[open] = sd;
                        if (!string.IsNullOrEmpty(close) && !_slidingSfx.ContainsKey(close)) _slidingSfx[close] = sd;
                    }
                }
                catch (System.Exception e) { Guard.Swallow(e); }
            }
            if (!_slidingSfx.TryGetValue(path, out door)) return false;
            if (door != null) return true;
            door = null; // destroyed since the rebuild
            return false;
        }
    }
}
