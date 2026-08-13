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
        private bool _scanned;
        private bool _needFullSend = true;

        // Type → (WorldId → component)
        private readonly Dictionary<PuzzleType, Dictionary<ulong, Component>> _maps
            = new Dictionary<PuzzleType, Dictionary<ulong, Component>>();

        private readonly Dictionary<string, PuzzleStateEntry> _lastSent
            = new Dictionary<string, PuzzleStateEntry>();

        private static FieldInfo _storageBoxOpenField;

        public void RefreshScene()
        {
            _scanned = false;
            _needFullSend = true;
            _lastSent.Clear();
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
            try { arr = UnityEngine.Object.FindObjectsOfType<T>(); }
            catch { return; }
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
            RegisterAll<FoldingShutterDoor>(PuzzleType.FoldingShutterDoor);
            RegisterAll<Interaction>(PuzzleType.InteractionTriggered);
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

        public void TickHost(LanNetworkManager net)
        {
            if (net.Role != NetworkRole.Host) return;
            if (!net.IsConnected) return;
            if (!Config.ModConfig.PuzzlesEnabled) return;

            _sendTimer += Mathf.Min(Time.deltaTime, 0.1f);
            if (_sendTimer < SendInterval && !_needFullSend) return;
            _sendTimer = 0f;

            EnsureScanned();
            var entries = new List<PuzzleStateEntry>(64);
            bool full = _needFullSend;
            ReadAll(entries, full);
            _needFullSend = false;
            if (entries.Count == 0) return;
            net.SendPuzzleState(entries.ToArray(), full);
        }

        private void ReadAll(List<PuzzleStateEntry> entries, bool full)
        {
            foreach (var typeMap in _maps)
            {
                var type = typeMap.Key;
                foreach (var kvp in typeMap.Value)
                {
                    if (kvp.Value == null) continue;
                    PuzzleStateEntry entry;
                    if (!TryRead(type, kvp.Key, kvp.Value, out entry)) continue;
                    if (ChangedOrFirst(entry, full)) entries.Add(entry);
                }
            }

            // Globals (WorldId = 0)
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
                    case PuzzleType.FoldingShutterDoor:
                        { var x = (FoldingShutterDoor)c; entry = Mk(type, wid, false, false, false, 0, 0, 0, 0, x.open); return true; }
                    case PuzzleType.InteractionTriggered:
                        { var x = (Interaction)c; entry = Mk(type, wid, x.triggered, false, false, 0, 0, 0, 0, 0); return true; }
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

        public void ApplyPuzzleState(PuzzleStateMessage msg)
        {
            if (msg.Entries == null || msg.Entries.Length == 0) return;
            EnsureScanned();
            NetGate.BeginApply();
            try
            {
                for (int i = 0; i < msg.Entries.Length; i++)
                    ApplyEntry(msg.Entries[i]);
            }
            finally
            {
                NetGate.EndApply();
            }
        }

        private T Get<T>(PuzzleType type, long worldId) where T : class
        {
            if (worldId == 0) return null;
            Dictionary<ulong, Component> map;
            if (!_maps.TryGetValue(type, out map)) return null;
            Component c;
            if (!map.TryGetValue(unchecked((ulong)worldId), out c) || c == null) return null;
            return c as T;
        }

        private void ApplyEntry(PuzzleStateEntry e)
        {
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
                                x.solved = e.Bool0;
                                if (e.Bool0) TryUnlockDoors(x.gameObject);
                            }
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
                        { var x = Get<UseItemInteraction>(e.Type, e.WorldId); if (x != null) { x.unlocked = e.Bool0; if (e.Bool0) TryUnlockDoors(x.gameObject); } break; }
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
                        { var x = Get<CryoDoorController>(e.Type, e.WorldId); if (x != null) x.open = e.Bool0; break; }
                    case PuzzleType.FoldingShutterDoor:
                        { var x = Get<FoldingShutterDoor>(e.Type, e.WorldId); if (x != null) x.open = e.Float0; break; }
                    case PuzzleType.InteractionTriggered:
                        { var x = Get<Interaction>(e.Type, e.WorldId); if (x != null) x.triggered = e.Bool0; break; }
                    case PuzzleType.EventZoneTriggered:
                        { var x = Get<EventZone>(e.Type, e.WorldId); if (x != null) x.triggered = e.Bool0; break; }
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
                                x.frequency = e.Int0;
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
                            {
                                x.completed = true;
                                try { if (x.onGameLoad != null) x.onGameLoad.Invoke(); } catch { }
                            }
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

        private static void TryUnlockDoors(GameObject go)
        {
            if (go == null) return;
            try
            {
                var cd = FindInParents<ConnectedDoors>(go);
                if (cd != null && cd.locked)
                {
                    try { cd.Unlock(); }
                    catch { cd.locked = false; try { cd.UpdateProperties(); } catch { } }
                }
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
