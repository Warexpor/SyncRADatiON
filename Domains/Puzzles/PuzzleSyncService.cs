// Host puzzle/interactive sync keyed by WorldId (never FindObjectsOfType index).
using System;
using System.Collections.Generic;
using SyncRADation.Patches;
using SyncRADation.Sync;
using UnityEngine;
using static SyncRADation.Networking.PuzzleMerge;

namespace SyncRADation.Networking
{
    public sealed partial class PuzzleSyncService : IPuzzleDomainHost
    {
        delegate bool PuzzleReader(PuzzleType type, Component c, long wid, out PuzzleStateEntry entry);
        delegate void PuzzleApplier(PuzzleSyncService self, PuzzleStateEntry e, bool cinematic);
        delegate void PuzzleScanner(PuzzleSyncService self);

        private float _sendTimer;
        private const float SendInterval = 0.5f;
        private float _lastFullSend = -999f;
        private const float MinFullSendInterval = 2f;
        private bool _scanned;
        private bool _needFullSend = true;

        /// <summary>
        /// Host is applying a packet from a peer. DET_RadioCodeLock keeps the host's
        /// generated frequency/code/hint; peers still copy those ints.
        /// </summary>
        internal static bool ApplyingPeerPacket;

        // Type → (WorldId → component)
        private readonly Dictionary<PuzzleType, Dictionary<ulong, Component>> _maps
            = new Dictionary<PuzzleType, Dictionary<ulong, Component>>();

        // Allocation-free (type, WorldId) key. Replaces the per-entry string that Key() used to build.
        internal readonly struct PKey : IEquatable<PKey>
        {
            public readonly long Id;
            public readonly byte Type;
            public PKey(byte type, long id) { Type = type; Id = id; }
            public bool Equals(PKey o) => Id == o.Id && Type == o.Type;
            public override bool Equals(object obj) => obj is PKey && Equals((PKey)obj);
            public override int GetHashCode() => unchecked((int)(Id ^ (Id >> 32)) * 31 + Type);
        }

        private readonly Dictionary<PKey, PuzzleStateEntry> _lastSent
            = new Dictionary<PKey, PuzzleStateEntry>();

        // Progressed solves survive inactive room chunks; re-applied on Room.EnterRoom.
        private readonly Dictionary<PKey, PuzzleStateEntry> _held
            = new Dictionary<PKey, PuzzleStateEntry>();
        private readonly List<PuzzleStateEntry> _reapplyScratch = new List<PuzzleStateEntry>(16);

        // Reused each Tick to avoid List allocs on the puzzle poll path.
        private readonly List<PuzzleStateEntry> _tickSeed = new List<PuzzleStateEntry>(64);
        private readonly List<PuzzleStateEntry> _tickLocal = new List<PuzzleStateEntry>(16);
        private readonly List<PuzzleStateEntry> _tickEntries = new List<PuzzleStateEntry>(64);
        private readonly PuzzleStateEntry[] _emitScratch = new PuzzleStateEntry[1];

        // _pendingReapply: re-snap every held entry. _reapplyRooms: only the rooms a chunk remount / room entry just
        // (re)mounted (entries under another, still-mounted room keep their live state: re-snapping those wrote a
        // held entry up to one poll old over the local player's own newer change).
        private bool _pendingReapply;
        private readonly List<Transform> _reapplyRooms = new List<Transform>(4);

        // Join/resync dump requested inside a unicast scope: sent as a read-only snapshot (see Tick).
        private bool _unicastFull;
        // Local emits that fired inside a unicast scope (would reach the joiner only): replayed on the next Tick.
        private readonly List<Action> _afterUnicast = new List<Action>(2);

        // An apply that could not finish natively (tarot take/place threw): re-applied on the next tick while the
        // entry is still the current state, a few times at most.
        const int MaxApplyRetries = 3;
        static readonly List<PuzzleStateEntry> _retry = new List<PuzzleStateEntry>(4);
        static readonly Dictionary<PKey, int> _retryCount = new Dictionary<PKey, int>();
        readonly List<PuzzleStateEntry> _retryScratch = new List<PuzzleStateEntry>(4);

        // Client seeding gate: the host is the only source of initial state. A client stays silent
        // (no seed, no diffs) until the host's full dump for this scene is applied, then snapshots
        // a baseline and only emits real local changes. Timeout covers scenes the host never dumps
        // (airlock split, SceneMismatch).
        private bool _hostDumpApplied;
        private bool _clientLive;
        private float _clientWaitSince = -1f;
        private int _dumpRetries;
        private const float ClientDumpWait = 6f;
        // Re-ask the host for the dump while the gate is still unarmed (request/response lost or rate-deferred).
        private const float ClientDumpRetryEvery = 2f;
        private const int ClientDumpMaxRetries = 2;

        // Per type+WorldId version. Host: current stamp (bumped per accepted change). Client: highest seen.
        private readonly Dictionary<PKey, int> _seq = new Dictionary<PKey, int>();
        // Host: who authored the current state of a key (0 = host, >=1 = that client). The last author's own
        // follow-up edit carries an old base Seq on purpose (non-merge entries are not echoed back to the sender),
        // so it is never a stale regress.
        private readonly Dictionary<PKey, int> _lastAuthor = new Dictionary<PKey, int>();
        private readonly List<PuzzleStateEntry> _resync = new List<PuzzleStateEntry>(4);

        // Client, before the gate arms: local edits are recorded here (First = first observed, Latest = last
        // observed) instead of being sent or swallowed by the baseline. When the gate arms, the real solves
        // among them are emitted once (see FlushPending).
        private struct PendingLocal
        {
            public PuzzleStateEntry First;
            public PuzzleStateEntry Latest;
            // Set by a Harmony-hook emit (an explicit local action), not by the 0.5 s observation poll: only those
            // may flush a float-only change (a polled Float0 can be a self-animating drift, not an edit).
            public bool Explicit;
        }
        private struct PendingFlush
        {
            public PuzzleStateEntry First;
            public PuzzleStateEntry Latest;
            public PuzzleStateEntry HostBase;
            public bool HaveHostBase;
            public bool Explicit;
        }
        private const int FloatMask = 128 | 256;
        private readonly Dictionary<PKey, PendingLocal> _pending = new Dictionary<PKey, PendingLocal>();
        private readonly List<PendingFlush> _pendingFlush = new List<PendingFlush>(8);
        private readonly List<PuzzleStateEntry> _pendingOut = new List<PuzzleStateEntry>(8);
        // Door-ish types that default to "unlocked": only hold them once a locked state has been observed,
        // otherwise a script relock after a scene reload is undone by the durable memory / held re-snap.
        private readonly HashSet<PKey> _sawLocked = new HashSet<PKey>();
        private readonly List<PuzzleStateEntry> _relayAll = new List<PuzzleStateEntry>(8);
        private readonly List<PuzzleStateEntry> _relayExcept = new List<PuzzleStateEntry>(8);
        private readonly List<PuzzleStateEntry> _applyScratch = new List<PuzzleStateEntry>(64);

        // Door / lock solves survive scene reloads within the session (WorldId already hashes the scene).
        // Seeded back into _held on RefreshScene so the solve re-snaps for everyone after a reload.
        private struct DurableMemory
        {
            public string Scene;
            public PuzzleStateEntry Entry;
        }
        private static readonly Dictionary<PKey, DurableMemory> _memory
            = new Dictionary<PKey, DurableMemory>();
        private string _sceneName = "";

        private static bool _liveEdge;
        /// <summary>True while applying a live (non-full-refresh, non-reapply) entry: the rising edge may run native consequences.</summary>
        internal static bool LiveEdge => _liveEdge;

        private static readonly HashSet<string> _warnedSites = new HashSet<string>();
        private static readonly HashSet<string> _worldAnimStarted = new HashSet<string>();
        // Join/resync dumps are a settled snapshot. Replaying native transitions
        // (EventZone.Invoke, openDoor, delayedOpen, slaveInteraction.enable)
        // wakes leftover inactive puzzles and unseals flavor doors the host never touched.
        private static bool _mutateWorld = true;

        internal static bool MutateWorld => _mutateWorld;

        // True while ReapplyHeld re-snaps held entries (never a live event). MutateWorld stays true there on purpose
        // (UseItem NoteRemoteUnlock / unlock latch, RevealPickups need it); the replay-prone consumers (FlipSwitch.Flip,
        // pin / ring / reaktor visual refresh, UseItem onSuccessful) read ReplayWorld instead.
        private static bool _reapplying;
        /// <summary>MutateWorld for a live apply, false during a join dump or a held re-snap.</summary>
        internal static bool ReplayWorld => _mutateWorld && !_reapplying;

        readonly CryoSyncService _cryo;
        readonly CodepadSyncService _codepad;

        static readonly Dictionary<PuzzleType, PuzzleReader> Readers;
        static readonly Dictionary<PuzzleType, PuzzleApplier> Appliers;
        static readonly HashSet<PuzzleType> ClientEmitTypes;
        static readonly HashSet<PuzzleType> ProgressedBool0;
        static readonly PuzzleScanner[] Scanners;

        static PuzzleSyncService()
        {
            Readers = BuildReaders();
            Appliers = BuildAppliers();
            ClientEmitTypes = BuildClientEmitTypes();
            ProgressedBool0 = BuildProgressedBool0();
            Scanners = BuildScanners();
        }

        public PuzzleSyncService()
        {
            _cryo = new CryoSyncService(this);
            _codepad = new CodepadSyncService(this);
        }

        public void RefreshScene()
        {
            // No WorldLookup.Invalidate here: WorldRegistry.Rebuild (ModRuntime.OnSceneChanged) already invalidated
            // and rescanned for this load; dropping it again made every load scan the scene twice.
            try { SyncRADation.Patches.EnvEmit.ClearOnce(); } catch (Exception e) { Guard.Swallow(e); }
            try { SyncRADation.Patches.BiodomeLockPatch.Reset(); } catch (Exception e) { Guard.Swallow(e); }
            _scanned = false;
            _needFullSend = true;
            _unicastFull = false;
            _afterUnicast.Clear();
            _lastSent.Clear();
            _clientEcho.Clear();
            _echoLogged.Clear();
            _fromApply.Clear();
            _held.Clear();
            _reapplyRooms.Clear();
            _retry.Clear();
            _retryCount.Clear();
            _worldAnimStarted.Clear();
            _maps.Clear();
            _seq.Clear();
            _lastAuthor.Clear();
            _ownSent.Clear();
            _pending.Clear();
            _hostDumpApplied = false;
            _clientLive = false;
            _clientWaitSince = -1f;
            _dumpRetries = 0;
            try { _sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? ""; }
            catch (Exception ex) { _sceneName = ""; WarnOnce("scene-name", ex.Message); }
            int restored = 0;
            foreach (var kvp in _memory)
            {
                if (kvp.Value.Scene != _sceneName) continue;
                // The stored Seq belongs to the previous visit; the host restarts at 1 for this load. Carrying it
                // into NoteApplied raised a client's _seq so every host dump/edit for the key was dropped as stale.
                var e = kvp.Value.Entry;
                e.Seq = 0;
                e.Mask = 0;
                _held[kvp.Key] = e;
                restored++;
            }
            if (restored > 0) _pendingReapply = true;
            ModRuntime.Log?.Msg("[PuzzleSync] Scene refreshed (durable restored=" + restored + ")");
        }

        /// <summary>Log a swallowed apply/read failure once per call site.</summary>
        internal static void WarnOnce(string site, string msg)
        {
            if (_warnedSites.Add(site))
                PlaytestLog.Warn("Puzzle", site + ": " + msg);
        }

        static PKey Key(PuzzleStateEntry e) => new PKey((byte)e.Type, e.WorldId);

        static bool IsDurableMemoryType(PuzzleType t)
        {
            switch (t)
            {
                case PuzzleType.InteractiveLock:
                case PuzzleType.InteractiveLockSingle:
                case PuzzleType.Keypad3D:
                case PuzzleType.ROT_Keypad:
                case PuzzleType.PEN_Codepad:
                case PuzzleType.PatternLock:
                case PuzzleType.DialLock:
                case PuzzleType.NumberLockNew:
                case PuzzleType.DoorLockPuzzle:
                case PuzzleType.MultiLock:
                case PuzzleType.DoorLockControl:
                case PuzzleType.DoorwaySimple:
                case PuzzleType.SwingDoor:
                case PuzzleType.DoorLockEventInteraction:
                case PuzzleType.BiodomeDoorLock:
                case PuzzleType.DET_ServiceLock:
                case PuzzleType.DET_ServiceLock_Key:
                case PuzzleType.SafeDoorSmall:
                case PuzzleType.MultiKeyLock:
                case PuzzleType.UseItemInteraction:
                case PuzzleType.DET_RadioCodeLock:
                case PuzzleType.ROT_DiskManager:
                case PuzzleType.MEM_ChecklistLogic:
                    return true;
                default:
                    return false;
            }
        }

        // Merge rules (IsMergeType, DiffMask, MergeEdit, SameCells, own-echo check): PuzzleMerge.cs (unit-tested).

        int CurSeq(PKey key)
        {
            int s;
            return _seq.TryGetValue(key, out s) ? s : 0;
        }

        /// <summary>Host: bump (live edit) or reuse (full dump) the entry version. Client: carry the base version.</summary>
        void StampOutgoing(ref PuzzleStateEntry e, bool host, bool full)
        {
            PKey key = Key(e);
            if (!host)
            {
                e.Seq = CurSeq(key);
                NoteOwnSent(key, e);
                return;
            }
            int s = CurSeq(key);
            if (!full || s == 0)
            {
                s++;
                _lastAuthor[key] = 0;
            }
            _seq[key] = s;
            e.Seq = s;
            e.Mask = 0;
        }

        // Client: the last few states this client sent per merge-type key (PuzzleMerge.IsStaleOwnEcho).
        private readonly Dictionary<PKey, List<PuzzleMerge.OwnSent>> _ownSent = new Dictionary<PKey, List<PuzzleMerge.OwnSent>>();

        void NoteOwnSent(PKey key, PuzzleStateEntry e)
        {
            if (!IsMergeType(e.Type)) return;
            List<PuzzleMerge.OwnSent> l;
            if (!_ownSent.TryGetValue(key, out l)) { l = new List<PuzzleMerge.OwnSent>(PuzzleMerge.OwnSentKeep); _ownSent[key] = l; }
            PuzzleMerge.NoteOwnSent(l, e, Time.unscaledTime);
        }

        /// <summary>Client: e equals a state this client sent recently but has since moved past.</summary>
        bool IsOwnStaleEcho(PuzzleStateEntry e)
        {
            if (!IsMergeType(e.Type)) return false;
            List<PuzzleMerge.OwnSent> l;
            if (!_ownSent.TryGetValue(Key(e), out l)) return false;
            if (!PuzzleMerge.IsStaleOwnEcho(l, e, Time.unscaledTime)) return false;
            int s;
            if (!_seq.TryGetValue(Key(e), out s) || e.Seq > s) _seq[Key(e)] = e.Seq;
            return true;
        }

        void StampOutgoing(List<PuzzleStateEntry> list, bool host, bool full)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                StampOutgoing(ref e, host, full);
                list[i] = e;
            }
        }

        /// <summary>Client: drop a host entry older than one already applied.</summary>
        bool IsStaleFromHost(PuzzleStateEntry e)
        {
            if (e.Seq == 0) return false;
            int s;
            return _seq.TryGetValue(Key(e), out s) && e.Seq < s;
        }

        /// <summary>
        /// Host: resolve a client-authored entry against the current state in arrival order.
        /// Returns false when dropped (stale regress); e then holds the host's current entry (stamped with
        /// the current Seq) so the caller can send it back to the sender. merged = true when only the client's
        /// cells were overlaid.
        /// </summary>
        bool ResolveClientEntry(ref PuzzleStateEntry e, int sender, bool canApply, out bool merged)
        {
            merged = false;
            PKey key = Key(e);
            PuzzleStateEntry cur;
            bool hasCur = _lastSent.TryGetValue(key, out cur);
            int curSeq = CurSeq(key);
            int author;
            bool senderIsAuthor = _lastAuthor.TryGetValue(key, out author) && author == sender;
            // A client that authored the current state carries an older base Seq by design (its own edits are
            // not echoed back to it), so only somebody else's newer state makes its regress stale.
            // A merge-type edit carries only the cells its author changed, so an older base cannot regress anything.
            if (hasCur && e.Seq < curSeq && !senderIsAuthor && !IsMergeType(e.Type) && !IsProgressed(e) && IsProgressed(cur))
            {
                PlaytestLog.Event("Puzzle", "drop stale " + e.Type + " id=" + unchecked((ulong)e.WorldId).ToString("X16")
                    + " seq=" + e.Seq + "<" + curSeq + " from p" + sender);
                e = cur;
                e.Seq = curSeq;
                e.Mask = 0;
                return false;
            }
            // Checklist only grows: two players ticking different items concurrently keep both.
            if (hasCur && e.Type == PuzzleType.MEM_ChecklistLogic)
            {
                int orInt = e.Int0 | cur.Int0;
                bool orDone = e.Bool0 || cur.Bool0;
                merged = orInt != e.Int0 || orDone != e.Bool0;
                e.Int0 = orInt;
                e.Bool0 = orDone;
            }
            else if (hasCur && e.Mask != 0 && IsMergeType(e.Type))
            {
                // _lastSent lags the live component by up to one poll: merge onto the live state so a cell the
                // host changed in the meantime is not reverted.
                PuzzleStateEntry baseEntry = cur;
                // While the host is transient (loading / other scene) the live scan is half-built: use _lastSent.
                if (e.WorldId != 0 && canApply)
                {
                    var live = Get<Component>(e.Type, e.WorldId);
                    PuzzleStateEntry fresh;
                    if (live != null && IsActiveInScene(live) && TryRead(e.Type, unchecked((ulong)e.WorldId), live, out fresh))
                        baseEntry = fresh;
                }
                e = MergeEdit(baseEntry, e);
                merged = true;
            }
            e.Mask = 0;
            curSeq++;
            _seq[key] = curSeq;
            _lastAuthor[key] = sender;
            e.Seq = curSeq;
            // The resolved entry is the host's new current state even when this peer cannot apply it now.
            _lastSent[key] = e;
            return true;
        }

        public void RequestFullSend() => _needFullSend = true;

        /// <summary>
        /// Join/resync dump: bypass MinFullSendInterval so the unicast snapshot is a true
        /// full puzzle dump. Rate-limit still applies to RefreshScene / ambient RequestFullSend.
        /// </summary>
        public void ForceFullSend()
        {
            var net = LanNetworkManager.Instance;
            if (net != null && net.UnicastActive)
            {
                // A dump for one peer: read-only snapshot on the next Tick inside this unicast scope. The broadcast
                // full-send state (_needFullSend, rate limit, send timer) is left alone for everyone else.
                _unicastFull = true;
                return;
            }
            _needFullSend = true;
            _lastFullSend = -999f;
            _sendTimer = SendInterval;
        }

        /// <summary>SessionReset: apply-scope flags back to their idle values (they are restored by try/finally, this covers an abort mid-scope).</summary>
        internal static void ResetFlags()
        {
            ApplyingPeerPacket = false;
            _liveEdge = false;
            _mutateWorld = true;
            _reapplying = false;
        }

        public void Reset()
        {
            _memory.Clear();
            _sawLocked.Clear();
            WorldObjectPuzzleSyncService.Reset();
            RefreshScene();
            _sendTimer = 0f;
        }

        private Dictionary<ulong, Component> Map(PuzzleType t)
        {
            Dictionary<ulong, Component> m;
            if (!_maps.TryGetValue(t, out m))
            {
                m = new Dictionary<ulong, Component>();
                _maps[t] = m;
            }
            return m;
        }

        private void RegisterAll<T>(PuzzleType type) where T : Component
        {
            var map = Map(type);
            T[] arr = WorldLookup.All<T>();
            if (arr == null) return;
            for (int i = 0; i < arr.Length; i++)
            {
                var c = arr[i];
                if (c == null) continue;
                ulong id = WorldId.FromGameObject(c.gameObject);
                if (id == 0) continue;
                if (!map.ContainsKey(id))
                    map[id] = c;
            }
        }

        private void EnsureScanned()
        {
            if (_scanned) return;
            _maps.Clear();

            for (int i = 0; i < Scanners.Length; i++)
                Scanners[i](this);

            _scanned = true;
            int total = 0;
            foreach (var kvp in _maps) total += kvp.Value.Count;
            ModRuntime.Log?.Msg("[PuzzleSync] Scanned " + total + " components by WorldId");
        }

        public void Tick(LanNetworkManager net)
        {
            if (net == null || !net.IsConnected) return;
            if (!Config.ModConfig.PuzzlesEnabled) return;
            if (!net.HasReadyPeers) return; // zero-peer host: nothing to sync, no read/diff work

            // Inside a join/resync unicast scope every send reaches only the joiner. The dump is a read-only snapshot
            // (no _lastSent / held / Seq writes) and nothing else runs here: a deferred emit, held re-snap or diff
            // recorded now would be sent to the joiner alone and never reach the other peers (3+ players).
            if (net.UnicastActive)
            {
                if (_unicastFull && net.Role == NetworkRole.Host)
                {
                    _unicastFull = false;
                    SendUnicastSnapshot(net);
                }
                return;
            }

            if (_afterUnicast.Count > 0)
            {
                var deferred = _afterUnicast.ToArray();
                _afterUnicast.Clear();
                for (int i = 0; i < deferred.Length; i++)
                {
                    try { deferred[i](); } catch (Exception e) { Guard.Swallow(e); }
                }
            }
            SyncRADation.Patches.EnvEmit.TickSoon();
            LibraryRobotGlide.Tick();

            if (_pendingReapply)
            {
                _pendingReapply = false;
                _reapplyRooms.Clear();
                ReapplyHeld(null);
            }
            else if (_reapplyRooms.Count > 0)
            {
                ReapplyHeld(_reapplyRooms);
                _reapplyRooms.Clear();
            }
            FlushRetries();

            // Unscaled: slow-mo (Time.timeScale) must not cut the send rate.
            _sendTimer += Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            bool fullNow = _needFullSend && (Time.unscaledTime - _lastFullSend >= MinFullSendInterval);
            if (_sendTimer < SendInterval && !fullNow) return;
            _sendTimer = 0f;
            if (fullNow)
                _lastFullSend = Time.unscaledTime;

            float t0 = Time.realtimeSinceStartup;
            EnsureScanned();
            try
            {

            // Clients only emit real local puzzle/lock changes. Buttons, event zones,
            // combat flags and cutscenes are not world-authoring — echoing those
            // runs native trigger()/EventScreen on the other Elster.
            if (net.Role != NetworkRole.Host)
            {
                if (!_clientLive)
                {
                    // No seed: default-true IsProgressed rules (elevator, mural, reactor…) would
                    // overwrite the host. Wait for the host dump (ApplyPuzzleState primes + goes live).
                    // A dump that lands while this client is still loading is dropped by ApplyPuzzleState, so the
                    // wait only runs on a loaded scene; the post-load SnapshotRequest (OnSceneChanged) asks for
                    // a fresh dump and the gate arms on that one. Re-ask if it has not arrived.
                    if (SceneFollowService.LocalIsTransient())
                    {
                        _clientWaitSince = -1f;
                        return;
                    }
                    if (_clientWaitSince < 0f) _clientWaitSince = Time.unscaledTime;
                    ObserveUnseeded();
                    float waited = Time.unscaledTime - _clientWaitSince;
                    if (!_hostDumpApplied && waited < ClientDumpWait)
                    {
                        if (_dumpRetries < ClientDumpMaxRetries && waited >= ClientDumpRetryEvery * (_dumpRetries + 1)
                            && !net.SceneMismatch && !AirlockCinematic.ShouldIgnoreHostFollow(net.HostSceneName))
                        {
                            _dumpRetries++;
                            PlaytestLog.Event("Puzzle", "client dump retry " + _dumpRetries + " (waited " + waited.ToString("0.0") + "s)");
                            net.RequestWorldSnapshot();
                        }
                        return;
                    }
                    PrimeClientBaseline("no host dump within " + ClientDumpWait.ToString("0") + "s");
                    return;
                }
                _tickLocal.Clear();
                ReadAll(_tickLocal, false, clientFilter: true, activeOnly: true, firstIsBaseline: true, dampEcho: true);
                if (_tickLocal.Count > 0)
                {
                    StampOutgoing(_tickLocal, host: false, full: false);
                    for (int i = 0; i < _tickLocal.Count; i++)
                        HoldIfProgressed(_tickLocal[i]);
                    if (ModRuntime.VerboseLogging)
                        PlaytestLog.Verbose("Puzzle", "client diff " + _tickLocal.Count
                            + " " + Describe(_tickLocal));
                    net.SendPuzzleState(_tickLocal, false);
                }
                return;
            }

            _tickEntries.Clear();
            bool full = fullNow;
            ReadAll(_tickEntries, full, clientFilter: false, activeOnly: !full);
            // Only clear after an actual full dump. Clearing on a rate-limited diff tick
            // dropped join/resync full state when MinFullSendInterval still blocked.
            if (full)
                _needFullSend = false;
            StampOutgoing(_tickEntries, host: true, full: full);
            for (int i = 0; i < _tickEntries.Count; i++)
                HoldIfProgressed(_tickEntries[i]);
            if (_tickEntries.Count == 0) return;
            net.SendPuzzleState(_tickEntries, full);
            }
            finally
            {
                HitchTrace.Cost("puzzle", (Time.realtimeSinceStartup - t0) * 1000f);
            }
        }

        /// <summary>
        /// Host, unicast scope: the full puzzle state for the joiner only. Every entry carries the key's current
        /// version (0 when never stamped: never stale) and nothing is recorded, so the broadcast diff still sends
        /// any change to everybody.
        /// </summary>
        void SendUnicastSnapshot(LanNetworkManager net)
        {
            float t0 = Time.realtimeSinceStartup;
            EnsureScanned();
            try
            {
                _tickEntries.Clear();
                ReadAll(_tickEntries, true, clientFilter: false, activeOnly: false, record: false);
                for (int i = 0; i < _tickEntries.Count; i++)
                {
                    var e = _tickEntries[i];
                    e.Seq = CurSeq(Key(e));
                    e.Mask = 0;
                    _tickEntries[i] = e;
                }
                if (_tickEntries.Count > 0)
                    net.SendPuzzleState(_tickEntries, true);
            }
            finally
            {
                HitchTrace.Cost("puzzle", (Time.realtimeSinceStartup - t0) * 1000f);
            }
        }

        /// <summary>A dump only seeds the gate when it is for the scene this client is in.</summary>
        bool HostSceneMatches(LanNetworkManager net)
        {
            if (net == null) return true;
            string hs = net.HostSceneName;
            return string.IsNullOrEmpty(hs) || string.IsNullOrEmpty(_sceneName)
                || string.Equals(hs, _sceneName, StringComparison.Ordinal);
        }

        /// <summary>Emit gate: a client stays silent until seeded by the host dump and only emits client types.</summary>
        bool MayEmit(LanNetworkManager net, PuzzleType type)
            => net.Role == NetworkRole.Host || (_clientLive && ClientMayEmit(type));

        bool Unseeded(LanNetworkManager net, PuzzleType type)
            => net.Role != NetworkRole.Host && !_clientLive && ClientMayEmit(type);

        /// <summary>Client, unseeded: remember the latest local state of a key (first observation = its baseline).</summary>
        void RecordPending(PuzzleStateEntry entry, bool solvedEdge = false, bool explicitEdit = true)
        {
            PKey key = Key(entry);
            PendingLocal p;
            if (!_pending.TryGetValue(key, out p))
            {
                p.First = entry;
                // An explicit "solved" emit has no earlier read: its prior state is the unsolved one.
                if (solvedEdge) p.First.Bool0 = false;
            }
            p.Latest = entry;
            if (explicitEdit) p.Explicit = true;
            _pending[key] = p;
        }

        /// <summary>0.5 s poll while unseeded: observe (not send) the client-emittable components.</summary>
        void ObserveUnseeded()
        {
            foreach (var typeMap in _maps)
            {
                var type = typeMap.Key;
                if (!ClientMayEmit(type)) continue;
                if (type == PuzzleType.UseItemMulti || type == PuzzleType.MED_KeyGrid || type == PuzzleType.ArianePhotoCode)
                    continue;
                foreach (var kvp in typeMap.Value)
                {
                    if (kvp.Value == null || !IsActiveInScene(kvp.Value)) continue;
                    PuzzleStateEntry entry;
                    if (TryRead(type, kvp.Key, kvp.Value, out entry)) RecordPending(entry, false, explicitEdit: false);
                }
            }
        }

        /// <summary>Client: snapshot current state as the baseline (nothing is sent) and start emitting real local diffs.</summary>
        private void PrimeClientBaseline(string why)
        {
            EnsureScanned();
            // Baselines must be read before the seed read overwrites _lastSent: with a host dump applied,
            // _lastSent[key] is the host's state for that key.
            _pendingFlush.Clear();
            foreach (var kvp in _pending)
            {
                var f = new PendingFlush { First = kvp.Value.First, Latest = kvp.Value.Latest, Explicit = kvp.Value.Explicit };
                PuzzleStateEntry hostBase = default(PuzzleStateEntry);
                f.HaveHostBase = _hostDumpApplied && _lastSent.TryGetValue(kvp.Key, out hostBase);
                f.HostBase = hostBase;
                _pendingFlush.Add(f);
            }
            _pending.Clear();
            _tickSeed.Clear();
            ReadAll(_tickSeed, true, clientFilter: true, activeOnly: false);
            _tickSeed.Clear();
            _needFullSend = false;
            _clientLive = true;
            PlaytestLog.Event("Puzzle", "client live (" + why + ")");
            FlushPending();
        }

        /// <summary>
        /// A solve/edit the client made before it was seeded: emit it as a normal client edit (base = the host's
        /// stamped Seq). Only keys whose latest local state differs from the FIRST local observation count (a real
        /// local change: a merge type's untouched default, e.g. PEN_Reaktor Int0=736, never flushes), and a float-only
        /// difference counts only for an explicit hook emit (a polled Float0 can be self-animating drift). The mask is
        /// the cells the client itself changed (First vs Latest); a host dump base only narrows it to the cells that
        /// still differ from the host. After a host dump the live component was reset to the host state: the local
        /// edit is restored first (cell-merged onto the host base for merge types).
        /// </summary>
        void FlushPending()
        {
            var net = LanNetworkManager.Instance;
            if (net == null || net.Role == NetworkRole.Host || _pendingFlush.Count == 0) { _pendingFlush.Clear(); return; }
            _pendingOut.Clear();
            for (int i = 0; i < _pendingFlush.Count; i++)
            {
                var f = _pendingFlush[i];
                var e = f.Latest;
                if (!IsProgressed(e)) continue;
                int mask = DiffMask(f.First, e);
                if (f.HaveHostBase) mask &= DiffMask(f.HostBase, e);
                if (mask == 0) continue;
                if (!f.Explicit && (mask & ~FloatMask) == 0) continue;
                if (f.HaveHostBase && IsProgressed(f.HostBase) && !IsMergeType(e.Type)) continue;
                PKey key = Key(e);
                e.Seq = CurSeq(key);
                e.Mask = mask;
                PuzzleStateEntry local = e;
                if (f.HaveHostBase)
                {
                    if (IsMergeType(e.Type)) local = MergeEdit(f.HostBase, e);
                    local.Seq = CurSeq(key);
                    bool prevMutate = _mutateWorld;
                    _mutateWorld = false;
                    NetGate.BeginApply();
                    try { ApplyEntry(local, cinematic: false); }
                    finally
                    {
                        _mutateWorld = prevMutate;
                        NetGate.EndApply();
                    }
                }
                _lastSent[key] = local;
                HoldIfProgressed(local);
                _pendingOut.Add(e);
            }
            _pendingFlush.Clear();
            if (_pendingOut.Count == 0) return;
            PlaytestLog.Event("Puzzle", "client pre-seed edits emitted n=" + _pendingOut.Count);
            net.SendPuzzleState(_pendingOut, false);
            _pendingOut.Clear();
        }

        /// <param name="dampEcho">Client diff: hold back a state this client already sent within EchoWindow.</param>
        /// <param name="record">False = read-only snapshot (unicast dump): _lastSent is not touched.</param>
        private void ReadAll(List<PuzzleStateEntry> entries, bool full, bool clientFilter, bool activeOnly,
            bool firstIsBaseline = false, bool dampEcho = false, bool record = true)
        {
            bool emittedMultiBlocked = false;
            foreach (var typeMap in _maps)
            {
                var type = typeMap.Key;
                if (clientFilter && !ClientMayEmit(type)) continue;
                foreach (var kvp in typeMap.Value)
                {
                    if (kvp.Value == null) continue;
                    if (activeOnly && !IsActiveInScene(kvp.Value)) continue;
                    if (type == PuzzleType.UseItemMulti)
                    {
                        if (emittedMultiBlocked) continue;
                        emittedMultiBlocked = true;
                    }
                    // Globals-only: instance WorldIds would double-emit with WorldId=0 below.
                    if (type == PuzzleType.MED_KeyGrid || type == PuzzleType.ArianePhotoCode)
                        continue;
                    PuzzleStateEntry entry;
                    if (!TryRead(type, kvp.Key, kvp.Value, out entry)) continue;
                    Collect(entries, entry, full, firstIsBaseline, dampEcho, record);
                }
            }

            // Globals (WorldId = 0). Alarm stays host-only (client poll would clobber).
            // RadioManagerState: client may emit moduleInstalled=true (latch on apply).
            // KeyGrid / ArianePhotoCode are client-emittable (static solved/code reverse arrow).
            if (!clientFilter)
                Collect(entries, EnemySyncService.ReadGlobalAlert(), full, firstIsBaseline, dampEcho, record);
            if (!clientFilter || ClientMayEmit(PuzzleType.RadioManagerState))
            {
                var radio = RadioPuzzleSyncService.ReadManagerState();
                // Client must not emit false — would race host latch before acquire.
                if (!(clientFilter && (radio.Int0 & 1) == 0))
                    Collect(entries, radio, full, firstIsBaseline, dampEcho, record);
            }
            if (!clientFilter || ClientMayEmit(PuzzleType.MED_KeyGrid))
                Collect(entries, ResidencyPuzzleSyncService.ReadKeyGridGlobal(), full, firstIsBaseline, dampEcho, record);
            if (!clientFilter || ClientMayEmit(PuzzleType.ArianePhotoCode))
                Collect(entries, ResidencyPuzzleSyncService.ReadArianePhotoCodeGlobal(), full, firstIsBaseline, dampEcho, record);
        }

        void Collect(List<PuzzleStateEntry> entries, PuzzleStateEntry entry, bool full, bool firstIsBaseline,
            bool dampEcho, bool record)
        {
            PKey key = Key(entry);
            PuzzleStateEntry prev;
            bool hadPrev = _lastSent.TryGetValue(key, out prev);
            bool fromApply = _fromApply.Contains(key);
            if (!ChangedOrFirst(ref entry, full, firstIsBaseline, record))
            {
                // The local read converged on a state the peer applied: the damper's record is history, so a later
                // local change to the old state is a real edit (a re-toggle), not the non-converging echo.
                if (dampEcho && fromApply) _clientEcho.Remove(key);
                return;
            }
            if (dampEcho && EchoDamped(key, entry))
            {
                // ChangedOrFirst already recorded this state as sent: put the previous one back, or a real re-toggle
                // to it inside the window (host changed it meanwhile) would read as "no change" and never go out.
                if (hadPrev) _lastSent[key] = prev;
                else _lastSent.Remove(key);
                if (fromApply) _fromApply.Add(key);
                return;
            }
            entries.Add(entry);
        }

        struct EchoRec { public PuzzleStateEntry Entry; public float At; }
        readonly Dictionary<PKey, EchoRec> _clientEcho = new Dictionary<PKey, EchoRec>();
        // Keys whose _lastSent is a state applied from a peer (not this peer's own read/send).
        readonly HashSet<PKey> _fromApply = new HashSet<PKey>();
        readonly HashSet<PKey> _echoLogged = new HashSet<PKey>();
        const float EchoWindow = 10f;

        /// <summary>
        /// A state the client cannot converge on (its read differs from what applying the host's entry produces,
        /// e.g. a door plate driven by local room traversal) ping-ponged forever: client re-reads X, host applies
        /// X, re-reads Y, client applies Y, re-reads X... (PEN_Wreck, one InteractiveLockSingle ~1/s). The same
        /// local state is sent once per <see cref="EchoWindow"/>; a different state always goes out at once.
        /// </summary>
        bool EchoDamped(PKey key, PuzzleStateEntry e)
        {
            float now = Time.unscaledTime;
            EchoRec rec;
            if (_clientEcho.TryGetValue(key, out rec) && SameCells(rec.Entry, e) && now - rec.At < EchoWindow)
            {
                if (_echoLogged.Add(key))
                    PlaytestLog.Event("Puzzle", "echo damp " + e.Type + " " + unchecked((ulong)e.WorldId).ToString("X16")
                        + " (host keeps a different state)");
                return true;
            }
            _clientEcho[key] = new EchoRec { Entry = e, At = now };
            return false;
        }

        private static bool IsActiveInScene(Component c)
        {
            try
            {
                var go = c.gameObject;
                return go != null && go.activeInHierarchy;
            }
            catch
            {
                return false;
            }
        }

        private bool TryRead(PuzzleType type, ulong id, Component c, out PuzzleStateEntry entry)
        {
            entry = default;
            long wid = unchecked((long)id);
            try
            {
                PuzzleReader reader;
                if (Readers.TryGetValue(type, out reader))
                    return reader(type, c, wid, out entry);
            }
            catch (Exception e) { Guard.Swallow(e); }
            return false;
        }

        private static PuzzleStateEntry Mk(PuzzleType type, long worldId, bool b0, bool b1, bool b2, int i0, int i1, int i2, int i3, float f0)
            => PuzzleDomainUtil.Mk(type, worldId, b0, b1, b2, i0, i1, i2, i3, f0);

        /// <summary>
        /// Change detect vs the last sent/applied state. A real change also fills entry.Mask (cells that differ
        /// from that previous state) so the host can merge concurrent edits. firstIsBaseline: a never-seen key is
        /// recorded as the baseline and not reported (client diff path).
        /// </summary>
        private bool ChangedOrFirst(ref PuzzleStateEntry entry, bool fullRefresh, bool firstIsBaseline = false,
            bool record = true)
        {
            PKey key = Key(entry);
            if (fullRefresh)
            {
                if (record) RecordSent(key, entry);
                return true;
            }
            PuzzleStateEntry prev;
            if (_lastSent.TryGetValue(key, out prev))
            {
                // Client: a lock's traverse plate (Bool1 = AutoTraverseDoor blocker / DoorLockControl) is set by
                // native code from where THIS player stands, so it never converges with the host's and the lock
                // ping-ponged every tick (Cryogenics doors). The client authors only locked/unlocked; the plate
                // stays whatever the host last sent.
                // Host: a plate-only flip is the same per-player native state, so it is not broadcast either (the
                // client would otherwise be forced into the host's blocker); it still rides any lock change and
                // the full dump.
                if (entry.Type == PuzzleType.InteractiveLockSingle)
                {
                    var role = LanNetworkManager.Instance;
                    if (firstIsBaseline || (role != null && role.Role == NetworkRole.Client))
                        entry.Bool1 = prev.Bool1;
                    else if (entry.Bool1 != prev.Bool1 && entry.Bool0 == prev.Bool0 && entry.Bool2 == prev.Bool2)
                        return false;
                }
                if (prev.Bool0 == entry.Bool0 && prev.Bool1 == entry.Bool1 && prev.Bool2 == entry.Bool2
                    && prev.Int0 == entry.Int0 && prev.Int1 == entry.Int1 && prev.Int2 == entry.Int2 && prev.Int3 == entry.Int3
                    && Mathf.Approximately(prev.Float0, entry.Float0)
                    && Mathf.Approximately(prev.Float1, entry.Float1))
                    return false;
                entry.Mask = DiffMask(prev, entry);
            }
            else if (firstIsBaseline)
            {
                if (record) RecordSent(key, entry);
                return false;
            }
            if (record) RecordSent(key, entry);
            return true;
        }

        void RecordSent(PKey key, PuzzleStateEntry entry)
        {
            _lastSent[key] = entry;
            _fromApply.Remove(key);
        }

        private static bool ClientMayEmit(PuzzleType type) => ClientEmitTypes.Contains(type);

        private static string Describe(IList<PuzzleStateEntry> entries)
        {
            if (entries == null || entries.Count == 0) return "";
            int n = entries.Count < 6 ? entries.Count : 6;
            var parts = new string[n];
            for (int i = 0; i < n; i++)
            {
                var e = entries[i];
                parts[i] = e.Type + (e.Bool0 ? "+ " : " ") + unchecked((ulong)e.WorldId).ToString("X16");
            }
            return string.Join(", ", parts) + (entries.Count > n ? "…" : "");
        }

        private void NoteApplied(PuzzleStateEntry entry, bool keepPending)
        {
            PKey key = Key(entry);
            _lastSent[key] = entry;
            if (!_reapplying) _fromApply.Add(key);
            var net = LanNetworkManager.Instance;
            // A live host edit supersedes a pre-seed local observation. A dump / held re-snap does not: the pending
            // record survives so PrimeClientBaseline can still flush a real local solve the dump left in place.
            if (!keepPending && net != null && net.Role != NetworkRole.Host && !_clientLive)
                _pending.Remove(key);
            if (net != null && net.Role != NetworkRole.Host && entry.Seq != 0 && entry.Seq > CurSeq(key))
                _seq[key] = entry.Seq;
        }

        private static bool IsProgressed(PuzzleStateEntry e)
        {
            if (e.Type == PuzzleType.EXC_Elevator)
                return e.Bool0 || e.Bool1;
            if (e.Type == PuzzleType.ArianePhotoCode)
                return e.Int0 != 0 || e.Bool0;
            if (e.Type == PuzzleType.AraNest)
                return e.Bool0 || e.Bool2;
            if (e.Type == PuzzleType.LAB_RifleQuest || e.Type == PuzzleType.LOV_Microfiche)
                return e.Bool0 || e.Bool1 || e.Bool2;
            // Door / lock flags: hold unlocked or open so remount re-snaps.
            if (e.Type == PuzzleType.SwingDoor)
                return e.Bool0 || !e.Bool1;
            if (e.Type == PuzzleType.DoorwaySimple
                || e.Type == PuzzleType.DoorLockControl
                || e.Type == PuzzleType.InteractiveLock
                || e.Type == PuzzleType.InteractiveLockSingle
                || e.Type == PuzzleType.NumberLockNew
                || e.Type == PuzzleType.DoorLockPuzzle)
                return !e.Bool0;
            if (e.Type == PuzzleType.FoldingShutterDoor)
                return e.Float0 > 0.01f;
            // Cabin floor/state must survive room remount.
            if (e.Type == PuzzleType.CentralElevator)
                return true;
            // Dig S: hold gate-active Bool0 even when solved weight is 0 so remount
            // / late-join re-snaps MultiInteraction.SetActive(true).
            if (e.Type == PuzzleType.LAB_Waage)
                return e.Bool0 || !Mathf.Approximately(e.Float0, 0f);
            if (e.Type == PuzzleType.ROT_RadioAlignment)
                return e.Bool0 || e.Int0 != 0 || e.Int1 != 0;
            // LAB_Rings: hold partial finger pack (Int0) so remount / late-join
            // re-snaps mid-puzzle place/take, not only final Bool0 solved.
            if (e.Type == PuzzleType.LAB_Rings)
                return e.Bool0 || e.Int0 != 0;
            // DET_ServiceLock: hold partial pinning pack (Int0) so remount / late-join
            // re-snaps mid-pin state, not only final Bool0 solved.
            if (e.Type == PuzzleType.DET_ServiceLock)
                return e.Bool0 || e.Int0 != 0;
            // FloodControls: hold partial input pack (Int1) so remount / late-join
            // re-snaps mid-switch input[], not only final Bool0 done.
            if (e.Type == PuzzleType.FloodControls)
                return e.Bool0 || e.Int1 != 0;
            // MED_KeyGrid: hold partial connected pack (Int1 count marker) so remount /
            // late-join re-snaps mid-node state, including all-zero Int0 packs.
            if (e.Type == PuzzleType.MED_KeyGrid)
                return e.Bool0 || e.Int1 != 0;
            // MED_Incinerator: hold mid-dial A/B/C (initial 10/10/10) so remount /
            // late-join re-snaps unsolved dial changes, not only final Bool0 solved.
            if (e.Type == PuzzleType.MED_Incinerator)
                return e.Bool0 || e.Int0 != 10 || e.Int1 != 10 || e.Int2 != 10;
            // MultiLock: hold partial element-key pack (Int0 Fire/Earth/Water/Air/Gold[/Star])
            // so remount / late-join re-snaps mid-key inserts, not only final Bool0 unlocked.
            if (e.Type == PuzzleType.MultiLock)
                return e.Bool0 || e.Int0 != 0;
            // DialLock (ROT_DialLock): hold mid-dial A/B/C/D (initial 0/0/0/5) so
            // remount / late-join re-snaps unsolved dial changes, not only final Bool0 solved.
            if (e.Type == PuzzleType.DialLock)
                return e.Bool0 || e.Int0 != 0 || e.Int1 != 0 || e.Int2 != 0 || e.Int3 != 5;
            // MED_CardWriter: hold partial connected pack (Int3 count marker) + hasCard
            // so remount / late-join re-snaps mid-trace / inserted card, including
            // all-zero Int0 packs (Dig AB).
            if (e.Type == PuzzleType.MED_CardWriter)
                return e.Bool0 || e.Bool1 || e.Int3 != 0;
            // EvidenceLocker: hold mid-button states pack (Int0/Int1) so remount /
            // late-join re-snaps unsolved button presses, not only final Bool0 solved.
            if (e.Type == PuzzleType.EvidenceLockerPuzzle)
                return e.Bool0 || e.Int0 != 0 || e.Int1 != 0;
            // RES_Power: hold mid-fuse states pack (Int0) so remount / late-join
            // re-snaps unsolved fuse flips, not only final Bool0 solved.
            if (e.Type == PuzzleType.RES_Power)
                return e.Bool0 || e.Int0 != 0;
            // MED_Pump: hold mid-water a/b/c (initial 12/0/0) so remount / late-join
            // re-snaps unsolved transfers, not only final Bool0 solved.
            if (e.Type == PuzzleType.MED_Pump)
                return e.Bool0 || e.Int0 != 12 || e.Int1 != 0 || e.Int2 != 0;
            // RES_Shrine: hold mid-dial big/mid/small (initial 0/0/0) so remount /
            // late-join re-snaps unsolved plate turns, not only final Bool0 solved.
            if (e.Type == PuzzleType.RES_Shrine)
                return e.Bool0 || e.Int0 != 0 || e.Int1 != 0 || e.Int2 != 0;
            // ROT_Mural: hold mid-moon Pos pack (Int0–Int3) + busy so remount /
            // late-join re-snaps unsolved moon turns, not only final Bool0 finished.
            // DesiredPos bits make Int≠0 from load — intentional so FullRefresh
            // always carries the moon pack (Dig AF).
            if (e.Type == PuzzleType.ROT_Mural)
                return e.Bool0 || e.Bool1 || e.Int0 != 0 || e.Int1 != 0 || e.Int2 != 0 || e.Int3 != 0;
            // PEN_Reaktor: hold mid-rod positions pack (Int0) + current (Int1) so
            // remount / late-join re-snaps unsolved rod moves, not only final Bool0
            // solved. Initial AssetStudio positions [0,4,3,1] → Int0=736≠0 from load
            // — intentional FullRefresh carry (Dig AG).
            if (e.Type == PuzzleType.PEN_Reaktor)
                return e.Bool0 || e.Bool1 || e.Int0 != 0 || e.Int1 != 0;
            // MultiKeyLock: hold partial keys[] pack (Int0) so remount / late-join
            // re-snaps inserted keys, not only final Bool0 (every key true).
            if (e.Type == PuzzleType.MultiKeyLock)
                return e.Bool0 || e.Int0 != 0;
            // RES_MusicBox: hold inserted cassette (Bool1) before the lid opens.
            if (e.Type == PuzzleType.RES_MusicBox)
                return e.Bool0 || e.Bool1;
            // ROT_MeatBlocker: hold mid pickup count (Int0) before the seal drops.
            if (e.Type == PuzzleType.ROT_MeatBlocker)
                return e.Bool0 || e.Int0 != 0;
            // DET_RadioCodeLock: hold host-generated frequency/code/hint (Int0–2)
            // so remount does not Start() a second local code. 0/0/0 is pre-Start.
            if (e.Type == PuzzleType.DET_RadioCodeLock)
                return e.Bool0 || e.Int0 != 0 || e.Int1 != 0 || e.Int2 != 0;
            // PatternLock: hold the button grid (Int3 = cell count, including all-off)
            // so remount / the other peer keep mid-presses, not only Bool0 solved.
            if (e.Type == PuzzleType.PatternLock)
                return e.Bool0 || e.Int3 != 0;
            // ROT_Tarot: hold the card pack (Int3 = slot count, empty coded 0xFF)
            // plus darkmode. Int3 != 0 from the first successful read.
            if (e.Type == PuzzleType.ROT_Tarot)
                return e.Bool0 || e.Int3 != 0;
            // RES_LibraryPC: hold mid-maze robotPos (Bool1 = pack valid from TryRead).
            if (e.Type == PuzzleType.RES_LibraryPC)
                return e.Bool0 || e.Bool1;
            // MED_Adler_EVdoors: hold DoorL/DoorR local X (Float0/Float1) + open Bool0.
            if (e.Type == PuzzleType.MED_Adler_EVdoors)
                return e.Bool0
                    || !Mathf.Approximately(e.Float0, 0f)
                    || !Mathf.Approximately(e.Float1, 0f);
            // BiodomeDoorLock: hold partial KeyLevel (Int0) before !hasLock.
            if (e.Type == PuzzleType.BiodomeDoorLock)
                return e.Bool0 || e.Int0 != 0;
            // MultiConditionEvent: hold partial tried (Int0) before triedOnce.
            if (e.Type == PuzzleType.MultiConditionEvent)
                return e.Bool0 || e.Int0 != 0;
            // MEM_ChecklistLogic: any item checked (Int0 bitmask) or complete.
            if (e.Type == PuzzleType.MEM_ChecklistLogic)
                return e.Int0 != 0 || e.Bool0;
            // ROT_DiskManager: either disk inserted.
            if (e.Type == PuzzleType.ROT_DiskManager)
                return e.Bool0 || e.Bool1;
            if (e.Type == PuzzleType.DET_WallCreature)
                return e.Bool0 || WorldObjectPuzzleSyncService.WallDamaged(e.WorldId, e.Int0);
            return ProgressedBool0.Contains(e.Type) && e.Bool0;
        }

        public void ApplyPuzzleState(PuzzleStateMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (msg.Entries == null || msg.Entries.Length == 0) return;
            if (net != null && msg.SenderPlayerId == net.LocalPlayerId)
                return;
            bool isHost = net != null && net.Role == NetworkRole.Host;
            bool canApply = !SceneFollowService.LocalIsTransient()
                && !(net != null && net.SceneMismatch);

            _applyScratch.Clear();
            _relayAll.Clear();
            _relayExcept.Clear();
            _resync.Clear();
            if (isHost)
            {
                // Arrival order wins: each client entry is resolved against the host's current state
                // (only the cells that client changed are overlaid) and stamped with a new version.
                // The host relays even when it cannot apply (loading / other scene) so 3+ peers stay in sync.
                for (int i = 0; i < msg.Entries.Length; i++)
                {
                    var e = msg.Entries[i];
                    // Client Start() generates its own radio code. Applying that
                    // unsolved payload would replace the host's frequency/code/hint.
                    if (e.Type == PuzzleType.DET_RadioCodeLock && !e.Bool0)
                        continue;
                    bool merged;
                    if (!ResolveClientEntry(ref e, msg.SenderPlayerId, canApply, out merged))
                    {
                        _resync.Add(e); // dropped as stale: tell the sender what the host actually holds
                        continue;
                    }
                    _applyScratch.Add(e);
                    if (merged) _relayAll.Add(e);
                    else _relayExcept.Add(e);
                }
            }
            else
            {
                if (!canApply) return;
                for (int i = 0; i < msg.Entries.Length; i++)
                {
                    if (IsStaleFromHost(msg.Entries[i])) continue;
                    _applyScratch.Add(msg.Entries[i]);
                }
            }
            if (_resync.Count > 0 && net != null && msg.SenderPlayerId >= 1)
            {
                int prevUni = net.BeginUnicast(msg.SenderPlayerId);
                try { net.SendPuzzleState(_resync, false); }
                finally { net.EndUnicast(prevUni); }
            }
            if (_applyScratch.Count == 0) return;

            if (canApply)
            {
                if (msg.FullRefresh)
                    PlaytestLog.Event("Puzzle", "apply n=" + _applyScratch.Count
                        + " from=" + msg.SenderPlayerId + " full " + Describe(_applyScratch));
                else if (ModRuntime.VerboseLogging)
                    PlaytestLog.Verbose("Puzzle", "apply n=" + _applyScratch.Count
                        + " from=" + msg.SenderPlayerId + " " + Describe(_applyScratch));
                EnsureScanned();
                bool cinematic = !msg.FullRefresh;
                bool prevMutate = _mutateWorld;
                _mutateWorld = cinematic;
                NetGate.BeginApply();
                ApplyingPeerPacket = isHost;
                int prevSender = NetGate.ApplySender;
                if (isHost && msg.SenderPlayerId >= 1) NetGate.ApplySender = msg.SenderPlayerId;
                bool prevLive = PuzzleFx.LiveApply;
                PuzzleFx.LiveApply = cinematic;
                try
                {
                    for (int i = 0; i < _applyScratch.Count; i++)
                    {
                        // A stale echo of this client's own earlier edit (the host relays merges to everyone):
                        // applying it would flick the puzzle back one press until the newer echo lands.
                        if (!isHost && IsOwnStaleEcho(_applyScratch[i])) continue;
                        ApplyEntry(_applyScratch[i], cinematic);
                    }
                }
                finally
                {
                    PuzzleFx.LiveApply = prevLive;
                    NetGate.ApplySender = prevSender;
                    ApplyingPeerPacket = false;
                    _mutateWorld = prevMutate;
                    NetGate.EndApply();
                }

                if (cinematic)
                {
                    try { DoorNative.ReassertLockVisuals(); }
                    catch (Exception ex) { WarnOnce("reassert-locks", ex.Message); }
                }
            }

            if (isHost)
            {
                if (_relayExcept.Count > 0)
                    net.SendPuzzleState(_relayExcept, false, msg.SenderPlayerId);
                if (_relayAll.Count > 0)
                    net.SendPuzzleState(_relayAll, false);
            }
            else if (msg.FullRefresh && !_hostDumpApplied && HostSceneMatches(net))
            {
                // The host dump is now the baseline: from here on only real local changes are emitted.
                _hostDumpApplied = true;
                if (!_clientLive)
                    PrimeClientBaseline("host dump applied");
            }
        }

        public void QueueReapply()
        {
            _pendingReapply = true;
        }

        /// <summary>Room entry / chunk remount: re-snap only what sits in that room (or in no room).</summary>
        public void QueueReapply(Room room)
        {
            Transform t = null;
            try { if (room != null) t = room.transform; } catch (Exception e) { Guard.Swallow(e); }
            if (t == null) { _pendingReapply = true; return; }
            if (!_reapplyRooms.Contains(t)) _reapplyRooms.Add(t);
        }

        // Cryo/Codepad ask "held" to mean SOLVED (they disable the pad and kill its buttons). A PatternLock entry
        // is held while unsolved too (mid-grid presses, Int3 = cell count), so for them only Bool0 counts:
        // otherwise every button of a fresh lock was swallowed as "already solved" (PEN_Wreck cryo, 0.5.64).
        static bool HeldSolved(PuzzleStateEntry e)
            => e.Type == PuzzleType.PatternLock ? e.Bool0 : IsProgressed(e);

        bool IPuzzleDomainHost.IsHeld(PuzzleType type, ulong worldId)
        {
            if (worldId == 0) return false;
            PuzzleStateEntry e;
            return _held.TryGetValue(new PKey((byte)type, unchecked((long)worldId)), out e) && HeldSolved(e);
        }

        /// <summary>
        /// A solved entry of this type is held under a WorldId no local component has, and candidateId (an unsolved
        /// component of the same type) is one the host never sent: only then is candidateId the local copy of that
        /// entry (a WorldId mismatch). A pad the host knows by its own id is never snapped by someone else's entry,
        /// and types never stand in for each other (an unmatched pattern lock used to solve every cryo pad).
        /// </summary>
        bool IPuzzleDomainHost.HeldUnmatched(PuzzleType type, ulong candidateId)
        {
            if (candidateId == 0) return false;
            if (_seq.ContainsKey(new PKey((byte)type, unchecked((long)candidateId)))) return false;
            return HeldSolvedUnmatched(type);
        }

        bool HeldSolvedUnmatched(PuzzleType type)
        {
            foreach (var kvp in _held)
            {
                var e = kvp.Value;
                if (e.Type != type || !HeldSolved(e)) continue;
                if (Get<Component>(type, e.WorldId) == null)
                    return true;
            }
            return false;
        }

        void IPuzzleDomainHost.RemapHeld(PuzzleType type, ulong newId)
        {
            if (newId == 0) return;
            PKey from = default(PKey);
            bool found = false;
            foreach (var kvp in _held)
            {
                var e = kvp.Value;
                if (e.Type != type || !HeldSolved(e)) continue;
                if (Get<Component>(type, e.WorldId) != null) continue;
                from = kvp.Key;
                found = true;
                break;
            }
            if (!found) return;
            var moved = _held[from];
            _held.Remove(from);
            moved.WorldId = unchecked((long)newId);
            _held[Key(moved)] = moved;
            PlaytestLog.Event("Puzzle", "remap " + type + " -> " + newId.ToString("X16"));
        }

        /// <summary>Native OnEnable re-enables pad/open. Shut them in the same callback if already solved.</summary>
        public void HandlePenCryoEnabled(PEN_Cryo x) => _cryo.HandlePenCryoEnabled(x);

        public void HandleCryoLockEnabled(CryoDoorLock c) => _cryo.HandleCryoLockEnabled(c);

        public void HandleCodepadEnabled(PEN_Codepad pad) => _codepad.HandleCodepadEnabled(pad);

        public void HandlePatternLockEnabled(LAB_PatternLock pad) => _codepad.HandlePatternLockEnabled(pad);

        public bool ShouldKillOverlay(Interaction it) => _codepad.ShouldKillOverlay(it);

        /// <param name="rooms">null = every held entry; else only entries in one of these (re)mounted rooms or in no
        /// room at all.</param>
        void ReapplyHeld(List<Transform> rooms)
        {
            if (_held.Count == 0)
            {
                try { DoorSyncService.ReapplyHeldDoors(); } catch (Exception ex) { WarnOnce("reapply-doors", ex.Message); }
                return;
            }
            // Snapshot first: ApplyEntry → HoldIfProgressed writes _held while this runs.
            _reapplyScratch.Clear();
            foreach (var kvp in _held)
                _reapplyScratch.Add(kvp.Value);
            // MutateWorld stays true on purpose: UseItem (per-player airlock card latch) and RevealPickups need it.
            // The replay-prone consumers (FlipSwitch.Flip, DET_ServiceLock pins, PEN_Reaktor positions, LAB_Rings
            // fingers) read ReplayWorld, which is false here, so a reapply never re-runs a rising-edge consequence.
            bool prevMutate = _mutateWorld;
            bool prevReapplying = _reapplying;
            _mutateWorld = true;
            _reapplying = true;
            int applied = 0, skipped = 0;
            NetGate.BeginApply();
            try
            {
                for (int i = 0; i < _reapplyScratch.Count; i++)
                {
                    var e = _reapplyScratch[i];
                    var c = Get<Component>(e.Type, e.WorldId);
                    if (c != null && !IsActiveInScene(c))
                        continue;
                    if (c != null && !InReapplyScope(c, rooms))
                        continue;
                    // Only genuinely reverted state is re-snapped: a component that still reads as the held entry
                    // needs nothing (and the heavy snaps ran for every held entry on every room change).
                    PuzzleStateEntry live;
                    if (c != null && TryRead(e.Type, unchecked((ulong)e.WorldId), c, out live) && SameCells(live, e))
                    {
                        skipped++;
                        continue;
                    }
                    ApplyEntry(e, cinematic: false);
                    _reapplyScratch[applied++] = e;
                }
            }
            finally
            {
                _mutateWorld = prevMutate;
                _reapplying = prevReapplying;
                NetGate.EndApply();
            }
            if (applied > 0)
            {
                _reapplyScratch.RemoveRange(applied, _reapplyScratch.Count - applied);
                PlaytestLog.Event("Puzzle", "reapply held " + applied + "/" + _held.Count
                    + (rooms != null ? " rooms=" + rooms.Count : "") + " " + Describe(_reapplyScratch));
            }
            else if (ModRuntime.VerboseLogging)
                PlaytestLog.Verbose("Puzzle", "reapply held none (" + skipped + " unchanged)");
            try { DoorNative.ReassertLockVisuals(); } catch (Exception ex) { WarnOnce("reapply-locks", ex.Message); }
            try { DoorSyncService.ReapplyHeldDoors(); } catch (Exception ex) { WarnOnce("reapply-doors", ex.Message); }
            try
            {
                var net = LanNetworkManager.Instance;
                if (net != null)
                    net.PickupSync.HideClaimed(null);
            }
            catch (Exception ex) { WarnOnce("reapply-pickups", ex.Message); }
        }

        /// <summary>
        /// Track the latest state per entry: progressed entries replace the held one, a falling edge drops it
        /// (ReapplyHeld must never re-snap a state the puzzle has since left).
        /// </summary>
        private void HoldIfProgressed(PuzzleStateEntry e, bool authored = false)
        {
            bool progressed = IsProgressed(e);
            PKey pk = Key(e);
            bool found = _held.ContainsKey(pk);
            bool durable = IsDurableMemoryType(e.Type) && e.WorldId != 0;
            // Default-unlocked doors are "progressed" from load: only hold one after it was seen locked, so a script
            // that relocks it (boss arena) is not undone by a held re-snap / durable memory after a reload.
            // A host-authored entry (ApplyEntry: dump / live apply / held re-snap) is always held: a client or late
            // joiner never reads the locked state itself, so the unlocked entry would otherwise be re-snapped away
            // on a chunk remount. The rule only guards local reads.
            bool hold = progressed;
            if (NeedsLockTransition(e.Type))
            {
                if (!progressed) _sawLocked.Add(pk);
                else if (!authored && !_sawLocked.Contains(pk) && !found && !_memory.ContainsKey(pk))
                    hold = false;
            }
            if (hold)
                _held[pk] = e;
            else if (!progressed && found)
                _held.Remove(pk);

            if (durable)
            {
                if (hold)
                    _memory[pk] = new DurableMemory { Scene = _sceneName, Entry = e };
                else if (!progressed)
                    _memory.Remove(pk);
            }
        }

        static bool NeedsLockTransition(PuzzleType t)
            => t == PuzzleType.DoorwaySimple || t == PuzzleType.SwingDoor || t == PuzzleType.DoorLockControl;

        /// <summary>c sits in one of the remounted rooms, or in no room (not chunk-managed).</summary>
        static bool InReapplyScope(Component c, List<Transform> rooms)
        {
            if (rooms == null) return true;
            Room own = null;
            try { own = PuzzleDomainUtil.FindInParents<Room>(c.gameObject); } catch (Exception e) { Guard.Swallow(e); }
            if (own == null) return true;
            Transform t = null;
            try { t = own.transform; } catch (Exception e) { Guard.Swallow(e); return true; }
            for (int i = 0; i < rooms.Count; i++)
                if (rooms[i] == t) return true;
            return false;
        }

        /// <summary>An apply could not finish natively: re-apply it next tick (bounded, see FlushRetries).</summary>
        internal static void RetryApply(PuzzleStateEntry e)
        {
            PKey k = Key(e);
            int n;
            _retryCount.TryGetValue(k, out n);
            if (n >= MaxApplyRetries)
            {
                WarnOnce("retry-" + e.Type, "apply still failing after " + MaxApplyRetries + " retries");
                return;
            }
            _retryCount[k] = n + 1;
            for (int i = 0; i < _retry.Count; i++)
            {
                if (!Key(_retry[i]).Equals(k)) continue;
                _retry[i] = e;
                return;
            }
            _retry.Add(e);
        }

        void FlushRetries()
        {
            if (_retry.Count == 0) return;
            _retryScratch.Clear();
            _retryScratch.AddRange(_retry);
            _retry.Clear();
            bool prevMutate = _mutateWorld;
            bool prevReapplying = _reapplying;
            _mutateWorld = true;
            _reapplying = true;
            NetGate.BeginApply();
            try
            {
                for (int i = 0; i < _retryScratch.Count; i++)
                {
                    var e = _retryScratch[i];
                    PKey k = Key(e);
                    PuzzleStateEntry cur;
                    // Superseded meanwhile: the newer entry was applied in its own right.
                    if (!_lastSent.TryGetValue(k, out cur) || !SameCells(cur, e)) { _retryCount.Remove(k); continue; }
                    ApplyEntry(e, cinematic: false);
                }
            }
            finally
            {
                _mutateWorld = prevMutate;
                _reapplying = prevReapplying;
                NetGate.EndApply();
            }
            // Keys that did not ask again succeeded: their budget resets.
            for (int i = 0; i < _retryScratch.Count; i++)
            {
                PKey k = Key(_retryScratch[i]);
                bool again = false;
                for (int j = 0; j < _retry.Count; j++)
                    if (Key(_retry[j]).Equals(k)) { again = true; break; }
                if (!again) _retryCount.Remove(k);
            }
        }

        public void Emit(PuzzleType type, ulong worldId, Component c)
        {
            if (c == null || NetGate.IsApplying) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected || !net.HasReadyPeers) return;
            if (net.UnicastActive) { _afterUnicast.Add(() => Emit(type, worldId, c)); return; }
            if (!MayEmit(net, type))
            {
                PuzzleStateEntry pe;
                if (Unseeded(net, type) && TryRead(type, worldId, c, out pe)) RecordPending(pe);
                return;
            }
            PuzzleStateEntry entry;
            if (!TryRead(type, worldId, c, out entry)) return;
            if (!ChangedOrFirst(ref entry, false)) return;
            StampOutgoing(ref entry, net.Role == NetworkRole.Host, false);
            HoldIfProgressed(entry);
            PlaytestLog.Event("Puzzle", "emit " + type + " id=" + worldId.ToString("X16"));
            _emitScratch[0] = entry;
            net.SendPuzzleState(_emitScratch, false);
        }

        /// <summary>Emit a pre-built entry (Adler EV projected end pose, etc.).</summary>
        public void EmitEntry(PuzzleStateEntry entry)
        {
            if (NetGate.IsApplying) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected || !net.HasReadyPeers) return;
            if (net.UnicastActive) { _afterUnicast.Add(() => EmitEntry(entry)); return; }
            if (!MayEmit(net, entry.Type))
            {
                if (Unseeded(net, entry.Type)) RecordPending(entry);
                return;
            }
            if (!ChangedOrFirst(ref entry, false)) return;
            StampOutgoing(ref entry, net.Role == NetworkRole.Host, false);
            HoldIfProgressed(entry);
            PlaytestLog.Event("Puzzle", "emit entry " + entry.Type
                + " id=" + unchecked((ulong)entry.WorldId).ToString("X16"));
            _emitScratch[0] = entry;
            net.SendPuzzleState(_emitScratch, false);
        }

        /// <summary>
        /// solved()/Open() are coroutines — native flags are still false when the
        /// Harmony postfix runs. Force Bool0 so peers apply the world result now.
        /// Prefer TryRead so Int/Bool payload is not zeroed (Reaktor dials, Pump a/b/c).
        /// </summary>
        public void EmitProgressed(PuzzleType type, ulong worldId)
        {
            if (worldId == 0 || NetGate.IsApplying) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected || !net.HasReadyPeers) return;
            if (net.UnicastActive) { _afterUnicast.Add(() => EmitProgressed(type, worldId)); return; }
            bool gated = !MayEmit(net, type);
            if (gated && !Unseeded(net, type)) return;
            PuzzleStateEntry entry;
            Component c = null;
            Dictionary<ulong, Component> map;
            if (_maps.TryGetValue(type, out map))
                map.TryGetValue(worldId, out c);
            if (c == null)
            {
                EnsureScanned();
                if (_maps.TryGetValue(type, out map))
                    map.TryGetValue(worldId, out c);
            }
            if (c != null && TryRead(type, worldId, c, out entry))
                entry.Bool0 = true;
            else
                entry = Mk(type, unchecked((long)worldId), true, false, false, 0, 0, 0, 0, 0f);
            if (gated)
            {
                RecordPending(entry, solvedEdge: true);
                return;
            }
            if (!ChangedOrFirst(ref entry, false)) return;
            StampOutgoing(ref entry, net.Role == NetworkRole.Host, false);
            HoldIfProgressed(entry);
            PlaytestLog.Event("Puzzle", "emit progressed " + type + " id=" + worldId.ToString("X16"));
            _emitScratch[0] = entry;
            net.SendPuzzleState(_emitScratch, false);
        }

        private T Get<T>(PuzzleType type, long worldId) where T : class
        {
            if (worldId == 0) return null;
            Dictionary<ulong, Component> map;
            if (!_maps.TryGetValue(type, out map))
            {
                EnsureScanned();
                if (!_maps.TryGetValue(type, out map)) return null;
            }
            Component c;
            if (!map.TryGetValue(unchecked((ulong)worldId), out c))
                return null;
            if (c == null)
            {
                map.Remove(unchecked((ulong)worldId));
                return null;
            }
            return c as T;
        }

        private void ApplyEntry(PuzzleStateEntry e, bool cinematic)
        {
            HoldIfProgressed(e, authored: true);
            NoteApplied(e, keepPending: !cinematic);
            bool prevLive = _liveEdge;
            _liveEdge = cinematic;
            try
            {
                PuzzleApplier apply;
                if (!Appliers.TryGetValue(e.Type, out apply)) return;
                // WorldId-keyed snaps: skip Apply* when the component is gone (avoids NRE).
                // Globals (WorldId 0 / static solved flags) apply without a map hit.
                if (e.WorldId != 0 && !IsGlobalApply(e.Type)
                    && Get<Component>(e.Type, e.WorldId) == null)
                    return;
                apply(this, e, cinematic);
            }
            catch (Exception ex)
            {
                PlaytestLog.Warn("Puzzle", "apply " + e.Type + ": " + ex.Message);
            }
            finally
            {
                _liveEdge = prevLive;
            }
        }

        static bool IsGlobalApply(PuzzleType type)
        {
            switch (type)
            {
                case PuzzleType.GlobalAlertStatus:
                case PuzzleType.RadioManagerState:
                case PuzzleType.MED_KeyGrid:
                case PuzzleType.ArianePhotoCode:
                case PuzzleType.UseItemMulti:
                    return true;
                default:
                    return false;
            }
        }

        public static void SnapUseItemWorld(UseItemInteraction x)
            => UseItemWorldSyncService.SnapUseItemWorld(x);

        internal static void DisableInteractions(Component root)
        {
            if (root == null) return;
            try
            {
                var all = root.GetComponentsInChildren<Interaction>(true);
                if (all == null) return;
                for (int i = 0; i < all.Length; i++)
                    DisableOne(all[i]);
            }
            catch (Exception e) { Guard.Swallow(e); }
        }

        internal static void DisableOne(Interaction it)
        {
            if (it == null) return;
            try
            {
                if (it.GetComponent<ItemPickup>() != null) return;
            }
            catch (Exception e) { Guard.Swallow(e); }
            try { it.triggered = true; } catch (Exception e) { Guard.Swallow(e); }
            try { it.enabled = false; } catch (Exception e) { Guard.Swallow(e); }
        }

        static string AnimKey(PuzzleType type, ulong id) => ((byte)type) + "_" + id.ToString("X");

        internal static bool AnimStarted(PuzzleType type, ulong id)
            => id != 0 && _worldAnimStarted.Contains(AnimKey(type, id));

        internal static void NoteAnimStarted(PuzzleType type, ulong id)
        {
            if (id != 0) _worldAnimStarted.Add(AnimKey(type, id));
        }

        internal static bool TryStartWorldAnim(PuzzleType type, GameObject go)
        {
            if (go == null) return false;
            bool active = false;
            try { active = go.activeInHierarchy; } catch { active = true; }
            if (!active) return false;
            ulong id = WorldId.FromGameObject(go);
            if (id == 0) return true;
            return _worldAnimStarted.Add(AnimKey(type, id));
        }

        internal static void RevealPickups(GameObject root)
            => PuzzleDoorFlagsSyncService.RevealPickups(root);

        internal static void UnlockDoorObject(GameObject door)
            => PuzzleDoorFlagsSyncService.UnlockDoorObject(door);

        internal static void TryUnlockDoors(GameObject go)
            => PuzzleDoorFlagsSyncService.TryUnlockDoors(go);
    }
}
