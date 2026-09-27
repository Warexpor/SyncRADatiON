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

        // Type → (WorldId → component)
        private readonly Dictionary<PuzzleType, Dictionary<ulong, Component>> _maps
            = new Dictionary<PuzzleType, Dictionary<ulong, Component>>();

        private readonly Dictionary<string, PuzzleStateEntry> _lastSent
            = new Dictionary<string, PuzzleStateEntry>();

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
        private static readonly HashSet<string> _worldAnimStarted = new HashSet<string>();
        // Join/resync dumps are a settled snapshot. Replaying native transitions
        // (EventZone.Invoke, openDoor, delayedOpen, slaveInteraction.enable)
        // wakes leftover inactive puzzles and unseals flavor doors the host never touched.
        private static bool _mutateWorld = true;

        internal static bool MutateWorld => _mutateWorld;

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
            try { SyncRADation.Patches.EnvEmit.ClearOnce(); } catch { }
            _scanned = false;
            _needFullSend = true;
            _lastSent.Clear();
            _held.Clear();
            _worldAnimStarted.Clear();
            _maps.Clear();
            ModRuntime.Log?.Msg("[PuzzleSync] Scene refreshed");
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

        public void Reset()
        {
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
                if (_needFullSend)
                {
                    _tickSeed.Clear();
                    ReadAll(_tickSeed, true, clientFilter: true, activeOnly: false);
                    _needFullSend = false;
                    _tickProgressed.Clear();
                    for (int i = 0; i < _tickSeed.Count; i++)
                    {
                        if (IsProgressed(_tickSeed[i]))
                            _tickProgressed.Add(_tickSeed[i]);
                    }
                    if (_tickProgressed.Count > 0)
                    {
                        for (int i = 0; i < _tickProgressed.Count; i++)
                            HoldIfProgressed(_tickProgressed[i]);
                        PlaytestLog.Event("Puzzle", "client seed " + _tickProgressed.Count
                            + " " + Describe(_tickProgressed));
                        net.SendPuzzleState(_tickProgressed, false);
                    }
                    return;
                }
                _tickLocal.Clear();
                ReadAll(_tickLocal, false, clientFilter: true, activeOnly: true);
                if (_tickLocal.Count > 0)
                {
                    for (int i = 0; i < _tickLocal.Count; i++)
                        HoldIfProgressed(_tickLocal[i]);
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

        private void ReadAll(List<PuzzleStateEntry> entries, bool full, bool clientFilter, bool activeOnly)
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
                    if (ChangedOrFirst(entry, full)) entries.Add(entry);
                }
            }

            // Globals (WorldId = 0). Alarm stays host-only (client poll would clobber).
            // RadioManagerState: client may emit moduleInstalled=true (latch on apply).
            // KeyGrid / ArianePhotoCode are client-emittable (static solved/code reverse arrow).
            if (!clientFilter)
            {
                var alarm = EnemySyncService.ReadGlobalAlert();
                if (ChangedOrFirst(alarm, full)) entries.Add(alarm);
            }
            if (!clientFilter || ClientMayEmit(PuzzleType.RadioManagerState))
            {
                var radio = RadioPuzzleSyncService.ReadManagerState();
                // Client must not emit false — would race host latch before acquire.
                if (!(clientFilter && (radio.Int0 & 1) == 0)
                    && ChangedOrFirst(radio, full))
                    entries.Add(radio);
            }
            if (!clientFilter || ClientMayEmit(PuzzleType.MED_KeyGrid))
            {
                var keyGrid = ResidencyPuzzleSyncService.ReadKeyGridGlobal();
                if (ChangedOrFirst(keyGrid, full)) entries.Add(keyGrid);
            }
            if (!clientFilter || ClientMayEmit(PuzzleType.ArianePhotoCode))
            {
                var ariane = ResidencyPuzzleSyncService.ReadArianePhotoCodeGlobal();
                if (ChangedOrFirst(ariane, full)) entries.Add(ariane);
            }
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
            catch { }
            return false;
        }

        private static PuzzleStateEntry Mk(PuzzleType type, long worldId, bool b0, bool b1, bool b2, int i0, int i1, int i2, int i3, float f0)
            => PuzzleDomainUtil.Mk(type, worldId, b0, b1, b2, i0, i1, i2, i3, f0);

        private bool ChangedOrFirst(PuzzleStateEntry entry, bool fullRefresh)
        {
            string key = (byte)entry.Type + "_" + entry.WorldId.ToString("X");
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

        private void NoteApplied(PuzzleStateEntry entry)
        {
            string key = (byte)entry.Type + "_" + entry.WorldId.ToString("X");
            _lastSent[key] = entry;
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
            // MED_Pump: hold mid-water a/b/c (initial 12/0/0) so remount / late-join
            // re-snaps unsolved transfers, not only final Bool0 solved.
            if (e.Type == PuzzleType.MED_Pump)
                return e.Bool0 || e.Int0 != 12 || e.Int1 != 0 || e.Int2 != 0;
            return ProgressedBool0.Contains(e.Type) && e.Bool0;
        }

        public void ApplyPuzzleState(PuzzleStateMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (msg.Entries == null || msg.Entries.Length == 0) return;
            if (net != null && msg.SenderPlayerId == net.LocalPlayerId)
                return;
            if (SceneFollowService.LocalIsTransient())
                return;
            if (net != null && net.SceneMismatch)
                return;

            string applyLine = "apply n=" + msg.Entries.Length
                + " from=" + msg.SenderPlayerId + (msg.FullRefresh ? " full" : "")
                + " " + Describe(msg.Entries);
            if (msg.FullRefresh) PlaytestLog.Event("Puzzle", applyLine);
            else PlaytestLog.Verbose("Puzzle", applyLine);
            EnsureScanned();
            bool cinematic = !msg.FullRefresh;
            bool prevMutate = _mutateWorld;
            _mutateWorld = cinematic;
            NetGate.BeginApply();
            try
            {
                for (int i = 0; i < msg.Entries.Length; i++)
                    ApplyEntry(msg.Entries[i], cinematic);
            }
            finally
            {
                _mutateWorld = prevMutate;
                NetGate.EndApply();
            }

            if (cinematic)
            {
                try { DoorNative.ReassertLockVisuals(); } catch { }
            }

            if (net != null && net.Role == NetworkRole.Host && msg.SenderPlayerId != net.LocalPlayerId)
                net.SendPuzzleState(msg.Entries, false, msg.SenderPlayerId);
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
            if (_held.Count == 0) return;
            PlaytestLog.Event("Puzzle", "reapply held " + _held.Count + " " + Describe(_held));
            bool prevMutate = _mutateWorld;
            _mutateWorld = true;
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
                NetGate.EndApply();
            }
            try { DoorNative.ReassertLockVisuals(); } catch { }
            try
            {
                var net = LanNetworkManager.Instance;
                if (net != null)
                    net.PickupSync.HideClaimed(null);
            }
            catch { }
        }

        private void HoldIfProgressed(PuzzleStateEntry e)
        {
            if (!IsProgressed(e)) return;
            for (int i = 0; i < _held.Count; i++)
            {
                if (_held[i].Type == e.Type && _held[i].WorldId == e.WorldId)
                {
                    _held[i] = e;
                    return;
                }
            }
            _held.Add(e);
        }

        public void Emit(PuzzleType type, ulong worldId, Component c)
        {
            if (c == null || NetGate.IsApplying) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            PuzzleStateEntry entry;
            if (!TryRead(type, worldId, c, out entry)) return;
            if (!ChangedOrFirst(entry, false)) return;
            HoldIfProgressed(entry);
            PlaytestLog.Event("Puzzle", "emit " + type + " id=" + worldId.ToString("X16"));
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
            if (net == null || !net.IsConnected) return;
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
            if (!ChangedOrFirst(entry, false)) return;
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
            HoldIfProgressed(e);
            NoteApplied(e);
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
            catch { }
        }

        internal static void DisableOne(Interaction it)
        {
            if (it == null) return;
            try
            {
                if (it.GetComponent<ItemPickup>() != null) return;
            }
            catch { }
            try { it.triggered = true; } catch { }
            try { it.enabled = false; } catch { }
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
