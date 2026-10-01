// World StudioEventEmitter Play/Stop by WorldId, plus the host relay of world one-shots.
// Local-only sounds never leave the peer: Elster, radio UI, doors (DoorNative plays those distance-gated),
// Music/Cutscenes/Ambience/UI beds, cinematics.
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

        /// <summary>Static relay decision for an emitter (the dynamic Cinematic test is repeated per Play).</summary>
        enum Route : byte { Unknown, Send, Skip }

        /// <summary>
        /// Everything known about one emitter key in the current scene. The wire state lives as long as the scene;
        /// the local binding (emitter + static checks) is dropped whenever WorldRegistry rebuilds, because WorldId
        /// includes the sibling index and a rebuild is where ids are settled again.
        /// </summary>
        sealed class EmitterInfo
        {
            public readonly EmitKey Key;
            public EmitterInfo(EmitKey key) { Key = key; }

            // Local binding.
            public StudioEventEmitter Emitter;
            public Route HostRoute;      // IsLocalOnlyStatic / door / bed
            public Route ClientRoute;    // HostRoute + enemy/boss emitters (host-authored, never client-sent)
            public bool? UnderDoor;      // a Doorway_Double / EventSlidingDoor above the emitter
            public float MissUntil;      // wire key with no local emitter: no rescan for it before this

            // Wire state.
            public bool Known;           // a Play/Stop for this key went out / was relayed
            public bool Playing;         // ...and the last one was a Play
            public int PlayFrame = -1;   // same-frame duplicate Play guard (prefix/postfix re-entry), not a time window
            public float HostPlayAt = -999f, HostStopAt = -999f;

            public bool WasPlaying => Known && Playing;

            public void Sent(bool play)
            {
                Known = true;
                Playing = play;
            }

            public void Unbind()
            {
                Emitter = null;
                HostRoute = Route.Unknown;
                ClientRoute = Route.Unknown;
                UnderDoor = null;
                MissUntil = 0f;
            }
        }

        struct Bucket
        {
            public float Tokens;
            public float At;
        }

        // ------------------------------------------------------------------ per-scene table (one invalidation point: Sync)

        static readonly Dictionary<EmitKey, EmitterInfo> _byKey = new Dictionary<EmitKey, EmitterInfo>(256);
        // Local Unity instance id -> record (never sent): WorldId + GetComponents run once per emitter.
        static readonly Dictionary<int, EmitterInfo> _byInst = new Dictionary<int, EmitterInfo>(256);
        static int _generation = -1;
        static string _scene;
        // Peers can name keys this scene never had: bound the table instead of growing it per request.
        const int MaxRecords = 8192;

        // Sliding-door open/close SFX paths (two IL2CPP strings per door: read once per generation, not per Play).
        static readonly Dictionary<string, EventSlidingDoor> _slidingSfx = new Dictionary<string, EventSlidingDoor>();
        static bool _slidingSfxReady;
        // Client: the local player's room and its floor-plan rectangle (XY of the room's mesh renderers), for the
        // room gate on relayed one-shots that only carry a position.
        static Room _boundsRoom;
        static Rect _roomRect;
        static bool _roomRectOk;

        // A key with no local emitter must not FindObjectsOfType the scene on every message: a burst of unknown keys
        // shares one rescan, and the full rescan runs at most once per window.
        static float _lastRebuildAt = -999f;
        const float RebuildMinInterval = 1f;
        const float MissTtl = 3f;
        static float _lastInvalidateAt = -999f;
        const float InvalidateMinInterval = 5f;

        // ------------------------------------------------------------------ session state

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
        // (HandleRequest relays it to everyone except the sender itself).
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

        /// <summary>
        /// The one invalidation point. A registry rebuild drops every local binding (keeps the wire state); a new scene
        /// drops the whole table.
        /// </summary>
        static void Sync()
        {
            int g = WorldRegistry.Generation;
            if (g == _generation) return;
            _generation = g;
            string scene = WorldRegistry.SceneName ?? "";
            if (scene != _scene)
            {
                _scene = scene;
                _byKey.Clear();
            }
            else
            {
                foreach (var kvp in _byKey) kvp.Value.Unbind();
            }
            _byInst.Clear();
            _slidingSfx.Clear();
            _slidingSfxReady = false;
            _boundsRoom = null;
            _roomRectOk = false;
            _lastRebuildAt = -999f;
            _lastInvalidateAt = -999f;
        }

        public static void Reset()
        {
            _byKey.Clear();
            _byInst.Clear();
            _slidingSfx.Clear();
            _slidingSfxReady = false;
            _boundsRoom = null;
            _roomRectOk = false;
            _generation = -1;
            _scene = null;
            _lastRebuildAt = -999f;
            _lastInvalidateAt = -999f;
            _hostBuckets.Clear();
            _clientBucket = default(Bucket);
            _suppressEmit = 0;
            _sceneStartAt = Time.unscaledTime;
        }

        /// <summary>Peer-named keys past the cap: start the scene table over (wire state included).</summary>
        static void TrimIfHuge()
        {
            if (_byKey.Count < MaxRecords && _byInst.Count < MaxRecords) return;
            PlaytestLog.Warn("FMOD", "emitter table over " + MaxRecords + " keys=" + _byKey.Count + " inst=" + _byInst.Count + ": cleared");
            _byKey.Clear();
            _byInst.Clear();
        }

        static EmitterInfo Record(EmitKey key)
        {
            EmitterInfo info;
            if (!_byKey.TryGetValue(key, out info))
            {
                info = new EmitterInfo(key);
                _byKey[key] = info;
            }
            return info;
        }

        /// <summary>Record of a live emitter (null when it has no WorldId). Binds the emitter when its key had none.</summary>
        static EmitterInfo InfoOf(StudioEventEmitter e)
        {
            Sync();
            int iid = e.GetInstanceID();
            EmitterInfo info;
            if (_byInst.TryGetValue(iid, out info)) return info;
            ulong id = WorldId.FromGameObject(e.gameObject);
            if (id == 0) return null;
            info = Record(new EmitKey(id, CompIndexOf(e)));
            if (info.Emitter == null) info.Emitter = e;
            _byInst[iid] = info;
            return info;
        }

        static byte CompIndexOf(StudioEventEmitter e)
        {
            try
            {
                var all = e.GetComponents<StudioEventEmitter>();
                if (all != null)
                {
                    for (int i = 0; i < all.Length && i < 255; i++)
                    {
                        if (all[i] == e) return (byte)i;
                    }
                }
            }
            catch (Exception ex) { Guard.Swallow(ex); }
            return 0;
        }

        /// <summary>Local emitter for a key from the wire (rescans the scene on a real miss, throttled).</summary>
        static StudioEventEmitter Resolve(EmitterInfo info)
        {
            if (info.Emitter != null) return info.Emitter;
            float now = Time.unscaledTime;
            if (now < info.MissUntil) return null;
            if (now - _lastRebuildAt < RebuildMinInterval) return null;
            // Rescanning without a fresh scene scan cannot find anything new: remember the miss and wait for the window.
            if (now - _lastInvalidateAt < InvalidateMinInterval)
            {
                info.MissUntil = now + MissTtl;
                return null;
            }
            // The cached scan predates emitters instantiated since the scene loaded.
            Rescan();
            if (info.Emitter != null) return info.Emitter;
            info.MissUntil = now + MissTtl;
            return null;
        }

        /// <summary>Fresh StudioEventEmitter scan; every record is re-bound (first emitter per key wins). Misses are kept.</summary>
        static void Rescan()
        {
            float now = Time.unscaledTime;
            _lastRebuildAt = now;
            _lastInvalidateAt = now;
            WorldLookup.Invalidate<StudioEventEmitter>();
            _byInst.Clear();
            foreach (var kvp in _byKey) kvp.Value.Emitter = null;
            var all = WorldLookup.All<StudioEventEmitter>();
            if (all == null) return;
            for (int i = 0; i < all.Length; i++)
            {
                var e = all[i];
                if (e != null) InfoOf(e);
            }
        }

        // ------------------------------------------------------------------ relay checks

        static string PathOf(StudioEventEmitter e)
        {
            try { return e.Event ?? ""; }
            catch (Exception ex) { Guard.Swallow(ex); return ""; }
        }

        static bool Cinematic(StudioEventEmitter e)
        {
            try { return LocalInspect.Cinematic(e.gameObject); }
            catch (Exception ex) { Guard.Swallow(ex); return false; }
        }

        static bool HostSkips(EmitterInfo info, StudioEventEmitter e)
        {
            if (info.HostRoute == Route.Unknown)
            {
                string path = PathOf(e);
                bool skip = IsLocalOnlyStatic(e.transform) || IsDoorSfxPath(path) || DoorAbove(info, e) || IsSceneBed(path);
                info.HostRoute = skip ? Route.Skip : Route.Send;
            }
            return info.HostRoute == Route.Skip;
        }

        static bool ClientSkips(EmitterInfo info, StudioEventEmitter e)
        {
            if (info.ClientRoute == Route.Unknown)
                info.ClientRoute = HostSkips(info, e) || UnderHostDrivenActor(e) ? Route.Skip : Route.Send;
            return info.ClientRoute == Route.Skip;
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

        static bool DoorAbove(EmitterInfo info, StudioEventEmitter e)
        {
            if (info == null) return DoorInHierarchy(e.transform);
            if (!info.UnderDoor.HasValue) info.UnderDoor = DoorInHierarchy(e.transform);
            return info.UnderDoor.Value;
        }

        static bool DoorInHierarchy(Transform t)
        {
            int hops = 0;
            while (t != null && hops++ < 16)
            {
                try
                {
                    if (t.GetComponent<Doorway_Double>() != null) return true;
                    if (t.GetComponent<EventSlidingDoor>() != null) return true;
                }
                catch (Exception e) { Guard.Swallow(e); }
                t = t.parent;
            }
            return false;
        }

        /// <summary>Door emitter (door SFX path or under a door): never world-relayed, DoorNative distance-gates it.</summary>
        public static bool IsDoorEmitter(StudioEventEmitter emitter)
        {
            if (emitter == null) return false;
            if (IsDoorSfxPath(PathOf(emitter))) return true;
            return DoorAbove(InfoOf(emitter), emitter);
        }

        // ------------------------------------------------------------------ emitters

        /// <summary>Client: a host Play/Stop (Kind 0) or world one-shot (Kind 1).</summary>
        public static void Handle(FmodEmitterMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (NetGate.HostRole) return;
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
                TrimIfHuge();
                Sync();
                var info = Record(new EmitKey(id, msg.Comp));
                var e = Resolve(info);
                if (e == null)
                {
                    PlaytestLog.Miss("FMOD", "StudioEventEmitter", id);
                    return;
                }
                string path = PathOf(e);
                if (IsDoorSfxPath(path) || DoorAbove(info, e) || IsSceneBed(path)) return;
                if (ModRuntime.VerboseLogging)
                    PlaytestLog.Verbose("FMOD", (msg.Play ? "Play" : "Stop")
                        + (path.Length == 0 ? "" : " " + path)
                        + " id=" + id.ToString("X16") + "/" + msg.Comp);
                // DoorNative already distance-gates; a non-door emitter Play() can still leak far one-shots /
                // 2D-ish events: skip far Play, always allow Stop.
                if (msg.Play)
                {
                    if (!NearHere(e))
                    {
                        if (ModRuntime.VerboseLogging)
                            PlaytestLog.Verbose("FMOD", "skip far Play id=" + id.ToString("X16"));
                        return;
                    }
                    e.Play();
                }
                else e.Stop();
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.Warning("[FMOD] apply: " + ex.Message);
            }
            finally
            {
                NetGate.EndApply();
            }
        }

        static bool NearHere(StudioEventEmitter e)
        {
            try
            {
                float vol;
                return e.transform == null || WorldSfx.TryVolume(e.transform.position, out vol);
            }
            catch (Exception ex) { Guard.Swallow(ex); return true; }
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
            var info = InfoOf(emitter);
            if (info == null) return;
            // Repeat Stop is redundant. Repeat Play is not: one-shot emitters must replay on clients.
            if (!play && !info.WasPlaying) return;
            if (play)
            {
                int frame = Time.frameCount;
                if (info.PlayFrame == frame) return;
                info.PlayFrame = frame;
            }
            // Cinematic is the one dynamic part of the check (airlock / event camera starts).
            if (HostSkips(info, emitter) || Cinematic(emitter))
            {
                info.Sent(play);
                return;
            }
            info.Sent(play);
            if (play) info.HostPlayAt = Time.unscaledTime;
            else info.HostStopAt = Time.unscaledTime;
            if (ModRuntime.VerboseLogging)
            {
                string path = PathOf(emitter);
                PlaytestLog.Verbose("FMOD", (play ? "Play" : "Stop")
                    + (path.Length == 0 ? "" : " " + path)
                    + " id=" + info.Key.Id.ToString("X16") + "/" + info.Key.Comp);
            }
            var outMsg = new FmodEmitterMessage
            {
                WorldId = unchecked((long)info.Key.Id),
                Play = play,
                Kind = 0,
                Comp = info.Key.Comp
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
        /// Same skip rules as the host path plus: nothing under enemies/bosses (host-authored), nothing while
        /// loading/settling, nothing far from the player.
        /// </summary>
        static void ClientEmit(StudioEventEmitter emitter, bool play)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected || !net.HandshakeComplete) return;
            if (net.SceneMismatch || SceneFollowService.LocalIsTransient()) return;
            if (Time.unscaledTime - _sceneStartAt < ClientSettleSeconds) return;
            var info = InfoOf(emitter);
            if (info == null) return;
            if (!play && !info.WasPlaying) return; // Stop only for something this client started
            if (info.ClientRoute == Route.Skip) return;
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
                int frame = Time.frameCount;
                if (info.PlayFrame == frame) return;
                info.PlayFrame = frame;
            }
            if (ClientSkips(info, emitter) || Cinematic(emitter)) return;
            if (!TakeToken(ref _clientBucket, ClientRatePerSec, ClientBurst, Time.unscaledTime)) return;
            info.Sent(play);
            if (ModRuntime.VerboseLogging)
            {
                string path = PathOf(emitter);
                PlaytestLog.Verbose("FMOD", "client " + (play ? "Play" : "Stop")
                    + (path.Length == 0 ? "" : " " + path)
                    + " id=" + info.Key.Id.ToString("X16") + "/" + info.Key.Comp);
            }
            net.FmodHandlers.SendFmodEmitterRequest(new FmodEmitterRequestMessage
            {
                WorldId = unchecked((long)info.Key.Id),
                Play = play,
                Comp = info.Key.Comp
            });
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

            TrimIfHuge();
            Sync();
            var info = Record(new EmitKey(id, req.Comp));
            if (!req.Play && !info.WasPlaying) return;
            if (req.Play)
            {
                if (now - info.HostPlayAt < HostDupWindow) return; // host just authored it
                // A client Play that crossed the host's Stop on the wire must not restart the loop for everyone.
                if (now - info.HostStopAt < HostDupWindow) return;
                int frame = Time.frameCount;
                if (info.PlayFrame == frame) return;
                info.PlayFrame = frame;
            }

            var e = Resolve(info);
            if (e != null)
            {
                if (ClientSkips(info, e) || Cinematic(e)) return;
                // Already audible on the host (loops / machine hum the host runs itself): do not restart it, but
                // the other clients have not heard it yet, so the relay below still goes out.
                bool playing = false;
                if (req.Play)
                {
                    try { playing = e.IsPlaying(); } catch (Exception ex) { Guard.Swallow(ex); }
                }
                bool near = !req.Play || NearHere(e);
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

            info.Sent(req.Play);
            if (req.Play) info.HostPlayAt = now;
            net.FmodHandlers.SendFmodEmitterExcept(new FmodEmitterMessage
            {
                WorldId = req.WorldId,
                Play = req.Play,
                Kind = 0,
                Comp = req.Comp
            }, senderId);
        }

        /// <summary>Host join/resync dump: every relayable emitter that is playing right now.</summary>
        public static void DumpPlaying()
        {
            var net = LanNetworkManager.Instance;
            if (!NetGate.Host) return;
            Sync();
            Rescan();
            foreach (var kvp in _byKey)
            {
                var info = kvp.Value;
                var e = info.Emitter;
                if (e == null) continue;
                if (HostSkips(info, e) || Cinematic(e)) continue;
                bool playing = false;
                try { playing = e.IsPlaying(); } catch (Exception ex) { Guard.Swallow(ex); }
                if (!playing) continue;
                // Recorded even inside the unicast dump: it only lets the later Stop go out (to everyone).
                info.Sent(true);
                net.SendFmodEmitter(new FmodEmitterMessage
                {
                    WorldId = unchecked((long)info.Key.Id),
                    Play = true,
                    Kind = 0,
                    Comp = info.Key.Comp
                });
            }
        }

        // ------------------------------------------------------------------ one-shots

        static void HostOneShot(string path, Vector3 pos)
        {
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
            catch (Exception e) { Guard.Swallow(e); }
            try { return guid.ToString(); }
            catch { return ""; }
        }

        /// <summary>Host relay for world one-shots (string / Guid / attached / fmod helper).</summary>
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

        // ------------------------------------------------------------------ local playback gates

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
            catch (Exception e) { Guard.Swallow(e); return true; }
        }

        const float RoomRectSlack = 1f;
        const float RoomNearAlways = 8f;

        /// <summary>
        /// Client gate for a relayed one-shot (position only, no emitter): inside the local player's room when that
        /// room's floor plan is known, else the door distance. The plan is the XY union of the room's active mesh
        /// renderers (its chunk geometry), measured once per room and registry generation.
        /// </summary>
        static bool AudibleAt(Vector3 pos)
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
                    Sync();
                    if (_boundsRoom == null || _boundsRoom != here)
                    {
                        _boundsRoom = here;
                        _roomRectOk = TryRoomRect(here, out _roomRect);
                    }
                    if (_roomRectOk)
                        return _roomRect.Contains(new Vector2(pos.x, pos.y));
                }
                float vol;
                return WorldSfx.TryVolume(pos, out vol);
            }
            catch (Exception e) { Guard.Swallow(e); return true; }
        }

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
            catch (Exception e) { Guard.Swallow(e); return false; }
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

        // ------------------------------------------------------------------ path / hierarchy classification

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

        static bool IsLocalOneShot(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            if (path.StartsWith("event:/Elster/")) return true;
            if (path.StartsWith("event:/UI/")) return true;
            return false;
        }

        /// <summary>The static part of "this emitter belongs to this peer only" (Cinematic is checked per Play).</summary>
        static bool IsLocalOnlyStatic(Transform t)
        {
            if (t == null) return true;
            try
            {
                var player = PlayerState.player;
                if (player != null && (t == player.transform || t.IsChildOf(player.transform)))
                    return true;
            }
            catch (Exception e) { Guard.Swallow(e); }
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
            catch (Exception e) { Guard.Swallow(e); }
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
                catch (Exception e) { Guard.Swallow(e); }
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
            catch (Exception e) { Guard.Swallow(e); }
            return false;
        }

        static bool TitleEmitter(StudioEventEmitter e, Transform t)
        {
            try { return e != null && t != null && e.transform == t; }
            catch { return false; }
        }

        /// <summary>Door open/close one-shots: never world-relayed, DoorNative distance-gates them.</summary>
        public static bool IsDoorSfxPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (path.StartsWith("event:/Environment/Doors/")) return true;
            return IsSlidingDoorSfxPath(path);
        }

        static bool IsSlidingDoorSfxPath(string path)
        {
            Sync();
            if (!_slidingSfxReady)
            {
                _slidingSfxReady = true;
                try
                {
                    foreach (var kvp in WorldRegistry.AllSlidingDoors())
                    {
                        var sd = kvp.Value;
                        if (sd == null) continue;
                        string open = sd.openSFX, close = sd.closeSFX;
                        // First registered door wins.
                        if (!string.IsNullOrEmpty(open) && !_slidingSfx.ContainsKey(open)) _slidingSfx[open] = sd;
                        if (!string.IsNullOrEmpty(close) && !_slidingSfx.ContainsKey(close)) _slidingSfx[close] = sd;
                    }
                }
                catch (Exception e) { Guard.Swallow(e); }
            }
            EventSlidingDoor door;
            return _slidingSfx.TryGetValue(path, out door) && door != null; // null: destroyed since the rebuild
        }
    }
}
