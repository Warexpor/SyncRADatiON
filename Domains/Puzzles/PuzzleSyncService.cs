// Host puzzle/interactive sync keyed by WorldId (never FindObjectsOfType index). Per-type knowledge (reader, applier,
// scan, emit / durable / merge / progressed rules) lives in PuzzleSpecs; this file is the coordinator.
using System;
using System.Collections.Generic;
using SyncRADation.Patches;
using SyncRADation.Sync;
using UnityEngine;
using static SyncRADation.Networking.PuzzleMerge;

namespace SyncRADation.Networking
{
    public sealed class PuzzleSyncService : IPuzzleDomainHost
    {
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

        // Allocation-free (type, WorldId) key.
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

        // An apply that threw or could not finish natively (tarot take/place): re-applied on the next tick while the
        // entry is still the current state, a few times at most, so a half-applied puzzle is never left as is (the
        // next poll would author that half state for everyone).
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
        // SpecFlags.LockTransition keys that have been seen locked (see HoldIfProgressed).
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

        // Warn-once sites: persistent for the process on purpose (one line per call site).
        private static readonly HashSet<string> _warnedSites = new HashSet<string>();
        private static readonly HashSet<(byte, ulong)> _worldAnimStarted = new HashSet<(byte, ulong)>();
        // Join/resync dumps are a settled snapshot. Replaying native transitions
        // (EventZone.Invoke, openDoor, delayedOpen, slaveInteraction.enable)
        // wakes leftover inactive puzzles and unseals flavor doors the host never touched.
        private static bool _mutateWorld = true;

        internal static bool MutateWorld => _mutateWorld;

        // True while ReapplyHeld / FlushRetries re-snap entries (never a live event). MutateWorld stays true there on
        // purpose (UseItem unlock latch, RevealPickups need it); the replay-prone consumers (FlipSwitch.Flip, pin /
        // ring / reaktor visual refresh, UseItem onSuccessful) read ReplayWorld instead.
        private static bool _reapplying;
        /// <summary>MutateWorld for a live apply, false during a join dump or a held re-snap.</summary>
        internal static bool ReplayWorld => _mutateWorld && !_reapplying;

        readonly CryoSyncService _cryo;
        readonly CodepadSyncService _codepad;

        public PuzzleSyncService()
        {
            _cryo = new CryoSyncService(this);
            _codepad = new CodepadSyncService(this);
            PuzzleSpecs.SelfCheck();
        }

        public void RefreshScene()
        {
            // No WorldLookup.Invalidate here: WorldRegistry.Rebuild (ModRuntime.OnSceneChanged) already invalidated
            // and rescanned for this load; dropping it again made every load scan the scene twice.
            EnvEmit.ClearOnce();
            BiodomeLockPatch.Reset();
            LibraryPcUpdatePatch.Reset();
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
            _sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? "";
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

        static PuzzleTypeSpec Spec(PuzzleType t) => PuzzleSpecs.Of(t);

        static bool IsCellMerge(PuzzleType t)
        {
            var s = Spec(t);
            return s != null && s.CellMerge;
        }

        static MergeKind KindOf(PuzzleType t)
        {
            var s = Spec(t);
            return s != null ? s.Merge : MergeKind.None;
        }

        private static bool IsProgressed(PuzzleStateEntry e)
        {
            var s = Spec(e.Type);
            return s != null && s.Progressed(e);
        }

        static bool ClientMayEmit(PuzzleType type)
        {
            var s = Spec(type);
            return s != null && s.ClientEmit;
        }

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
            if (!IsCellMerge(e.Type)) return;
            List<PuzzleMerge.OwnSent> l;
            if (!_ownSent.TryGetValue(key, out l)) { l = new List<PuzzleMerge.OwnSent>(PuzzleMerge.OwnSentKeep); _ownSent[key] = l; }
            PuzzleMerge.NoteOwnSent(l, e, Time.unscaledTime);
        }

        /// <summary>Client: e equals a state this client sent recently but has since moved past.</summary>
        bool IsOwnStaleEcho(PuzzleStateEntry e)
        {
            if (!IsCellMerge(e.Type)) return false;
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
            var kind = KindOf(e.Type);
            bool cellMerge = PuzzleMerge.IsCellMerge(kind);
            PuzzleStateEntry cur;
            bool hasCur = _lastSent.TryGetValue(key, out cur);
            int curSeq = CurSeq(key);
            int author;
            bool senderIsAuthor = _lastAuthor.TryGetValue(key, out author) && author == sender;
            // A client that authored the current state carries an older base Seq by design (its own edits are
            // not echoed back to it), so only somebody else's newer state makes its regress stale.
            // A merge-type edit carries only the cells its author changed, so an older base cannot regress anything.
            if (hasCur && e.Seq < curSeq && !senderIsAuthor && !cellMerge && !IsProgressed(e) && IsProgressed(cur))
            {
                PlaytestLog.Event("Puzzle", "drop stale " + e.Type + " id=" + unchecked((ulong)e.WorldId).ToString("X16")
                    + " seq=" + e.Seq + "<" + curSeq + " from p" + sender);
                e = cur;
                e.Seq = curSeq;
                e.Mask = 0;
                return false;
            }
            if (hasCur && kind == MergeKind.Grow)
                merged = PuzzleMerge.Grow(ref e, cur);
            else if (hasCur && e.Mask != 0 && cellMerge)
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
                e = MergeEdit(baseEntry, e, kind);
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

        /// <summary>PuzzleTypeSpec.Scan: register every T in the scene under its WorldId.</summary>
        internal void RegisterAll<T>(PuzzleType type) where T : Component
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
            var specs = PuzzleSpecs.All;
            for (int i = 0; i < specs.Length; i++)
                specs[i].Scan?.Invoke(this);
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
                if (_unicastFull && NetGate.HostRole)
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
                    deferred[i]();
            }
            EnvEmit.TickSoon();
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
                if (!NetGate.HostRole)
                    TickClient(net);
                else
                    TickHost(net, fullNow);
            }
            finally
            {
                HitchTrace.Cost("puzzle", (Time.realtimeSinceStartup - t0) * 1000f);
            }
        }

        /// <summary>
        /// Clients only emit real local puzzle/lock changes (SpecFlags.Emit). Buttons, event zones, combat flags and
        /// cutscenes are not world-authoring: echoing those runs native trigger()/EventScreen on the other Elster.
        /// </summary>
        void TickClient(LanNetworkManager net)
        {
            if (!_clientLive)
            {
                // No seed: default-true IsProgressed rules (elevator, mural, reactor…) would overwrite the host.
                // Wait for the host dump (ApplyPuzzleState primes + goes live). A dump that lands while this client is
                // still loading is dropped by ApplyPuzzleState, so the wait only runs on a loaded scene; the post-load
                // SnapshotRequest (OnSceneChanged) asks for a fresh dump and the gate arms on that one. Re-ask if it
                // has not arrived.
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
                        net.SessionHandlers.RequestWorldSnapshot();
                    }
                    return;
                }
                PrimeClientBaseline("no host dump within " + ClientDumpWait.ToString("0") + "s");
                return;
            }
            SendClientDiff(net);
        }

        void SendClientDiff(LanNetworkManager net)
        {
            _tickLocal.Clear();
            ReadAll(_tickLocal, false, clientFilter: true, activeOnly: true, firstIsBaseline: true, dampEcho: true);
            if (_tickLocal.Count == 0) return;
            StampOutgoing(_tickLocal, host: false, full: false);
            for (int i = 0; i < _tickLocal.Count; i++)
                HoldIfProgressed(_tickLocal[i]);
            if (ModRuntime.VerboseLogging)
                PlaytestLog.Verbose("Puzzle", "client diff " + _tickLocal.Count + " " + Describe(_tickLocal));
            net.PuzzleHandlers.SendPuzzleState(_tickLocal, false);
        }

        /// <summary>
        /// Read + send the pending local diff now instead of on the next 0.5 s tick (a dump or scene change is about
        /// to happen). Diff only: no full dump, no client seeding; nothing while unscanned or inside a unicast scope.
        /// </summary>
        public void FlushDiffNow()
        {
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected || !net.HasReadyPeers || net.UnicastActive) return;
            if (!Config.ModConfig.PuzzlesEnabled || !_scanned) return;
            float t0 = Time.realtimeSinceStartup;
            try
            {
                if (NetGate.HostRole) TickHost(net, false);
                else if (_clientLive) SendClientDiff(net);
                _sendTimer = 0f;
            }
            finally
            {
                HitchTrace.Cost("puzzle", (Time.realtimeSinceStartup - t0) * 1000f);
            }
        }

        void TickHost(LanNetworkManager net, bool full)
        {
            _tickEntries.Clear();
            ReadAll(_tickEntries, full, clientFilter: false, activeOnly: !full);
            // Only clear after an actual full dump. Clearing on a rate-limited diff tick
            // dropped join/resync full state when MinFullSendInterval still blocked.
            if (full)
                _needFullSend = false;
            StampOutgoing(_tickEntries, host: true, full: full);
            for (int i = 0; i < _tickEntries.Count; i++)
                HoldIfProgressed(_tickEntries[i]);
            if (_tickEntries.Count == 0) return;
            net.PuzzleHandlers.SendPuzzleState(_tickEntries, full);
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
                    net.PuzzleHandlers.SendPuzzleState(_tickEntries, true);
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
            => NetGate.HostRole || (_clientLive && ClientMayEmit(type));

        bool Unseeded(LanNetworkManager net, PuzzleType type)
            => !NetGate.HostRole && !_clientLive && ClientMayEmit(type);

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
                var spec = Spec(typeMap.Key);
                if (spec == null || !spec.ClientEmit || spec.FirstInstanceOnly) continue;
                foreach (var kvp in typeMap.Value)
                {
                    if (kvp.Value == null || !IsActiveInScene(kvp.Value)) continue;
                    PuzzleStateEntry entry;
                    if (TryRead(typeMap.Key, kvp.Key, kvp.Value, out entry)) RecordPending(entry, false, explicitEdit: false);
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
            if (net == null || NetGate.HostRole || _pendingFlush.Count == 0) { _pendingFlush.Clear(); return; }
            _pendingOut.Clear();
            for (int i = 0; i < _pendingFlush.Count; i++)
            {
                var f = _pendingFlush[i];
                var e = f.Latest;
                if (!IsProgressed(e)) continue;
                var kind = KindOf(e.Type);
                bool cellMerge = PuzzleMerge.IsCellMerge(kind);
                int mask = DiffMask(f.First, e, kind);
                if (f.HaveHostBase) mask &= DiffMask(f.HostBase, e, kind);
                if (mask == 0) continue;
                if (!f.Explicit && (mask & ~FloatMask) == 0) continue;
                if (f.HaveHostBase && IsProgressed(f.HostBase) && !cellMerge) continue;
                PKey key = Key(e);
                e.Seq = CurSeq(key);
                e.Mask = mask;
                PuzzleStateEntry local = e;
                if (f.HaveHostBase)
                {
                    if (cellMerge) local = MergeEdit(f.HostBase, e, kind);
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
            net.PuzzleHandlers.SendPuzzleState(_pendingOut, false);
            _pendingOut.Clear();
        }

        /// <param name="dampEcho">Client diff: hold back a state this client already sent within EchoWindow.</param>
        /// <param name="record">False = read-only snapshot (unicast dump): _lastSent is not touched.</param>
        private void ReadAll(List<PuzzleStateEntry> entries, bool full, bool clientFilter, bool activeOnly,
            bool firstIsBaseline = false, bool dampEcho = false, bool record = true)
        {
            foreach (var typeMap in _maps)
            {
                var type = typeMap.Key;
                var spec = Spec(type);
                if (spec == null || (clientFilter && !spec.ClientEmit)) continue;
                foreach (var kvp in typeMap.Value)
                {
                    if (kvp.Value == null) continue;
                    if (activeOnly && !IsActiveInScene(kvp.Value)) continue;
                    PuzzleStateEntry entry;
                    if (TryRead(type, kvp.Key, kvp.Value, out entry))
                        Collect(entries, entry, full, firstIsBaseline, dampEcho, record);
                    if (spec.FirstInstanceOnly) break;
                }
            }

            // WorldId-0 globals (alarm, radio module, key grid, photo code).
            var globals = PuzzleSpecs.Globals;
            for (int i = 0; i < globals.Length; i++)
            {
                var g = globals[i];
                if (clientFilter && !g.ClientEmit) continue;
                PuzzleStateEntry entry;
                try { entry = g.ReadGlobal(); }
                catch (Exception e) { Guard.Swallow(e); continue; }
                if (clientFilter && g.ClientSendIf != null && !g.ClientSendIf(entry)) continue;
                Collect(entries, entry, full, firstIsBaseline, dampEcho, record);
            }
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

        /// <summary>Liveness probe: a component destroyed mid-scene throws on access instead of comparing null.</summary>
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

        /// <summary>
        /// Test pilot digest: every registered puzzle / lock and the WorldId-0 globals as one line each, read through
        /// the same spec readers a full dump uses. Read only: nothing recorded as sent.
        /// </summary>
        public void PilotDigest(List<string> into)
        {
            EnsureScanned();
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var typeMap in _maps)
            {
                var spec = Spec(typeMap.Key);
                foreach (var kvp in typeMap.Value)
                {
                    if (kvp.Value == null) continue;
                    PuzzleStateEntry e;
                    if (!TryRead(typeMap.Key, kvp.Key, kvp.Value, out e)) continue;
                    // A component in a room chunk this peer never woke holds its serialized value until its Start / the
                    // held re-snap on room entry: marked, so the diff reports it apart from awake differences.
                    bool awake = false;
                    try { awake = kvp.Value.gameObject.activeInHierarchy; } catch (Exception ex) { Guard.Swallow(ex); }
                    into.Add(PilotLine(e, ci) + (awake ? "" : " asleep"));
                    if (spec != null && spec.FirstInstanceOnly) break;
                }
            }
            var globals = PuzzleSpecs.Globals;
            for (int i = 0; i < globals.Length; i++)
            {
                PuzzleStateEntry e;
                try { e = globals[i].ReadGlobal(); }
                catch (Exception ex) { Guard.Swallow(ex); continue; }
                into.Add(PilotLine(e, ci));
            }
        }

        static string PilotLine(PuzzleStateEntry e, System.Globalization.CultureInfo ci) =>
            "puzzle " + e.Type + ":" + unchecked((ulong)e.WorldId).ToString("X16") + " b=" + (e.Bool0 ? 1 : 0) + (e.Bool1 ? 1 : 0) + (e.Bool2 ? 1 : 0)
            + " i=" + e.Int0 + "," + e.Int1 + "," + e.Int2 + "," + e.Int3
            + " f=" + e.Float0.ToString("0.0", ci) + "," + e.Float1.ToString("0.0", ci);

        /// <summary>The one try around a read: a read that throws yields no entry this tick (never a half-read one).</summary>
        private bool TryRead(PuzzleType type, ulong id, Component c, out PuzzleStateEntry entry)
        {
            entry = default;
            var spec = Spec(type);
            if (spec == null || spec.Read == null) return false;
            try { return spec.Read(c, unchecked((long)id), out entry); }
            catch (Exception e) { Guard.Swallow("Puzzle.read." + type, e); return false; }
        }

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
                    if (firstIsBaseline || NetGate.ClientRole)
                        entry.Bool1 = prev.Bool1;
                    else if (entry.Bool1 != prev.Bool1 && entry.Bool0 == prev.Bool0 && entry.Bool2 == prev.Bool2)
                        return false;
                }
                if (SameCells(prev, entry))
                    return false;
                entry.Mask = DiffMask(prev, entry, KindOf(entry.Type));
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
            if (!keepPending && net != null && !NetGate.HostRole && !_clientLive)
                _pending.Remove(key);
            if (net != null && !NetGate.HostRole && entry.Seq != 0 && entry.Seq > CurSeq(key))
                _seq[key] = entry.Seq;
        }

        public void ApplyPuzzleState(PuzzleStateMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (msg.Entries == null || msg.Entries.Length == 0) return;
            if (net != null && msg.SenderPlayerId == net.LocalPlayerId)
                return;
            bool isHost = net != null && NetGate.HostRole;
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
                    var spec = Spec(e.Type);
                    if (spec != null && spec.HostAcceptIf != null && !spec.HostAcceptIf(e))
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
                try { net.PuzzleHandlers.SendPuzzleState(_resync, false); }
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
                    // Door visuals are a separate pass: a failure there must not drop the relay below.
                    try { DoorNative.ReassertLockVisuals(); }
                    catch (Exception ex) { WarnOnce("reassert-locks", ex.Message); }
                }
            }

            if (isHost)
            {
                if (_relayExcept.Count > 0)
                    net.PuzzleHandlers.SendPuzzleState(_relayExcept, false, msg.SenderPlayerId);
                if (_relayAll.Count > 0)
                    net.PuzzleHandlers.SendPuzzleState(_relayAll, false);
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
            Transform t = room != null ? room.transform : null;
            if (t == null) { _pendingReapply = true; return; }
            if (!_reapplyRooms.Contains(t)) _reapplyRooms.Add(t);
        }

        static bool HeldSolved(PuzzleStateEntry e)
        {
            var s = Spec(e.Type);
            return s != null && s.HeldSolved(e);
        }

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
        /// and types never stand in for each other (an unmatched pattern lock would otherwise solve every cryo pad).
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
                ReapplyDoorsAndPickups(false);
                return;
            }
            // Snapshot first: ApplyEntry → HoldIfProgressed writes _held while this runs.
            _reapplyScratch.Clear();
            foreach (var kvp in _held)
                _reapplyScratch.Add(kvp.Value);
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
            ReapplyDoorsAndPickups(true);
        }

        /// <summary>The door / pickup passes after a re-snap: separate domains, each must run even if another throws.</summary>
        static void ReapplyDoorsAndPickups(bool full)
        {
            if (full)
            {
                try { DoorNative.ReassertLockVisuals(); } catch (Exception ex) { WarnOnce("reapply-locks", ex.Message); }
            }
            try { DoorSyncService.ReapplyHeldDoors(); } catch (Exception ex) { WarnOnce("reapply-doors", ex.Message); }
            if (!full) return;
            try { LanNetworkManager.Instance?.PickupSync.HideClaimed(null); }
            catch (Exception ex) { WarnOnce("reapply-pickups", ex.Message); }
        }

        /// <summary>
        /// Track the latest state per entry: progressed entries replace the held one, a falling edge drops it
        /// (ReapplyHeld must never re-snap a state the puzzle has since left).
        /// </summary>
        private void HoldIfProgressed(PuzzleStateEntry e, bool authored = false)
        {
            var spec = Spec(e.Type);
            if (spec == null) return;
            bool progressed = spec.Progressed(e);
            PKey pk = Key(e);
            bool found = _held.ContainsKey(pk);
            bool durable = spec.Durable && e.WorldId != 0;
            // Default-unlocked doors are "progressed" from load: only hold one after it was seen locked, so a script
            // that relocks it (boss arena) is not undone by a held re-snap / durable memory after a reload.
            // A host-authored entry (ApplyEntry: dump / live apply / held re-snap) is always held: a client or late
            // joiner never reads the locked state itself, so the unlocked entry would otherwise be re-snapped away
            // on a chunk remount. The rule only guards local reads.
            bool hold = progressed;
            if (spec.LockTransition)
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

        /// <summary>c sits in one of the remounted rooms, or in no room (not chunk-managed).</summary>
        static bool InReapplyScope(Component c, List<Transform> rooms)
        {
            if (rooms == null) return true;
            Room own = PuzzleDomainUtil.FindInParents<Room>(c.gameObject);
            if (own == null) return true;
            Transform t = own.transform;
            for (int i = 0; i < rooms.Count; i++)
                if (rooms[i] == t) return true;
            return false;
        }

        /// <summary>An apply could not finish (threw / a native step failed): re-apply it next tick (bounded, see FlushRetries).</summary>
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
            SendEmit(net, entry, "emit ");
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
            SendEmit(net, entry, "emit entry ");
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
                entry = PuzzleDomainUtil.Mk(type, unchecked((long)worldId), true, false, false, 0, 0, 0, 0, 0f);
            if (gated)
            {
                RecordPending(entry, solvedEdge: true);
                return;
            }
            SendEmit(net, entry, "emit progressed ");
        }

        void SendEmit(LanNetworkManager net, PuzzleStateEntry entry, string what)
        {
            if (!ChangedOrFirst(ref entry, false)) return;
            StampOutgoing(ref entry, NetGate.HostRole, false);
            HoldIfProgressed(entry);
            PlaytestLog.Event("Puzzle", what + entry.Type + " id=" + unchecked((ulong)entry.WorldId).ToString("X16"));
            _emitScratch[0] = entry;
            net.PuzzleHandlers.SendPuzzleState(_emitScratch, false);
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

        /// <summary>The one try around an apply: a throw marks the entry for a bounded re-apply (RetryApply).</summary>
        private void ApplyEntry(PuzzleStateEntry e, bool cinematic)
        {
            HoldIfProgressed(e, authored: true);
            NoteApplied(e, keepPending: !cinematic);
            var spec = Spec(e.Type);
            if (spec == null || spec.Apply == null) return;
            // WorldId-keyed snaps: skip when the component is gone. Globals (WorldId 0 / static flags) apply without
            // a map hit.
            Component c = Get<Component>(e.Type, e.WorldId);
            if (e.WorldId != 0 && !spec.GlobalApply && c == null) return;
            bool prevLive = _liveEdge;
            _liveEdge = cinematic;
            try { spec.Apply(c, e); }
            catch (Exception ex)
            {
                PlaytestLog.Warn("Puzzle", "apply " + e.Type + ": " + ex.Message);
                RetryApply(e);
            }
            finally
            {
                _liveEdge = prevLive;
            }
        }

        public static void SnapUseItemWorld(UseItemInteraction x)
            => UseItemWorldSyncService.SnapUseItemWorld(x);

        internal static void DisableInteractions(Component root)
        {
            if (root == null) return;
            var all = root.GetComponentsInChildren<Interaction>(true);
            if (all == null) return;
            for (int i = 0; i < all.Length; i++)
                DisableOne(all[i]);
        }

        internal static void DisableOne(Interaction it)
        {
            if (it == null || it.GetComponent<ItemPickup>() != null) return;
            it.triggered = true;
            it.enabled = false;
        }

        internal static bool AnimStarted(PuzzleType type, ulong id)
            => id != 0 && _worldAnimStarted.Contains(((byte)type, id));

        internal static void NoteAnimStarted(PuzzleType type, ulong id)
        {
            if (id != 0) _worldAnimStarted.Add(((byte)type, id));
        }

        /// <summary>First live start of a native world animation (drain, valve, hatch) for this object this scene.</summary>
        internal static bool TryStartWorldAnim(PuzzleType type, GameObject go)
        {
            if (go == null || !go.activeInHierarchy) return false;
            ulong id = WorldId.FromGameObject(go);
            if (id == 0) return true;
            return _worldAnimStarted.Add(((byte)type, id));
        }

        internal static void RevealPickups(GameObject root)
            => PuzzleDoorFlagsSyncService.RevealPickups(root);

        internal static void UnlockDoorObject(GameObject door)
            => PuzzleDoorFlagsSyncService.UnlockDoorObject(door);

        internal static void TryUnlockDoors(GameObject go)
            => PuzzleDoorFlagsSyncService.TryUnlockDoors(go);
    }
}
