// Host puzzle/interactive sync keyed by WorldId (never FindObjectsOfType index).
using System;
using System.Collections.Generic;
using System.Reflection;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    public sealed class PuzzleSyncService
    {
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

        private bool _pendingReapply;
        private static readonly HashSet<ulong> _cryoAnimStarted = new HashSet<ulong>();

        private static FieldInfo _storageBoxOpenField;

        public void RefreshScene()
        {
            _scanned = false;
            _needFullSend = true;
            _lastSent.Clear();
            _held.Clear();
            _cryoAnimStarted.Clear();
            _maps.Clear();
            ModRuntime.Log?.Msg("[PuzzleSync] Scene refreshed");
        }

        public void RequestFullSend() => _needFullSend = true;

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
            T[] arr = null;
            try { arr = UnityEngine.Object.FindObjectsOfType<T>(true); }
            catch { try { arr = UnityEngine.Object.FindObjectsOfType<T>(); } catch { return; } }
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

        // Buttons/levers only. Ladder / room-link Interactions teleport the local Elster.
        private void RegisterInteractions()
        {
            var map = Map(PuzzleType.InteractionTriggered);
            Interaction[] arr = null;
            try { arr = UnityEngine.Object.FindObjectsOfType<Interaction>(true); }
            catch { try { arr = UnityEngine.Object.FindObjectsOfType<Interaction>(); } catch { return; } }
            if (arr == null) return;
            for (int i = 0; i < arr.Length; i++)
            {
                var c = arr[i];
                if (c == null || IsLocalTraverse(c)) continue;
                ulong id = WorldId.FromGameObject(c.gameObject);
                if (id == 0) continue;
                if (!map.ContainsKey(id))
                    map[id] = c;
            }
        }

        private static bool IsLocalTraverse(Component c)
        {
            if (c == null) return false;
            try
            {
                var go = c.gameObject;
                if (FindInParents<Ladder>(go) != null) return true;
                if (FindInParents<ConnectedDoors>(go) != null) return true;
                if (FindInParents<LoadLevelZone>(go) != null) return true;
                if (FindInParents<LoadLevelInteraction>(go) != null) return true;
                if (FindInParents<AutoTraverseDoor>(go) != null) return true;
            }
            catch { }
            return false;
        }

        private void EnsureScanned()
        {
            if (_scanned) return;
            _maps.Clear();

            RegisterAll<PuzzleStatus>(PuzzleType.PuzzleStatus);
            RegisterAll<InteractiveLock>(PuzzleType.InteractiveLock);
            RegisterAll<InteractiveLockSingle>(PuzzleType.InteractiveLockSingle);
            RegisterAll<Keypad3D>(PuzzleType.Keypad3D);
            RegisterAll<ROT_Keypad>(PuzzleType.ROT_Keypad);
            RegisterAll<PEN_Codepad>(PuzzleType.PEN_Codepad);
            RegisterAll<LAB_PatternLock>(PuzzleType.PatternLock);
            RegisterAll<ROT_DialLock>(PuzzleType.DialLock);
            RegisterAll<FlipSwitch>(PuzzleType.FlipSwitch);
            RegisterAll<FloodControlSwitch>(PuzzleType.FloodControlSwitch);
            RegisterAll<FloodControls>(PuzzleType.FloodControls);
            RegisterAll<RES_Power>(PuzzleType.RES_Power);
            RegisterAll<UseItemInteraction>(PuzzleType.UseItemInteraction);
            RegisterAll<NumberLockNew>(PuzzleType.NumberLockNew);
            RegisterAll<DoorLockPuzzle>(PuzzleType.DoorLockPuzzle);
            RegisterAll<MED_MultiLock>(PuzzleType.MultiLock);
            RegisterAll<LAB_MultiLock>(PuzzleType.MultiLock);
            RegisterAll<MED_VentPuzzle>(PuzzleType.MED_VentPuzzle);
            RegisterAll<Doorway_simple>(PuzzleType.DoorwaySimple);
            RegisterAll<SwingDoor>(PuzzleType.SwingDoor);
            RegisterAll<DoorLockControl>(PuzzleType.DoorLockControl);
            RegisterAll<EvidenceLockerLogicPuzzle>(PuzzleType.EvidenceLockerPuzzle);
            RegisterAll<RadioStationTutorialPuzzle>(PuzzleType.RadioStationTutorial);
            RegisterAll<CentralElevatorControl>(PuzzleType.CentralElevator);
            RegisterAll<ElevatorCallButton>(PuzzleType.ElevatorCallButton);
            RegisterAll<DoorLockEventInteraction>(PuzzleType.DoorLockEventInteraction);
            RegisterAll<MultiConditionEvent>(PuzzleType.MultiConditionEvent);
            RegisterAll<CryoDoorController>(PuzzleType.CryoDoorController);
            RegisterAll<CryoDoorLock>(PuzzleType.CryoDoorLock);
            RegisterAll<PEN_Cryo>(PuzzleType.PEN_Cryo);
            RegisterAll<FoldingShutterDoor>(PuzzleType.FoldingShutterDoor);
            RegisterInteractions();
            RegisterAll<EventZone>(PuzzleType.EventZoneTriggered);
            RegisterAll<EnemyManager>(PuzzleType.EnemyManagerState);
            RegisterAll<StorageBox>(PuzzleType.StorageBox);
            RegisterAll<ROT_Tarot>(PuzzleType.ROT_Tarot);
            RegisterAll<ROT_Mural>(PuzzleType.ROT_Mural);
            RegisterAll<MED_Incinerator>(PuzzleType.MED_Incinerator);
            RegisterAll<LAB_Waage>(PuzzleType.LAB_Waage);
            RegisterAll<RES_Shrine>(PuzzleType.RES_Shrine);
            RegisterAll<ROT_RadioAlignment>(PuzzleType.ROT_RadioAlignment);
            RegisterAll<DET_RadioCodeLock>(PuzzleType.DET_RadioCodeLock);
            RegisterAll<UseItemMultiInteraction>(PuzzleType.UseItemMulti);
            RegisterAll<SaveRoomEvent>(PuzzleType.SaveRoomEvent);
            RegisterAll<CutsceneManager>(PuzzleType.CutsceneCompleted);
            RegisterAll<Dialogue>(PuzzleType.DialoguePlayedOnce);
            RegisterAll<EXC_Elevator>(PuzzleType.EXC_Elevator);
            RegisterAll<KolibriManager>(PuzzleType.KolibriManager);
            RegisterAll<BOS_Adler>(PuzzleType.BOS_Adler);

            if (_storageBoxOpenField == null)
                _storageBoxOpenField = typeof(StorageBox).GetField("open", BindingFlags.NonPublic | BindingFlags.Instance);

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

            EnsureScanned();

            // Clients only emit real local puzzle/lock changes. Buttons, event zones,
            // combat flags and cutscenes are not world-authoring — echoing those
            // runs native trigger()/EventScreen on the other Elster.
            if (net.Role != NetworkRole.Host)
            {
                if (_needFullSend)
                {
                    var seed = new List<PuzzleStateEntry>(64);
                    ReadAll(seed, true, clientFilter: true);
                    _needFullSend = false;
                    var progressed = new List<PuzzleStateEntry>(8);
                    for (int i = 0; i < seed.Count; i++)
                    {
                        if (IsProgressed(seed[i]))
                            progressed.Add(seed[i]);
                    }
                    if (progressed.Count > 0)
                    {
                        PlaytestLog.Event("Puzzle", "client seed send " + progressed.Count
                            + " " + Describe(progressed));
                        net.SendPuzzleState(progressed.ToArray(), false);
                    }
                    return;
                }
                var local = new List<PuzzleStateEntry>(16);
                ReadAll(local, false, clientFilter: true);
                if (local.Count > 0)
                {
                    PlaytestLog.Event("Puzzle", "client diff " + local.Count
                        + " " + Describe(local));
                    net.SendPuzzleState(local.ToArray(), false);
                }
                return;
            }

            var entries = new List<PuzzleStateEntry>(64);
            bool full = fullNow;
            ReadAll(entries, full, clientFilter: false);
            _needFullSend = false;
            if (entries.Count == 0) return;
            net.SendPuzzleState(entries.ToArray(), full);
        }

        private void ReadAll(List<PuzzleStateEntry> entries, bool full, bool clientFilter)
        {
            bool emittedMultiBlocked = false;
            foreach (var typeMap in _maps)
            {
                var type = typeMap.Key;
                if (clientFilter && !ClientMayEmit(type)) continue;
                foreach (var kvp in typeMap.Value)
                {
                    if (kvp.Value == null) continue;
                    if (type == PuzzleType.UseItemMulti)
                    {
                        if (emittedMultiBlocked) continue;
                        emittedMultiBlocked = true;
                    }
                    PuzzleStateEntry entry;
                    if (!TryRead(type, kvp.Key, kvp.Value, out entry)) continue;
                    if (ChangedOrFirst(entry, full)) entries.Add(entry);
                }
            }

            // Globals (WorldId = 0) — host only. Client combat/radio poll would clobber the host.
            if (!clientFilter)
            {
                int alarmVal = 0;
                try { alarmVal = (int)GlobalAlertStatus.currentStatus; } catch { }
                var alarm = Mk(PuzzleType.GlobalAlertStatus, 0, false, false, false, alarmVal, 0, 0, 0, 0f);
                if (ChangedOrFirst(alarm, full)) entries.Add(alarm);

                int radioBools = 0;
                try
                {
                    if (RadioManager.moduleInstalled) radioBools |= 1;
                }
                catch { }
                var radio = Mk(PuzzleType.RadioManagerState, 0, radioBools != 0, false, false, radioBools, 0, 0, 0, 0f);
                if (ChangedOrFirst(radio, full)) entries.Add(radio);
            }
        }

        private bool TryRead(PuzzleType type, ulong id, Component c, out PuzzleStateEntry entry)
        {
            entry = default;
            long wid = unchecked((long)id);
            try
            {
                switch (type)
                {
                    case PuzzleType.PuzzleStatus:
                        { var x = (PuzzleStatus)c; entry = Mk(type, wid, x.solved, false, false, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.InteractiveLock:
                        { var x = (InteractiveLock)c; entry = Mk(type, wid, x.locked, false, false, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.InteractiveLockSingle:
                        {
                            var x = (InteractiveLockSingle)c;
                            bool locked = x.timedOut || x.door == null || x.door.locked;
                            entry = Mk(type, wid, locked, false, false, 0, 0, 0, 0, 0); return true;
                        }
                    case PuzzleType.Keypad3D:
                        { var x = (Keypad3D)c; entry = Mk(type, wid, x.solved || x.opening, x.opening, x.blocked, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.ROT_Keypad:
                        { var x = (ROT_Keypad)c; entry = Mk(type, wid, x.solved || x.opening, x.opening, x.blocked, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.PEN_Codepad:
                        { var x = (PEN_Codepad)c; entry = Mk(type, wid, x.solved, false, false, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.PatternLock:
                        { var x = (LAB_PatternLock)c; entry = Mk(type, wid, x.solved, false, false, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.DialLock:
                        {
                            var x = (ROT_DialLock)c;
                            entry = Mk(type, wid, x.solved, false, false, x.A, x.B, x.C, x.D, 0); return true;
                        }
                    case PuzzleType.FlipSwitch:
                        { var x = (FlipSwitch)c; entry = Mk(type, wid, x.flipped, false, false, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.FloodControlSwitch:
                        { var x = (FloodControlSwitch)c; entry = Mk(type, wid, x.state, false, false, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.FloodControls:
                        { var x = (FloodControls)c; entry = Mk(type, wid, x.done, x.locked, false, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.RES_Power:
                        { var x = (RES_Power)c; entry = Mk(type, wid, x.solved, x.powered, false, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.UseItemInteraction:
                        { var x = (UseItemInteraction)c; entry = Mk(type, wid, x.unlocked, false, false, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.NumberLockNew:
                        { var x = (NumberLockNew)c; entry = Mk(type, wid, x.locked, false, false, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.DoorLockPuzzle:
                        { var x = (DoorLockPuzzle)c; entry = Mk(type, wid, x.locked, false, false, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.MultiLock:
                        {
                            var med = c as MED_MultiLock;
                            if (med != null) { entry = Mk(type, wid, med.unlocked, false, false, 0, 0, 0, 0, 0); return true; }
                            var lab = c as LAB_MultiLock;
                            if (lab != null) { entry = Mk(type, wid, lab.unlocked, false, false, 0, 0, 0, 0, 0); return true; }
                            return false;
                        }
                    case PuzzleType.MED_VentPuzzle:
                        { var x = (MED_VentPuzzle)c; entry = Mk(type, wid, x.uncovered, false, false, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.DoorwaySimple:
                        { var x = (Doorway_simple)c; entry = Mk(type, wid, x.locked, false, false, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.SwingDoor:
                        { var x = (SwingDoor)c; entry = Mk(type, wid, x.Open, x.locked, false, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.DoorLockControl:
                        { var x = (DoorLockControl)c; entry = Mk(type, wid, x.locked, false, false, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.EvidenceLockerPuzzle:
                        { var x = (EvidenceLockerLogicPuzzle)c; entry = Mk(type, wid, x.solved, false, false, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.RadioStationTutorial:
                        { var x = (RadioStationTutorialPuzzle)c; entry = Mk(type, wid, x.solved, false, false, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.CentralElevator:
                        {
                            var x = (CentralElevatorControl)c;
                            entry = Mk(type, wid, false, false, false, x.floor, (int)x.state, 0, 0, 0); return true;
                        }
                    case PuzzleType.ElevatorCallButton:
                        { var x = (ElevatorCallButton)c; entry = Mk(type, wid, x.called, false, false, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.DoorLockEventInteraction:
                        { var x = (DoorLockEventInteraction)c; entry = Mk(type, wid, x.done, false, false, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.MultiConditionEvent:
                        { var x = (MultiConditionEvent)c; entry = Mk(type, wid, x.triedOnce, false, false, x.tried, 0, 0, 0, 0); return true; }
                    case PuzzleType.CryoDoorController:
                        { var x = (CryoDoorController)c; entry = Mk(type, wid, x.open, false, false, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.CryoDoorLock:
                        { var x = (CryoDoorLock)c; entry = Mk(type, wid, x.done, false, false, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.PEN_Cryo:
                        { var x = (PEN_Cryo)c; entry = Mk(type, wid, x.opened, false, false, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.FoldingShutterDoor:
                        { var x = (FoldingShutterDoor)c; entry = Mk(type, wid, false, false, false, 0, 0, 0, 0, x.open); return true; }
                    case PuzzleType.InteractionTriggered:
                        {
                            if (IsLocalTraverse(c)) return false;
                            var x = (Interaction)c;
                            entry = Mk(type, wid, x.triggered, false, false, 0, 0, 0, 0, 0);
                            return true;
                        }
                    case PuzzleType.EventZoneTriggered:
                        { var x = (EventZone)c; entry = Mk(type, wid, x.triggered, false, false, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.StorageBox:
                        {
                            var x = (StorageBox)c;
                            bool open = _storageBoxOpenField != null && (bool)_storageBoxOpenField.GetValue(x);
                            entry = Mk(type, wid, open, false, false, 0, 0, 0, 0, 0); return true;
                        }
                    case PuzzleType.ROT_Tarot:
                        {
                            var x = (ROT_Tarot)c;
                            entry = Mk(type, wid, x.darkmode, false, false, 0, 0, 0, 0, x.FlipSwitchPos);
                            return true;
                        }
                    case PuzzleType.ROT_Mural:
                        {
                            var x = (ROT_Mural)c;
                            entry = Mk(type, wid, x.finished, x.busy, false, 0, 0, 0, 0, 0);
                            return true;
                        }
                    case PuzzleType.MED_Incinerator:
                        {
                            var x = (MED_Incinerator)c;
                            entry = Mk(type, wid, x.solved, false, false, x.A, x.B, x.C, 0, 0);
                            return true;
                        }
                    case PuzzleType.LAB_Waage:
                        {
                            var x = (LAB_Waage)c;
                            int item = 0;
                            try { if (x.content != null) item = (int)x.content._item; } catch { }
                            entry = Mk(type, wid, false, false, false, item, 0, 0, 0, x.weight);
                            return true;
                        }
                    case PuzzleType.RES_Shrine:
                        {
                            var x = (RES_Shrine)c;
                            entry = Mk(type, wid, x.solved, x.busy, false, x.big, x.mid, x.small, 0, 0);
                            return true;
                        }
                    case PuzzleType.ROT_RadioAlignment:
                        {
                            var x = (ROT_RadioAlignment)c;
                            entry = Mk(type, wid, x.east, false, false, x.correctAntenna, x.setAntenna, 0, 0, x.QualityE);
                            return true;
                        }
                    case PuzzleType.DET_RadioCodeLock:
                        {
                            var x = (DET_RadioCodeLock)c;
                            entry = Mk(type, wid, false, false, false, x.frequency, x.code, x.hintStation, 0, 0);
                            return true;
                        }
                    case PuzzleType.UseItemMulti:
                        {
                            var x = (UseItemMultiInteraction)c;
                            entry = Mk(type, wid, UseItemMultiInteraction.blocked, false, false, 0, 0, 0, 0, 0);
                            return true;
                        }
                    case PuzzleType.SaveRoomEvent:
                        {
                            var x = (SaveRoomEvent)c;
                            entry = Mk(type, wid, x.triggered, false, false, 0, 0, 0, 0, 0);
                            return true;
                        }
                    case PuzzleType.CutsceneCompleted:
                        {
                            var x = (CutsceneManager)c;
                            entry = Mk(type, wid, x.completed, false, false, 0, 0, 0, 0, 0);
                            return true;
                        }
                    case PuzzleType.DialoguePlayedOnce:
                        {
                            var x = (Dialogue)c;
                            entry = Mk(type, wid, x.playedOnce, false, false, 0, 0, 0, 0, 0);
                            return true;
                        }
                    case PuzzleType.EXC_Elevator:
                        {
                            var x = (EXC_Elevator)c;
                            entry = Mk(type, wid, x.riding, x.stopped, false, 0, 0, 0, 0, 0);
                            return true;
                        }
                    case PuzzleType.KolibriManager:
                        {
                            var x = (KolibriManager)c;
                            entry = Mk(type, wid, x.dead, false, false, 0, 0, 0, 0, x.intensity);
                            return true;
                        }
                    case PuzzleType.BOS_Adler:
                        {
                            var x = (BOS_Adler)c;
                            entry = Mk(type, wid, false, false, false, 0, 0, 0, 0, x.intensity);
                            return true;
                        }
                    case PuzzleType.EnemyManagerState:
                        {
                            var x = (EnemyManager)c;
                            int bits = 0;
                            if (EnemyManager.inCombat) bits |= 1;
                            if (EnemyManager.enemyPresence) bits |= 2;
                            entry = Mk(type, wid, x.cleared, x.inOperation, false, bits, 0, 0, 0, 0); return true;
                        }
                }
            }
            catch { }
            return false;
        }

        private static PuzzleStateEntry Mk(PuzzleType type, long worldId, bool b0, bool b1, bool b2, int i0, int i1, int i2, int i3, float f0)
        {
            return new PuzzleStateEntry
            {
                Type = type,
                WorldId = worldId,
                Bool0 = b0, Bool1 = b1, Bool2 = b2,
                Int0 = i0, Int1 = i1, Int2 = i2, Int3 = i3,
                Float0 = f0
            };
        }

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
                    && Mathf.Approximately(prev.Float0, entry.Float0))
                    return false;
            }
            _lastSent[key] = entry;
            return true;
        }

        private static bool ClientMayEmit(PuzzleType type)
        {
            switch (type)
            {
                case PuzzleType.PuzzleStatus:
                case PuzzleType.InteractiveLock:
                case PuzzleType.InteractiveLockSingle:
                case PuzzleType.Keypad3D:
                case PuzzleType.ROT_Keypad:
                case PuzzleType.PEN_Codepad:
                case PuzzleType.PatternLock:
                case PuzzleType.DialLock:
                case PuzzleType.FlipSwitch:
                case PuzzleType.FloodControlSwitch:
                case PuzzleType.FloodControls:
                case PuzzleType.RES_Power:
                case PuzzleType.UseItemInteraction:
                case PuzzleType.NumberLockNew:
                case PuzzleType.DoorLockPuzzle:
                case PuzzleType.MultiLock:
                case PuzzleType.MED_VentPuzzle:
                case PuzzleType.DoorLockControl:
                case PuzzleType.EvidenceLockerPuzzle:
                case PuzzleType.RadioStationTutorial:
                case PuzzleType.ElevatorCallButton:
                case PuzzleType.DoorLockEventInteraction:
                case PuzzleType.CryoDoorController:
                case PuzzleType.CryoDoorLock:
                case PuzzleType.PEN_Cryo:
                case PuzzleType.FoldingShutterDoor:
                case PuzzleType.ROT_Tarot:
                case PuzzleType.ROT_Mural:
                case PuzzleType.MED_Incinerator:
                case PuzzleType.LAB_Waage:
                case PuzzleType.RES_Shrine:
                case PuzzleType.ROT_RadioAlignment:
                case PuzzleType.DET_RadioCodeLock:
                    return true;
                default:
                    return false;
            }
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
            switch (e.Type)
            {
                case PuzzleType.PEN_Codepad:
                case PuzzleType.Keypad3D:
                case PuzzleType.ROT_Keypad:
                case PuzzleType.PuzzleStatus:
                case PuzzleType.UseItemInteraction:
                case PuzzleType.CryoDoorLock:
                case PuzzleType.PEN_Cryo:
                case PuzzleType.CryoDoorController:
                case PuzzleType.PatternLock:
                case PuzzleType.EvidenceLockerPuzzle:
                    return e.Bool0;
                default:
                    return false;
            }
        }

        public void ApplyPuzzleState(PuzzleStateMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (msg.Entries == null || msg.Entries.Length == 0) return;
            if (net != null && msg.SenderPlayerId == net.LocalPlayerId)
                return;

            PlaytestLog.Event("Puzzle", "apply n=" + msg.Entries.Length
                + " from=" + msg.SenderPlayerId + (msg.FullRefresh ? " full" : "")
                + " " + Describe(msg.Entries));
            EnsureScanned();
            NetGate.BeginApply();
            try
            {
                for (int i = 0; i < msg.Entries.Length; i++)
                    ApplyEntry(msg.Entries[i], cinematic: false);
            }
            finally
            {
                NetGate.EndApply();
            }

            if (net != null && net.Role == NetworkRole.Host && msg.SenderPlayerId != net.LocalPlayerId)
                net.SendPuzzleState(msg.Entries, false, msg.SenderPlayerId);
        }

        public void QueueReapply()
        {
            _pendingReapply = true;
        }

        public void ReapplyHeld()
        {
            if (_held.Count == 0) return;
            EnsureScanned();
            PlaytestLog.Event("Puzzle", "reapply held " + _held.Count + " " + Describe(_held));
            NetGate.BeginApply();
            try
            {
                for (int i = 0; i < _held.Count; i++)
                    ApplyEntry(_held[i], cinematic: false);
            }
            finally
            {
                NetGate.EndApply();
            }
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
            PlaytestLog.Event("Puzzle", "emit " + type + " id=" + worldId.ToString("X16"));
            net.SendPuzzleState(new[] { entry }, false);
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
            if (!map.TryGetValue(unchecked((ulong)worldId), out c) || c == null)
                return null;
            return c as T;
        }

        private void ApplyEntry(PuzzleStateEntry e, bool cinematic)
        {
            HoldIfProgressed(e);
            NoteApplied(e);
            try
            {
                switch (e.Type)
                {
                    case PuzzleType.PuzzleStatus:
                        { var x = Get<PuzzleStatus>(e.Type, e.WorldId); if (x != null) x.solved = e.Bool0; break; }
                    case PuzzleType.InteractiveLock:
                        {
                            var x = Get<InteractiveLock>(e.Type, e.WorldId);
                            if (x != null)
                            {
                                bool was = x.locked;
                                x.locked = e.Bool0;
                                if (was && !e.Bool0)
                                {
                                    try { x.delayedOpen(); } catch { }
                                    TryUnlockDoors(x.gameObject);
                                }
                            }
                            break;
                        }
                    case PuzzleType.InteractiveLockSingle:
                        {
                            var x = Get<InteractiveLockSingle>(e.Type, e.WorldId);
                            if (x != null) { x.timedOut = e.Bool0; if (x.door != null) x.door.locked = e.Bool0; }
                            break;
                        }
                    case PuzzleType.Keypad3D:
                        {
                            var x = Get<Keypad3D>(e.Type, e.WorldId);
                            if (x != null)
                            {
                                bool was = x.solved;
                                x.solved = e.Bool0; x.opening = e.Bool1; x.blocked = e.Bool2;
                                if (e.Bool0)
                                {
                                    if (!was) { try { x.openDoor(); } catch { } }
                                    TryUnlockDoors(x.gameObject);
                                }
                            }
                            break;
                        }
                    case PuzzleType.ROT_Keypad:
                        {
                            var x = Get<ROT_Keypad>(e.Type, e.WorldId);
                            if (x != null)
                            {
                                x.solved = e.Bool0; x.opening = e.Bool1; x.blocked = e.Bool2;
                                if (e.Bool0) TryUnlockDoors(x.gameObject);
                            }
                            break;
                        }
                    case PuzzleType.PEN_Codepad:
                        {
                            var x = Get<PEN_Codepad>(e.Type, e.WorldId);
                            if (x != null)
                            {
                                if (x.solved && !e.Bool0) break;
                                x.solved = e.Bool0 || x.solved;
                                if (e.Bool0)
                                    ApplyCodepadConsequences(x);
                            }
                            else
                                PlaytestLog.Miss("Puzzle", "PEN_Codepad", unchecked((ulong)e.WorldId));
                            break;
                        }
                    case PuzzleType.PatternLock:
                        {
                            var x = Get<LAB_PatternLock>(e.Type, e.WorldId);
                            if (x != null)
                            {
                                x.solved = e.Bool0;
                                if (e.Bool0) TryUnlockDoors(x.gameObject);
                            }
                            break;
                        }
                    case PuzzleType.DialLock:
                        {
                            var x = Get<ROT_DialLock>(e.Type, e.WorldId);
                            if (x != null)
                            {
                                x.solved = e.Bool0;
                                x.A = e.Int0; x.B = e.Int1; x.C = e.Int2; x.D = e.Int3;
                                if (x.a != null) x.a.localEulerAngles = new Vector3(0, e.Int0 * 36, 0);
                                if (x.b != null) x.b.localEulerAngles = new Vector3(0, e.Int1 * 36, 0);
                                if (x.c != null) x.c.localEulerAngles = new Vector3(0, e.Int2 * 36, 0);
                                if (x.d != null) x.d.localEulerAngles = new Vector3(0, e.Int3 * 36, 0);
                            }
                            break;
                        }
                    case PuzzleType.FlipSwitch:
                        { var x = Get<FlipSwitch>(e.Type, e.WorldId); if (x != null) x.flipped = e.Bool0; break; }
                    case PuzzleType.FloodControlSwitch:
                        { var x = Get<FloodControlSwitch>(e.Type, e.WorldId); if (x != null) x.state = e.Bool0; break; }
                    case PuzzleType.FloodControls:
                        {
                            var x = Get<FloodControls>(e.Type, e.WorldId);
                            if (x != null) { x.done = e.Bool0; x.locked = e.Bool1; }
                            break;
                        }
                    case PuzzleType.RES_Power:
                        {
                            var x = Get<RES_Power>(e.Type, e.WorldId);
                            if (x != null) { x.solved = e.Bool0; x.powered = e.Bool1; }
                            break;
                        }
                    case PuzzleType.UseItemInteraction:
                        {
                            var x = Get<UseItemInteraction>(e.Type, e.WorldId);
                            if (x != null)
                            {
                                x.unlocked = e.Bool0;
                                if (e.Bool0)
                                    TryUnlockDoors(x.gameObject);
                            }
                            break;
                        }
                    case PuzzleType.NumberLockNew:
                        { var x = Get<NumberLockNew>(e.Type, e.WorldId); if (x != null) x.locked = e.Bool0; break; }
                    case PuzzleType.DoorLockPuzzle:
                        { var x = Get<DoorLockPuzzle>(e.Type, e.WorldId); if (x != null) x.locked = e.Bool0; break; }
                    case PuzzleType.MultiLock:
                        {
                            var med = Get<MED_MultiLock>(e.Type, e.WorldId);
                            if (med != null) { med.unlocked = e.Bool0; break; }
                            var lab = Get<LAB_MultiLock>(e.Type, e.WorldId);
                            if (lab != null) lab.unlocked = e.Bool0;
                            break;
                        }
                    case PuzzleType.MED_VentPuzzle:
                        { var x = Get<MED_VentPuzzle>(e.Type, e.WorldId); if (x != null) x.uncovered = e.Bool0; break; }
                    case PuzzleType.DoorwaySimple:
                        { var x = Get<Doorway_simple>(e.Type, e.WorldId); if (x != null) x.locked = e.Bool0; break; }
                    case PuzzleType.SwingDoor:
                        {
                            var x = Get<SwingDoor>(e.Type, e.WorldId);
                            if (x != null) { x.Open = e.Bool0; x.locked = e.Bool1; }
                            break;
                        }
                    case PuzzleType.DoorLockControl:
                        { var x = Get<DoorLockControl>(e.Type, e.WorldId); if (x != null) x.locked = e.Bool0; break; }
                    case PuzzleType.EvidenceLockerPuzzle:
                        { var x = Get<EvidenceLockerLogicPuzzle>(e.Type, e.WorldId); if (x != null) x.solved = e.Bool0; break; }
                    case PuzzleType.RadioStationTutorial:
                        { var x = Get<RadioStationTutorialPuzzle>(e.Type, e.WorldId); if (x != null) x.solved = e.Bool0; break; }
                    case PuzzleType.CentralElevator:
                        {
                            var x = Get<CentralElevatorControl>(e.Type, e.WorldId);
                            if (x != null) { x.floor = e.Int0; x.state = (CentralElevatorControl.evState)e.Int1; }
                            break;
                        }
                    case PuzzleType.ElevatorCallButton:
                        { var x = Get<ElevatorCallButton>(e.Type, e.WorldId); if (x != null) x.called = e.Bool0; break; }
                    case PuzzleType.DoorLockEventInteraction:
                        { var x = Get<DoorLockEventInteraction>(e.Type, e.WorldId); if (x != null) x.done = e.Bool0; break; }
                    case PuzzleType.MultiConditionEvent:
                        {
                            var x = Get<MultiConditionEvent>(e.Type, e.WorldId);
                            if (x != null) { x.triedOnce = e.Bool0; x.tried = e.Int0; }
                            break;
                        }
                    case PuzzleType.CryoDoorController:
                        {
                            var x = Get<CryoDoorController>(e.Type, e.WorldId);
                            if (x != null && e.Bool0 != x.open)
                            {
                                try { x.toggleDoors(); }
                                catch { x.open = e.Bool0; }
                            }
                            break;
                        }
                    case PuzzleType.CryoDoorLock:
                        {
                            var x = Get<CryoDoorLock>(e.Type, e.WorldId);
                            if (x != null && e.Bool0)
                                SnapCryoLock(x);
                            else if (x == null)
                                PlaytestLog.Miss("Puzzle", "CryoDoorLock", unchecked((ulong)e.WorldId));
                            break;
                        }
                    case PuzzleType.PEN_Cryo:
                        {
                            var x = Get<PEN_Cryo>(e.Type, e.WorldId);
                            if (x != null && e.Bool0)
                                SnapPenCryo(x, playOpen: cinematic);
                            else if (x == null)
                                PlaytestLog.Miss("Puzzle", "PEN_Cryo", unchecked((ulong)e.WorldId));
                            break;
                        }
                    case PuzzleType.FoldingShutterDoor:
                        { var x = Get<FoldingShutterDoor>(e.Type, e.WorldId); if (x != null) x.open = e.Float0; break; }
                    case PuzzleType.InteractionTriggered:
                        {
                            var x = Get<Interaction>(e.Type, e.WorldId);
                            if (x != null && !IsLocalTraverse(x))
                                x.triggered = e.Bool0;
                            break;
                        }
                    case PuzzleType.EventZoneTriggered:
                        {
                            var x = Get<EventZone>(e.Type, e.WorldId);
                            if (x != null)
                            {
                                x.triggered = e.Bool0;
                                if (e.Bool0)
                                    SyncRADation.Patches.EventZonePatch.MarkFired(unchecked((ulong)e.WorldId));
                            }
                            break;
                        }
                    case PuzzleType.GlobalAlertStatus:
                        GlobalAlertStatus.currentStatus = (GlobalAlertStatus.alarm)e.Int0;
                        break;
                    case PuzzleType.RadioManagerState:
                        RadioManager.moduleInstalled = (e.Int0 & 1) != 0;
                        break;
                    case PuzzleType.StorageBox:
                        {
                            var x = Get<StorageBox>(e.Type, e.WorldId);
                            if (x != null && _storageBoxOpenField != null)
                                _storageBoxOpenField.SetValue(x, e.Bool0);
                            break;
                        }
                    case PuzzleType.ROT_Tarot:
                        {
                            var x = Get<ROT_Tarot>(e.Type, e.WorldId);
                            if (x != null) { x.darkmode = e.Bool0; x.FlipSwitchPos = e.Float0; }
                            break;
                        }
                    case PuzzleType.ROT_Mural:
                        {
                            var x = Get<ROT_Mural>(e.Type, e.WorldId);
                            if (x != null)
                            {
                                x.finished = e.Bool0; x.busy = e.Bool1;
                                if (e.Bool0)
                                {
                                    try { x.useRing(); } catch { }
                                    TryUnlockDoors(x.gameObject);
                                }
                            }
                            break;
                        }
                    case PuzzleType.MED_Incinerator:
                        {
                            var x = Get<MED_Incinerator>(e.Type, e.WorldId);
                            if (x != null)
                            {
                                x.solved = e.Bool0; x.A = e.Int0; x.B = e.Int1; x.C = e.Int2;
                                if (e.Bool0)
                                {
                                    try { x.StartShutdown(); } catch { }
                                    TryUnlockDoors(x.gameObject);
                                }
                            }
                            break;
                        }
                    case PuzzleType.LAB_Waage:
                        {
                            var x = Get<LAB_Waage>(e.Type, e.WorldId);
                            if (x != null)
                            {
                                x.weight = e.Float0;
                                try { x.content = InventoryManager.getItem((Items.itemlist)e.Int0); } catch { }
                            }
                            break;
                        }
                    case PuzzleType.RES_Shrine:
                        {
                            var x = Get<RES_Shrine>(e.Type, e.WorldId);
                            if (x != null)
                            {
                                x.solved = e.Bool0; x.busy = e.Bool1;
                                x.big = e.Int0; x.mid = e.Int1; x.small = e.Int2;
                                if (e.Bool0)
                                {
                                    try { x.CheckSolve(); } catch { }
                                    TryUnlockDoors(x.gameObject);
                                }
                            }
                            break;
                        }
                    case PuzzleType.ROT_RadioAlignment:
                        {
                            var x = Get<ROT_RadioAlignment>(e.Type, e.WorldId);
                            if (x != null)
                            {
                                x.east = e.Bool0;
                                x.correctAntenna = e.Int0;
                                x.setAntenna = e.Int1;
                                x.QualityE = e.Float0;
                            }
                            break;
                        }
                    case PuzzleType.DET_RadioCodeLock:
                        {
                            var x = Get<DET_RadioCodeLock>(e.Type, e.WorldId);
                            if (x != null)
                            {
                                x.code = e.Int1;
                                x.hintStation = e.Int2;
                            }
                            break;
                        }
                    case PuzzleType.UseItemMulti:
                        UseItemMultiInteraction.blocked = e.Bool0;
                        break;
                    case PuzzleType.SaveRoomEvent:
                        {
                            var x = Get<SaveRoomEvent>(e.Type, e.WorldId);
                            if (x != null) x.triggered = e.Bool0;
                            break;
                        }
                    case PuzzleType.CutsceneCompleted:
                        {
                            var x = Get<CutsceneManager>(e.Type, e.WorldId);
                            if (x != null && e.Bool0)
                                x.completed = true;
                            break;
                        }
                    case PuzzleType.DialoguePlayedOnce:
                        {
                            var x = Get<Dialogue>(e.Type, e.WorldId);
                            if (x != null) x.playedOnce = e.Bool0;
                            break;
                        }
                    case PuzzleType.EXC_Elevator:
                        {
                            var x = Get<EXC_Elevator>(e.Type, e.WorldId);
                            if (x != null) { x.stopped = e.Bool1; }
                            break;
                        }
                    case PuzzleType.KolibriManager:
                        {
                            var x = Get<KolibriManager>(e.Type, e.WorldId);
                            if (x != null) { x.dead = e.Bool0; x.intensity = e.Float0; }
                            break;
                        }
                    case PuzzleType.BOS_Adler:
                        {
                            var x = Get<BOS_Adler>(e.Type, e.WorldId);
                            if (x != null) x.intensity = e.Float0;
                            break;
                        }
                    case PuzzleType.EnemyManagerState:
                        {
                            var x = Get<EnemyManager>(e.Type, e.WorldId);
                            if (x != null) { x.cleared = e.Bool0; x.inOperation = e.Bool1; }
                            EnemyManager.inCombat = (e.Int0 & 1) != 0;
                            EnemyManager.enemyPresence = (e.Int0 & 2) != 0;
                            break;
                        }
                }
            }
            catch (Exception ex)
            {
                if (Config.ModConfig.VerboseLogging?.Value == true)
                    ModRuntime.Log?.Warning("[PuzzleSync] Apply " + e.Type + ": " + ex.Message);
            }
        }

        private static T FindInParents<T>(GameObject go) where T : Component
        {
            Transform t = go.transform;
            while (t != null)
            {
                var c = t.GetComponent<T>();
                if (c != null) return c;
                t = t.parent;
            }
            return null;
        }

        public static bool IsPuzzleOverlay(EventScreenInteraction e)
        {
            if (e == null) return false;
            try
            {
                var go = e.gameObject;
                if (FindInParents<CryoDoorLock>(go) != null) return true;
                if (FindInParents<PEN_Codepad>(go) != null) return true;
                if (FindInParents<PEN_Cryo>(go) != null) return true;
                if (FindInParents<Keypad3D>(go) != null) return true;
                if (FindInParents<ROT_Keypad>(go) != null) return true;
            }
            catch { }
            try
            {
                var locks = UnityEngine.Object.FindObjectsOfType<CryoDoorLock>(true);
                if (locks != null)
                {
                    for (int i = 0; i < locks.Length; i++)
                    {
                        if (locks[i] != null && locks[i].Event == e)
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }

        public static void UnlockLinked(GameObject go) => TryUnlockDoors(go);

        public static void ApplyCodepadConsequences(PEN_Codepad pad)
        {
            if (pad == null) return;
            pad.solved = true;
            TryUnlockDoors(pad.gameObject);
            PlaytestLog.Event("Puzzle", "codepad solved " + pad.gameObject.name);
            try
            {
                var locks = UnityEngine.Object.FindObjectsOfType<CryoDoorLock>(true);
                if (locks == null) return;
                for (int i = 0; i < locks.Length; i++)
                {
                    var c = locks[i];
                    if (c == null) continue;
                    try
                    {
                        if (c.puzzle != null && c.puzzle != pad) continue;
                    }
                    catch { continue; }
                    SnapCryoLock(c);
                }
            }
            catch { }
        }

        // World result only. CryoDoorLock.solved() starts EventScreen on the local
        // Elster — that is why the other player froze / could not walk.
        internal static void SnapCryoLock(CryoDoorLock c)
        {
            if (c == null) return;
            try { if (c.done) { TryUnlockDoors(c.gameObject); return; } } catch { }

            PlaytestLog.Event("Puzzle", "snap CryoDoorLock " + c.gameObject.name);
            try { c.done = true; } catch { }
            try
            {
                if (c.puzzle != null)
                    c.puzzle.solved = true;
            }
            catch { }
            try
            {
                if (c.inter != null)
                {
                    c.inter.triggered = true;
                    c.inter.enabled = false;
                }
            }
            catch { }
            try
            {
                if (c.Door != null)
                {
                    c.Door.SetActive(true);
                    UnlockDoorObject(c.Door);
                }
            }
            catch { }
            TryUnlockDoors(c.gameObject);
        }

        static void SnapPenCryo(PEN_Cryo x, bool playOpen)
        {
            if (x == null) return;
            PlaytestLog.Event("Puzzle", "snap PEN_Cryo " + x.gameObject.name
                + (playOpen ? " open" : " pose"));
            try
            {
                if (x.contentLateActivated != null)
                    x.contentLateActivated.SetActive(true);
            }
            catch { }
            try
            {
                if (x.interaction != null)
                    x.interaction.triggered = true;
            }
            catch { }
            try
            {
                if (x.RoomDoor != null)
                    UnlockDoorObject(x.RoomDoor);
            }
            catch { }
            TryUnlockDoors(x.gameObject);

            bool active = false;
            try { active = x.gameObject.activeInHierarchy; } catch { active = true; }
            ulong id = WorldId.FromGameObject(x.gameObject);

            if (playOpen && active && (id == 0 || _cryoAnimStarted.Add(id)))
            {
                try { x.Open(); }
                catch { }
            }
            else
                PosePenCryoOpen(x);

            try { x.opened = true; } catch { }
        }

        static void PosePenCryoOpen(PEN_Cryo x)
        {
            if (x == null) return;
            try
            {
                if (x.Door != null)
                {
                    var e = x.Door.localEulerAngles;
                    e.y = x.doorOpenAngle;
                    x.Door.localEulerAngles = e;
                }
            }
            catch { }
            try
            {
                if (x.cover != null)
                {
                    var e = x.cover.localEulerAngles;
                    e.y = x.coverOpenAngle;
                    x.cover.localEulerAngles = e;
                }
            }
            catch { }
            try
            {
                if (x.coverMover != null)
                {
                    var p = x.coverMover.localPosition;
                    p.y = x.moverYPos;
                    x.coverMover.localPosition = p;
                }
            }
            catch { }
            try
            {
                if (x.Opener != null)
                {
                    var p = x.Opener.localPosition;
                    p.y = x.openerPos;
                    x.Opener.localPosition = p;
                }
            }
            catch { }
            try { if (x.scanner != null) x.scanner.enabled = false; } catch { }
        }

        static void UnlockDoorObject(GameObject door)
        {
            if (door == null) return;
            TryUnlockDoors(door);
            try
            {
                var dlc = door.GetComponent<DoorLockControl>();
                if (dlc != null)
                {
                    try { dlc.setLock(false); } catch { dlc.locked = false; }
                }
            }
            catch { }
            try
            {
                var simple = door.GetComponent<Doorway_simple>();
                if (simple != null) simple.locked = false;
            }
            catch { }
            try
            {
                var dbl = door.GetComponent<Doorway_Double>();
                if (dbl != null) dbl.locked = false;
            }
            catch { }
            try
            {
                var cd = door.GetComponent<ConnectedDoors>();
                if (cd != null) DoorNative.ApplyConnectedDoors(cd, false);
            }
            catch { }
        }

        private static void TryUnlockDoors(GameObject go)
        {
            if (go == null) return;
            try
            {
                var cd = FindInParents<ConnectedDoors>(go);
                if (cd != null && cd.locked)
                    DoorNative.ApplyConnectedDoors(cd, false);
            }
            catch { }
            try
            {
                var dlc = FindInParents<DoorLockControl>(go);
                if (dlc != null)
                {
                    try { dlc.setLock(false); } catch { dlc.locked = false; }
                }
            }
            catch { }
        }
    }
}
