// Host puzzle/interactive sync keyed by WorldId (never FindObjectsOfType index).
using System;
using System.Collections.Generic;
using SyncRADation.Patches;
using SyncRADation.Sync;
using UnityEngine;

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
        private readonly List<PuzzleStateEntry> _held
            = new List<PuzzleStateEntry>(16);

        // Reused each Tick to avoid List allocs on the puzzle poll path.
        private readonly List<PuzzleStateEntry> _tickSeed = new List<PuzzleStateEntry>(64);
        private readonly List<PuzzleStateEntry> _tickProgressed = new List<PuzzleStateEntry>(8);
        private readonly List<PuzzleStateEntry> _tickLocal = new List<PuzzleStateEntry>(16);
        private readonly List<PuzzleStateEntry> _tickEntries = new List<PuzzleStateEntry>(64);
        private readonly PuzzleStateEntry[] _emitScratch = new PuzzleStateEntry[1];

        private bool _pendingReapply;

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

        private static bool _reapplying;
        /// <summary>
        /// True while ReapplyHeld re-snaps held entries (never a live event). MutateWorld stays true there on purpose
        /// (UseItem NoteRemoteUnlock / unlock latch, RevealPickups need it); the replay-prone consumers (FlipSwitch.Flip,
        /// pin / ring / reaktor visual refresh) read <see cref="ReplayWorld"/> instead.
        /// </summary>
        internal static bool Reapplying => _reapplying;
        /// <summary>MutateWorld for a live or join apply, false during a held re-snap.</summary>
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
            WorldLookup.Invalidate();
            try { SyncRADation.Patches.EnvEmit.ClearOnce(); } catch (Exception e) { Guard.Swallow(e); }
            _scanned = false;
            _needFullSend = true;
            _lastSent.Clear();
            _clientEcho.Clear();
            _echoLogged.Clear();
            _held.Clear();
            _worldAnimStarted.Clear();
            _maps.Clear();
            _seq.Clear();
            _lastAuthor.Clear();
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
                _held.Add(kvp.Value.Entry);
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

        // Types where the host merges only the cells a client actually changed (see PuzzleStateEntry.Mask).
        static bool IsMergeType(PuzzleType t)
        {
            switch (t)
            {
                case PuzzleType.DialLock:
                case PuzzleType.MED_Incinerator:
                case PuzzleType.RES_Shrine:
                case PuzzleType.PEN_Reaktor:
                case PuzzleType.ROT_Mural:
                case PuzzleType.ROT_RadioAlignment:
                case PuzzleType.DET_RadioCodeLock:
                    return true;
                default:
                    return false;
            }
        }

        // Mural packs two 16-bit moons per Int: merge at half granularity.
        static bool IsHalfMergeType(PuzzleType t) => t == PuzzleType.ROT_Mural;

        // Reaktor packs four 3-bit rod positions in Int0: mask bits 16..19 = rod r changed. The selection
        // cursor (PEN_Reaktor.current) is per player and not on the wire (Int1 stays 0).
        const int ReaktorRodBits = 4;
        static bool IsRodMergeType(PuzzleType t) => t == PuzzleType.PEN_Reaktor;

        static int DiffMask(PuzzleStateEntry a, PuzzleStateEntry b)
        {
            int m = 0;
            if (a.Bool0 != b.Bool0) m |= 1;
            if (a.Bool1 != b.Bool1) m |= 2;
            if (a.Bool2 != b.Bool2) m |= 4;
            if (a.Int0 != b.Int0) m |= 8;
            if (a.Int1 != b.Int1) m |= 16;
            if (a.Int2 != b.Int2) m |= 32;
            if (a.Int3 != b.Int3) m |= 64;
            if (!Mathf.Approximately(a.Float0, b.Float0)) m |= 128;
            if (!Mathf.Approximately(a.Float1, b.Float1)) m |= 256;
            if (IsRodMergeType(b.Type))
            {
                int x = a.Int0 ^ b.Int0;
                for (int r = 0; r < ReaktorRodBits; r++)
                    if (((x >> (r * 3)) & 7) != 0) m |= 1 << (16 + r);
            }
            if (IsHalfMergeType(b.Type))
            {
                for (int k = 0; k < 4; k++)
                {
                    int x = GetInt(a, k) ^ GetInt(b, k);
                    if ((x & 0xFFFF) != 0) m |= 1 << (16 + 2 * k);
                    if ((x & unchecked((int)0xFFFF0000)) != 0) m |= 1 << (17 + 2 * k);
                }
            }
            return m;
        }

        static int GetInt(PuzzleStateEntry e, int k)
        {
            switch (k)
            {
                case 0: return e.Int0;
                case 1: return e.Int1;
                case 2: return e.Int2;
                default: return e.Int3;
            }
        }

        static void SetInt(ref PuzzleStateEntry e, int k, int v)
        {
            switch (k)
            {
                case 0: e.Int0 = v; break;
                case 1: e.Int1 = v; break;
                case 2: e.Int2 = v; break;
                default: e.Int3 = v; break;
            }
        }

        /// <summary>Overlay only the cells named by inc.Mask onto the host's current state.</summary>
        static PuzzleStateEntry MergeEdit(PuzzleStateEntry cur, PuzzleStateEntry inc)
        {
            int m = inc.Mask;
            var r = cur;
            if ((m & 1) != 0) r.Bool0 = inc.Bool0;
            if ((m & 2) != 0) r.Bool1 = inc.Bool1;
            if ((m & 4) != 0) r.Bool2 = inc.Bool2;
            bool half = IsHalfMergeType(inc.Type);
            bool rods = IsRodMergeType(inc.Type);
            for (int k = 0; k < 4; k++)
            {
                if ((m & (8 << k)) == 0) continue;
                if (rods && k == 0)
                {
                    int rv = cur.Int0;
                    for (int rr = 0; rr < ReaktorRodBits; rr++)
                    {
                        if ((m & (1 << (16 + rr))) == 0) continue;
                        int sh = rr * 3;
                        rv = (rv & ~(7 << sh)) | (inc.Int0 & (7 << sh));
                    }
                    r.Int0 = rv;
                    continue;
                }
                if (!half)
                {
                    SetInt(ref r, k, GetInt(inc, k));
                    continue;
                }
                int v = GetInt(cur, k);
                int n = GetInt(inc, k);
                if ((m & (1 << (16 + 2 * k))) != 0) v = (v & unchecked((int)0xFFFF0000)) | (n & 0xFFFF);
                if ((m & (1 << (17 + 2 * k))) != 0) v = (v & 0xFFFF) | (n & unchecked((int)0xFFFF0000));
                SetInt(ref r, k, v);
            }
            if ((m & 128) != 0) r.Float0 = inc.Float0;
            if ((m & 256) != 0) r.Float1 = inc.Float1;
            r.Seq = inc.Seq;
            r.Mask = 0;
            return r;
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
            if (hasCur && e.Seq < curSeq && !senderIsAuthor && !IsProgressed(e) && IsProgressed(cur))
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
            StorageLidSyncService.EnsureOpenField();

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

            if (_pendingReapply)
            {
                _pendingReapply = false;
                ReapplyHeld();
            }

            _sendTimer += Mathf.Min(Time.deltaTime, 0.1f);
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
                ReadAll(_tickLocal, false, clientFilter: true, activeOnly: true, firstIsBaseline: true);
                DampClientEcho(_tickLocal);
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

        private void ReadAll(List<PuzzleStateEntry> entries, bool full, bool clientFilter, bool activeOnly,
            bool firstIsBaseline = false)
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
                    if (ChangedOrFirst(ref entry, full, firstIsBaseline)) entries.Add(entry);
                }
            }

            // Globals (WorldId = 0). Alarm stays host-only (client poll would clobber).
            // RadioManagerState: client may emit moduleInstalled=true (latch on apply).
            // KeyGrid / ArianePhotoCode are client-emittable (static solved/code reverse arrow).
            if (!clientFilter)
            {
                var alarm = EnemySyncService.ReadGlobalAlert();
                if (ChangedOrFirst(ref alarm, full, firstIsBaseline)) entries.Add(alarm);
            }
            if (!clientFilter || ClientMayEmit(PuzzleType.RadioManagerState))
            {
                var radio = RadioPuzzleSyncService.ReadManagerState();
                // Client must not emit false — would race host latch before acquire.
                if (!(clientFilter && (radio.Int0 & 1) == 0)
                    && ChangedOrFirst(ref radio, full, firstIsBaseline))
                    entries.Add(radio);
            }
            if (!clientFilter || ClientMayEmit(PuzzleType.MED_KeyGrid))
            {
                var keyGrid = ResidencyPuzzleSyncService.ReadKeyGridGlobal();
                if (ChangedOrFirst(ref keyGrid, full, firstIsBaseline)) entries.Add(keyGrid);
            }
            if (!clientFilter || ClientMayEmit(PuzzleType.ArianePhotoCode))
            {
                var ariane = ResidencyPuzzleSyncService.ReadArianePhotoCodeGlobal();
                if (ChangedOrFirst(ref ariane, full, firstIsBaseline)) entries.Add(ariane);
            }
        }

        struct EchoRec { public PuzzleStateEntry Entry; public float At; }
        readonly Dictionary<PKey, EchoRec> _clientEcho = new Dictionary<PKey, EchoRec>();
        readonly HashSet<PKey> _echoLogged = new HashSet<PKey>();
        const float EchoWindow = 10f;

        /// <summary>
        /// A state the client cannot converge on (its read differs from what applying the host's entry produces,
        /// e.g. a door plate driven by local room traversal) ping-ponged forever: client re-reads X, host applies
        /// X, re-reads Y, client applies Y, re-reads X... (PEN_Wreck, one InteractiveLockSingle ~1/s). The same
        /// local state is sent once per <see cref="EchoWindow"/>; a different state always goes out at once.
        /// </summary>
        void DampClientEcho(List<PuzzleStateEntry> entries)
        {
            float now = Time.unscaledTime;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var e = entries[i];
                var key = Key(e);
                EchoRec rec;
                if (_clientEcho.TryGetValue(key, out rec) && SameCells(rec.Entry, e) && now - rec.At < EchoWindow)
                {
                    entries.RemoveAt(i);
                    if (_echoLogged.Add(key))
                        PlaytestLog.Event("Puzzle", "echo damp " + e.Type + " " + unchecked((ulong)e.WorldId).ToString("X16")
                            + " (host keeps a different state)");
                    continue;
                }
                _clientEcho[key] = new EchoRec { Entry = e, At = now };
            }
        }

        static bool SameCells(PuzzleStateEntry a, PuzzleStateEntry b)
        {
            return a.Bool0 == b.Bool0 && a.Bool1 == b.Bool1 && a.Bool2 == b.Bool2
                && a.Int0 == b.Int0 && a.Int1 == b.Int1 && a.Int2 == b.Int2 && a.Int3 == b.Int3
                && Mathf.Approximately(a.Float0, b.Float0) && Mathf.Approximately(a.Float1, b.Float1);
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
        private bool ChangedOrFirst(ref PuzzleStateEntry entry, bool fullRefresh, bool firstIsBaseline = false)
        {
            PKey key = Key(entry);
            if (fullRefresh)
            {
                _lastSent[key] = entry;
                return true;
            }
            PuzzleStateEntry prev;
            if (_lastSent.TryGetValue(key, out prev))
            {
                if (prev.Bool0 == entry.Bool0 && prev.Bool1 == entry.Bool1 && prev.Bool2 == entry.Bool2
                    && prev.Int0 == entry.Int0 && prev.Int1 == entry.Int1 && prev.Int2 == entry.Int2 && prev.Int3 == entry.Int3
                    && Mathf.Approximately(prev.Float0, entry.Float0)
                    && Mathf.Approximately(prev.Float1, entry.Float1))
                    return false;
                entry.Mask = DiffMask(prev, entry);
            }
            else if (firstIsBaseline)
            {
                _lastSent[key] = entry;
                return false;
            }
            _lastSent[key] = entry;
            return true;
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

        private static string Describe(PuzzleStateEntry[] entries)
        {
            if (entries == null) return "";
            return Describe((IList<PuzzleStateEntry>)entries);
        }

        private void NoteApplied(PuzzleStateEntry entry, bool keepPending)
        {
            PKey key = Key(entry);
            _lastSent[key] = entry;
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
                try
                {
                    for (int i = 0; i < _applyScratch.Count; i++)
                        ApplyEntry(_applyScratch[i], cinematic);
                }
                finally
                {
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

        bool IsHeld(PuzzleType type, ulong worldId)
        {
            if (worldId == 0) return false;
            for (int i = 0; i < _held.Count; i++)
            {
                if (_held[i].Type == type && _held[i].WorldId == unchecked((long)worldId) && IsProgressed(_held[i]))
                    return true;
            }
            return false;
        }

        bool HeldUnmatched(PuzzleType type)
        {
            for (int i = 0; i < _held.Count; i++)
            {
                if (_held[i].Type != type || !IsProgressed(_held[i])) continue;
                if (Get<Component>(type, _held[i].WorldId) == null)
                    return true;
            }
            return false;
        }

        void RemapHeld(PuzzleType type, ulong newId)
        {
            if (newId == 0) return;
            for (int i = 0; i < _held.Count; i++)
            {
                if (_held[i].Type != type || !IsProgressed(_held[i])) continue;
                if (Get<Component>(type, _held[i].WorldId) != null) continue;
                var e = _held[i];
                e.WorldId = unchecked((long)newId);
                _held[i] = e;
                PlaytestLog.Event("Puzzle", "remap " + type + " -> " + newId.ToString("X16"));
                return;
            }
        }

        bool CryoFamilyHeldUnmatched()
        {
            return HeldUnmatched(PuzzleType.PEN_Cryo)
                || HeldUnmatched(PuzzleType.CryoDoorLock)
                || HeldUnmatched(PuzzleType.PEN_Codepad)
                || HeldUnmatched(PuzzleType.PatternLock);
        }

        bool IPuzzleDomainHost.IsHeld(PuzzleType type, ulong worldId) => IsHeld(type, worldId);
        bool IPuzzleDomainHost.HeldUnmatched(PuzzleType type) => HeldUnmatched(type);
        void IPuzzleDomainHost.RemapHeld(PuzzleType type, ulong newId) => RemapHeld(type, newId);
        bool IPuzzleDomainHost.CryoFamilyHeldUnmatched() => CryoFamilyHeldUnmatched();

        /// <summary>Native OnEnable re-enables pad/open. Shut them in the same callback if already solved.</summary>
        public void HandlePenCryoEnabled(PEN_Cryo x) => _cryo.HandlePenCryoEnabled(x);

        public void HandleCryoLockEnabled(CryoDoorLock c) => _cryo.HandleCryoLockEnabled(c);

        public void HandleCodepadEnabled(PEN_Codepad pad) => _codepad.HandleCodepadEnabled(pad);

        public void HandlePatternLockEnabled(LAB_PatternLock pad) => _codepad.HandlePatternLockEnabled(pad);

        public bool ShouldKillOverlay(Interaction it) => _codepad.ShouldKillOverlay(it);

        public void ReapplyHeld()
        {
            if (_held.Count == 0)
            {
                try { DoorSyncService.ReapplyHeldDoors(); } catch (Exception ex) { WarnOnce("reapply-doors", ex.Message); }
                return;
            }
            PlaytestLog.Event("Puzzle", "reapply held " + _held.Count + " " + Describe(_held));
            // MutateWorld stays true on purpose: UseItem (per-player airlock card latch) and RevealPickups need it.
            // The replay-prone consumers (FlipSwitch.Flip, DET_ServiceLock pins, PEN_Reaktor positions, LAB_Rings
            // fingers) read ReplayWorld, which is false here, so a reapply never re-runs a rising-edge consequence.
            bool prevMutate = _mutateWorld;
            bool prevReapplying = _reapplying;
            _mutateWorld = true;
            _reapplying = true;
            NetGate.BeginApply();
            try
            {
                for (int i = 0; i < _held.Count; i++)
                {
                    var e = _held[i];
                    var c = Get<Component>(e.Type, e.WorldId);
                    if (c != null && !IsActiveInScene(c))
                        continue;
                    ApplyEntry(e, cinematic: false);
                }
            }
            finally
            {
                _mutateWorld = prevMutate;
                _reapplying = prevReapplying;
                NetGate.EndApply();
            }
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
            int found = -1;
            for (int i = 0; i < _held.Count; i++)
            {
                if (_held[i].Type == e.Type && _held[i].WorldId == e.WorldId)
                {
                    found = i;
                    break;
                }
            }
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
                else if (!authored && !_sawLocked.Contains(pk) && found < 0 && !_memory.ContainsKey(pk))
                    hold = false;
            }
            if (hold)
            {
                if (found >= 0) _held[found] = e;
                else _held.Add(e);
            }
            else if (!progressed && found >= 0)
                _held.RemoveAt(found);

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

        public void Emit(PuzzleType type, ulong worldId, Component c)
        {
            if (c == null || NetGate.IsApplying) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected || !net.HasReadyPeers) return;
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

        internal static bool PerPlayerUse(UseItemInteraction x)
            => UseItemWorldSyncService.PerPlayerUse(x);

        public static void SnapUseItemWorld(UseItemInteraction x)
            => UseItemWorldSyncService.SnapUseItemWorld(x);

        public static void ApplyCodepadConsequences(PEN_Codepad pad)
            => CodepadSyncService.ApplyCodepadConsequences(pad);

        // Domain peels — keep façade names for callers.
        internal static void SnapCryoLock(CryoDoorLock c, bool playAnim = false)
            => CryoSyncService.SnapCryoLock(c, playAnim);

        internal static void SnapPenCryo(PEN_Cryo x, bool playOpen)
            => CryoSyncService.SnapPenCryo(x, playOpen);

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
