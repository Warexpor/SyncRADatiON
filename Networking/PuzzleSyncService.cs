// Host puzzle/interactive sync keyed by WorldId (never FindObjectsOfType index).
using System;
using System.Collections.Generic;
using System.Reflection;
using FMODUnity;
using SyncRADation.ItemSystem;
using SyncRADation.Patches;
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
        private static readonly HashSet<string> _worldAnimStarted = new HashSet<string>();
        // Join/resync dumps are a settled snapshot. Replaying native transitions
        // (EventZone.Invoke, openDoor, delayedOpen, slaveInteraction.enable)
        // wakes leftover inactive puzzles and unseals flavor doors the host never touched.
        private static bool _mutateWorld = true;

        private static FieldInfo _storageBoxOpenField;

        public void RefreshScene()
        {
            _scanned = false;
            _needFullSend = true;
            _lastSent.Clear();
            _held.Clear();
            _worldAnimStarted.Clear();
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

        private static bool IsLocalTraverse(Component c)
        {
            if (c == null) return false;
            // The door/lock component itself must stay in the puzzle map. Parent-walk
            // would skip every Doorway_simple / SwingDoor / DoorLockControl as "self".
            if (c is Doorway_simple || c is SwingDoor || c is DoorLockControl)
                return false;
            try
            {
                var go = c.gameObject;
                if (FindInParents<Ladder>(go) != null) return true;
                if (FindInParents<ConnectedDoors>(go) != null) return true;
                if (FindInParents<LoadLevelZone>(go) != null) return true;
                if (FindInParents<LoadLevelInteraction>(go) != null) return true;
                if (FindInParents<AirlockDoorLoadZone>(go) != null) return true;
                if (FindInParents<EventOnlyRoom>(go) != null) return true;
                if (FindInParents<PEN_Airlock>(go) != null) return true;
                if (FindInParents<PEN_Titles>(go) != null) return true;
                if (LocalInspect.Cinematic(go) || LocalInspect.LockWorld(go)) return true;
                if (FindInParents<PenroseAirlockNew>(go) != null) return true;
                if (FindInParents<AutoTraverseDoor>(go) != null) return true;
                if (FindInParents<Doorway_Double>(go) != null) return true;
                if (FindInParents<Doorway_simple>(go) != null) return true;
                if (FindInParents<EventSlidingDoor>(go) != null) return true;
                if (FindInParents<EventDoor>(go) != null) return true;
                if (FindInParents<SwingDoor>(go) != null) return true;
                var inter = c as Interaction ?? go.GetComponent<Interaction>();
                if (inter != null && (inter.type == Interaction.interType.move
                    || inter.type == Interaction.interType.open))
                    return true;
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
            RegisterAll<MED_Pump>(PuzzleType.MED_Pump);
            RegisterAll<MED_FloodedBathroom>(PuzzleType.MED_FloodedBathroom);
            RegisterAll<MED_CardWriter>(PuzzleType.MED_CardWriter);
            RegisterAll<RES_Shutters>(PuzzleType.RES_Shutters);
            RegisterAll<ROT_Pipes>(PuzzleType.ROT_Pipes);
            RegisterAll<ROT_Magpie>(PuzzleType.ROT_Magpie);
            RegisterAll<PEN_Reaktor>(PuzzleType.PEN_Reaktor);
            RegisterAll<DET_ServiceLock>(PuzzleType.DET_ServiceLock);
            RegisterAll<EXC_Seilbahn>(PuzzleType.EXC_Seilbahn);
            RegisterAll<EXC_Hatch>(PuzzleType.EXC_Hatch);
            RegisterAll<LAB_Rings>(PuzzleType.LAB_Rings);
            RegisterAll<BiodomeDoorLock>(PuzzleType.BiodomeDoorLock);
            RegisterAll<ROT_MeatBlocker>(PuzzleType.ROT_MeatBlocker);
            RegisterAll<FoldingShutterDoor>(PuzzleType.FoldingShutterDoor);
            // Do not poll Interaction.triggered. Door/move Interactions teleport the local
            // Elster when the other peer walks through a ConnectedDoors link.
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
                    var seed = new List<PuzzleStateEntry>(64);
                    ReadAll(seed, true, clientFilter: true, activeOnly: false);
                    _needFullSend = false;
                    var progressed = new List<PuzzleStateEntry>(8);
                    for (int i = 0; i < seed.Count; i++)
                    {
                        if (IsProgressed(seed[i]))
                            progressed.Add(seed[i]);
                    }
                    if (progressed.Count > 0)
                    {
                        PlaytestLog.Event("Puzzle", "client seed " + progressed.Count
                            + " " + Describe(progressed));
                        net.SendPuzzleState(progressed.ToArray(), false);
                    }
                    return;
                }
                var local = new List<PuzzleStateEntry>(16);
                ReadAll(local, false, clientFilter: true, activeOnly: true);
                if (local.Count > 0)
                {
                    PlaytestLog.Verbose("Puzzle", "client diff " + local.Count
                        + " " + Describe(local));
                    net.SendPuzzleState(local.ToArray(), false);
                }
                return;
            }

            var entries = new List<PuzzleStateEntry>(64);
            bool full = fullNow;
            ReadAll(entries, full, clientFilter: false, activeOnly: !full);
            _needFullSend = false;
            if (entries.Count == 0) return;
            net.SendPuzzleState(entries.ToArray(), full);
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
                switch (type)
                {
                    case PuzzleType.PuzzleStatus:
                        { var x = (PuzzleStatus)c; entry = Mk(type, wid, x.solved, false, false, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.InteractiveLock:
                        { var x = (InteractiveLock)c; entry = Mk(type, wid, x.locked, false, false, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.InteractiveLockSingle:
                        {
                            var x = (InteractiveLockSingle)c;
                            bool locked = x.door != null && x.door.locked;
                            bool plate = DoorNative.TraversePlateActive(x);
                            try
                            {
                                if (!plate && x.key == null && (x.door == null || !x.door.open))
                                    plate = DoorNative.IsFlavorSeal(x.gameObject)
                                        || (x.door != null && DoorNative.IsFlavorSeal(x.door.gameObject));
                            }
                            catch { }
                            entry = Mk(type, wid, locked, plate, false, 0, 0, 0, 0, 0); return true;
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
                    case PuzzleType.MED_Pump:
                        { var x = (MED_Pump)c; entry = Mk(type, wid, x.solved, false, false, x.a, x.b, x.c, 0, 0); return true; }
                    case PuzzleType.MED_FloodedBathroom:
                        {
                            var x = (MED_FloodedBathroom)c;
                            bool drained = x.Ladder != null && x.Ladder.activeSelf;
                            entry = Mk(type, wid, drained, false, false, 0, 0, 0, 0, x.level);
                            return true;
                        }
                    case PuzzleType.MED_CardWriter:
                        { var x = (MED_CardWriter)c; entry = Mk(type, wid, x.solved, x.hasCard, false, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.RES_Shutters:
                        { var x = (RES_Shutters)c; entry = Mk(type, wid, x.unlocked, false, false, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.ROT_Pipes:
                        {
                            var x = (ROT_Pipes)c;
                            bool off = x.loaded || (x.Blockers != null && !x.Blockers.activeSelf);
                            entry = Mk(type, wid, off, false, false, 0, 0, 0, 0, 0);
                            return true;
                        }
                    case PuzzleType.ROT_Magpie:
                        { var x = (ROT_Magpie)c; entry = Mk(type, wid, x.opened, false, false, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.PEN_Reaktor:
                        { var x = (PEN_Reaktor)c; entry = Mk(type, wid, x.solved, x.valid, false, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.DET_ServiceLock:
                        {
                            var x = (DET_ServiceLock)c;
                            bool ok = x.solved != null && x.solved.solved;
                            entry = Mk(type, wid, ok, false, false, 0, 0, 0, 0, 0);
                            return true;
                        }
                    case PuzzleType.EXC_Seilbahn:
                        { var x = (EXC_Seilbahn)c; entry = Mk(type, wid, x.down, false, false, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.EXC_Hatch:
                        {
                            var x = (EXC_Hatch)c;
                            bool open = x.Ladder != null && x.Ladder.activeSelf;
                            entry = Mk(type, wid, open, false, false, 0, 0, 0, 0, 0);
                            return true;
                        }
                    case PuzzleType.LAB_Rings:
                        { var x = (LAB_Rings)c; entry = Mk(type, wid, x.solved, false, false, 0, 0, 0, 0, 0); return true; }
                    case PuzzleType.BiodomeDoorLock:
                        { var x = (BiodomeDoorLock)c; entry = Mk(type, wid, !x.hasLock, false, false, x.KeyLevel, 0, 0, 0, 0); return true; }
                    case PuzzleType.ROT_MeatBlocker:
                        { var x = (ROT_MeatBlocker)c; entry = Mk(type, wid, !x.blocked, false, false, x.pickups, x.required, 0, 0, 0); return true; }
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
                            bool combat = EnemyManager.inCombat;
                            try
                            {
                                var enemies = UnityEngine.Object.FindObjectsOfType<EnemyController>();
                                if (enemies != null)
                                {
                                    for (int ei = 0; ei < enemies.Length; ei++)
                                    {
                                        var en = enemies[ei];
                                        if (en == null) continue;
                                        if (en.state == EnemyController.enemystate.attack)
                                        {
                                            combat = true;
                                            break;
                                        }
                                    }
                                }
                            }
                            catch { }
                            if (combat) bits |= 1;
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
                case PuzzleType.MED_Pump:
                case PuzzleType.MED_FloodedBathroom:
                case PuzzleType.MED_CardWriter:
                case PuzzleType.RES_Shutters:
                case PuzzleType.ROT_Pipes:
                case PuzzleType.ROT_Magpie:
                case PuzzleType.PEN_Reaktor:
                case PuzzleType.DET_ServiceLock:
                case PuzzleType.EXC_Seilbahn:
                case PuzzleType.EXC_Hatch:
                case PuzzleType.LAB_Rings:
                case PuzzleType.BiodomeDoorLock:
                case PuzzleType.ROT_MeatBlocker:
                case PuzzleType.UseItemMulti:
                case PuzzleType.FoldingShutterDoor:
                case PuzzleType.ROT_Tarot:
                case PuzzleType.ROT_Mural:
                case PuzzleType.MED_Incinerator:
                case PuzzleType.LAB_Waage:
                case PuzzleType.RES_Shrine:
                case PuzzleType.ROT_RadioAlignment:
                case PuzzleType.DET_RadioCodeLock:
                case PuzzleType.EXC_Elevator:
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
                case PuzzleType.MED_Pump:
                case PuzzleType.MED_FloodedBathroom:
                case PuzzleType.MED_CardWriter:
                case PuzzleType.RES_Shutters:
                case PuzzleType.ROT_Pipes:
                case PuzzleType.ROT_Magpie:
                case PuzzleType.PEN_Reaktor:
                case PuzzleType.DET_ServiceLock:
                case PuzzleType.EXC_Seilbahn:
                case PuzzleType.EXC_Hatch:
                case PuzzleType.LAB_Rings:
                case PuzzleType.BiodomeDoorLock:
                case PuzzleType.ROT_MeatBlocker:
                case PuzzleType.FlipSwitch:
                case PuzzleType.FloodControls:
                case PuzzleType.StorageBox:
                    return e.Bool0;
                case PuzzleType.EXC_Elevator:
                    return e.Bool0 || e.Bool1;
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

        /// <summary>Native OnEnable re-enables pad/open. Shut them in the same callback if already solved.</summary>
        public void HandlePenCryoEnabled(PEN_Cryo x)
        {
            if (x == null) return;
            ulong id = 0;
            try { id = WorldId.FromGameObject(x.gameObject); } catch { }
            bool open = false;
            try { open = x.opened; } catch { }
            if (!open && !IsHeld(PuzzleType.PEN_Cryo, id) && !CryoFamilyHeldUnmatched())
                return;
            if (!IsHeld(PuzzleType.PEN_Cryo, id))
                RemapHeld(PuzzleType.PEN_Cryo, id);
            NetGate.BeginApply();
            try { SnapPenCryo(x, playOpen: false); }
            finally { NetGate.EndApply(); }
        }

        public void HandleCryoLockEnabled(CryoDoorLock c)
        {
            if (c == null) return;
            ulong id = 0;
            try { id = WorldId.FromGameObject(c.gameObject); } catch { }
            bool done = false;
            try { done = c.done; } catch { }
            if (!done && !IsHeld(PuzzleType.CryoDoorLock, id))
            {
                try
                {
                    if (c.puzzle != null && c.puzzle.solved)
                        done = true;
                }
                catch { }
                try
                {
                    var pen = FindInParents<PEN_Cryo>(c.gameObject)
                        ?? (c.Door != null ? FindInParents<PEN_Cryo>(c.Door) : null);
                    if (pen != null && pen.opened) done = true;
                }
                catch { }
            }
            if (!done && !IsHeld(PuzzleType.CryoDoorLock, id) && !CryoFamilyHeldUnmatched())
                return;
            if (!IsHeld(PuzzleType.CryoDoorLock, id))
                RemapHeld(PuzzleType.CryoDoorLock, id);
            NetGate.BeginApply();
            try { SnapCryoLock(c, playAnim: false); }
            finally { NetGate.EndApply(); }
        }

        public void HandleCodepadEnabled(PEN_Codepad pad)
        {
            if (pad == null) return;
            ulong id = 0;
            try { id = WorldId.FromGameObject(pad.gameObject); } catch { }
            bool solved = false;
            try { solved = pad.solved; } catch { }
            if (!solved && !IsHeld(PuzzleType.PEN_Codepad, id))
            {
                try
                {
                    var cryo = FindInParents<PEN_Cryo>(pad.gameObject);
                    if (cryo != null && cryo.opened) solved = true;
                }
                catch { }
            }
            if (!solved && !IsHeld(PuzzleType.PEN_Codepad, id) && !CryoFamilyHeldUnmatched())
                return;
            if (!IsHeld(PuzzleType.PEN_Codepad, id))
                RemapHeld(PuzzleType.PEN_Codepad, id);
            NetGate.BeginApply();
            try { DisablePad(pad); }
            finally { NetGate.EndApply(); }
        }

        public void HandlePatternLockEnabled(LAB_PatternLock pad)
        {
            if (pad == null) return;
            ulong id = 0;
            try { id = WorldId.FromGameObject(pad.gameObject); } catch { }
            bool solved = false;
            try { solved = pad.solved; } catch { }
            if (!solved && !IsHeld(PuzzleType.PatternLock, id))
            {
                try
                {
                    var cryo = FindInParents<PEN_Cryo>(pad.gameObject);
                    if (cryo != null && cryo.opened) solved = true;
                }
                catch { }
            }
            if (!solved && !IsHeld(PuzzleType.PatternLock, id) && !CryoFamilyHeldUnmatched())
                return;
            if (!IsHeld(PuzzleType.PatternLock, id))
                RemapHeld(PuzzleType.PatternLock, id);
            NetGate.BeginApply();
            try { DisablePatternLock(pad); }
            finally { NetGate.EndApply(); }
        }

        public bool ShouldKillOverlay(Interaction it)
        {
            if (it == null) return false;
            GameObject go = null;
            try { go = it.gameObject; } catch { }
            if (go == null) return false;
            if (DroppedItemManager.IsDroppedGo(go)) return false;
            try
            {
                var pad = FindInParents<LAB_PatternLock>(go);
                if (pad != null)
                {
                    ulong id = WorldId.FromGameObject(pad.gameObject);
                    if (pad.solved || IsHeld(PuzzleType.PatternLock, id))
                        return true;
                }
            }
            catch { }
            try
            {
                var cryo = FindInParents<PEN_Cryo>(go);
                if (cryo != null)
                {
                    ulong id = WorldId.FromGameObject(cryo.gameObject);
                    if (cryo.opened || IsHeld(PuzzleType.PEN_Cryo, id))
                        return true;
                }
            }
            catch { }
            try
            {
                var doorLock = FindInParents<CryoDoorLock>(go);
                if (doorLock != null)
                {
                    ulong id = WorldId.FromGameObject(doorLock.gameObject);
                    if (doorLock.done || IsHeld(PuzzleType.CryoDoorLock, id))
                        return true;
                }
            }
            catch { }
            try
            {
                var code = FindInParents<PEN_Codepad>(go);
                if (code != null)
                {
                    ulong id = WorldId.FromGameObject(code.gameObject);
                    if (code.solved || IsHeld(PuzzleType.PEN_Codepad, id))
                        return true;
                }
            }
            catch { }
            return false;
        }

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
            net.SendPuzzleState(new[] { entry }, false);
        }

        /// <summary>
        /// solved()/Open() are coroutines — native flags are still false when the
        /// Harmony postfix runs. Force Bool0 so peers apply the world result now.
        /// </summary>
        public void EmitProgressed(PuzzleType type, ulong worldId)
        {
            if (worldId == 0 || NetGate.IsApplying) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            var entry = Mk(type, unchecked((long)worldId), true, false, false, 0, 0, 0, 0, 0f);
            if (!ChangedOrFirst(entry, false)) return;
            HoldIfProgressed(entry);
            PlaytestLog.Event("Puzzle", "emit progressed " + type + " id=" + worldId.ToString("X16"));
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
                        {
                            var x = Get<PuzzleStatus>(e.Type, e.WorldId);
                            if (x != null)
                            {
                                x.solved = e.Bool0;
                                if (e.Bool0) TryUnlockDoors(x.gameObject);
                            }
                            break;
                        }
                    case PuzzleType.InteractiveLock:
                        {
                            var x = Get<InteractiveLock>(e.Type, e.WorldId);
                            if (x != null)
                            {
                                bool was = x.locked;
                                x.locked = e.Bool0;
                                if (_mutateWorld && was && !e.Bool0)
                                {
                                    try { x.delayedOpen(); } catch { }
                                    TryUnlockDoors(x.gameObject);
                                }
                            }
                            break;
                        }
                    case PuzzleType.InteractiveLockSingle:
                        {
                            if (!_mutateWorld)
                                break;
                            var x = Get<InteractiveLockSingle>(e.Type, e.WorldId);
                            if (x == null) break;
                            try
                            {
                                if (DoorNative.IsFlavorSeal(x.gameObject)
                                    || (x.door != null && DoorNative.IsFlavorSeal(x.door.gameObject)))
                                    break;
                            }
                            catch { }
                            if (e.Bool0)
                            {
                                if (x.door != null) x.door.locked = true;
                                if (e.Bool1)
                                    DoorNative.ApplyLockPlate(x, true);
                            }
                            else
                            {
                                if (x.door != null)
                                {
                                    try { x.door.locked = false; } catch { }
                                    UnlockDoorObject(x.door.gameObject);
                                }
                                TryUnlockDoors(x.gameObject);
                                DoorNative.ApplyLockPlate(x, false);
                            }
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
                                    if (_mutateWorld && !was) { try { x.openDoor(); } catch { } }
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
                                    ApplyCodepadConsequences(x, cinematic);
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
                                if (e.Bool0)
                                {
                                    DisablePatternLock(x);
                                    TryUnlockDoors(x.gameObject);
                                }
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
                        {
                            var x = Get<FlipSwitch>(e.Type, e.WorldId);
                            if (x != null)
                            {
                                if (x.flipped != e.Bool0)
                                {
                                    if (_mutateWorld)
                                    {
                                        try { x.Flip(); }
                                        catch { x.flipped = e.Bool0; }
                                    }
                                    else
                                        x.flipped = e.Bool0;
                                }
                                if (e.Bool0) TryUnlockDoors(x.gameObject);
                            }
                            break;
                        }
                    case PuzzleType.FloodControlSwitch:
                        {
                            var x = Get<FloodControlSwitch>(e.Type, e.WorldId);
                            if (x != null)
                            {
                                x.state = e.Bool0;
                                if (e.Bool0) TryUnlockDoors(x.gameObject);
                            }
                            break;
                        }
                    case PuzzleType.FloodControls:
                        {
                            var x = Get<FloodControls>(e.Type, e.WorldId);
                            if (x != null)
                            {
                                x.done = e.Bool0;
                                x.locked = e.Bool1;
                                if (e.Bool0)
                                {
                                    if (_mutateWorld)
                                    {
                                        try { if (x.dlc != null) x.dlc.locked = false; } catch { }
                                    }
                                    TryUnlockDoors(x.gameObject);
                                }
                            }
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
                            if (x == null) break;
                            if (e.Bool0)
                                SnapUseItemWorld(x);
                            else if (!PerPlayerUse(x))
                                x.unlocked = false;
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
                        {
                            if (!_mutateWorld)
                                break;
                            var x = Get<Doorway_simple>(e.Type, e.WorldId);
                            if (x != null && (e.Bool0 || DoorNative.IsFlavorSeal(x.gameObject)))
                                x.locked = true;
                            break;
                        }
                    case PuzzleType.SwingDoor:
                        {
                            if (!_mutateWorld)
                                break;
                            var x = Get<SwingDoor>(e.Type, e.WorldId);
                            if (x != null) { x.Open = e.Bool0; x.locked = e.Bool1; }
                            break;
                        }
                    case PuzzleType.DoorLockControl:
                        {
                            if (!_mutateWorld)
                                break;
                            var x = Get<DoorLockControl>(e.Type, e.WorldId);
                            if (x != null && e.Bool0)
                                DoorNative.ApplyDoorLockControl(x, true);
                            break;
                        }
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
                                SnapCryoLock(x, cinematic);
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
                    case PuzzleType.MED_Pump:
                        {
                            var x = Get<MED_Pump>(e.Type, e.WorldId);
                            if (x != null)
                            {
                                try { x.a = e.Int0; x.b = e.Int1; x.c = e.Int2; } catch { }
                                if (e.Bool0)
                                    SnapMedPump(x, cinematic);
                            }
                            break;
                        }
                    case PuzzleType.MED_FloodedBathroom:
                        {
                            var x = Get<MED_FloodedBathroom>(e.Type, e.WorldId);
                            if (x != null && e.Bool0)
                                SnapFlood(x, cinematic);
                            break;
                        }
                    case PuzzleType.MED_CardWriter:
                        {
                            var x = Get<MED_CardWriter>(e.Type, e.WorldId);
                            if (x != null)
                                SnapCardWriter(x, e.Bool0, e.Bool1);
                            break;
                        }
                    case PuzzleType.RES_Shutters:
                        {
                            var x = Get<RES_Shutters>(e.Type, e.WorldId);
                            if (x != null && e.Bool0)
                                SnapShutters(x);
                            break;
                        }
                    case PuzzleType.ROT_Pipes:
                        {
                            var x = Get<ROT_Pipes>(e.Type, e.WorldId);
                            if (x != null && e.Bool0)
                                SnapPipes(x, cinematic);
                            break;
                        }
                    case PuzzleType.ROT_Magpie:
                        {
                            var x = Get<ROT_Magpie>(e.Type, e.WorldId);
                            if (x != null && e.Bool0)
                                SnapMagpie(x);
                            break;
                        }
                    case PuzzleType.PEN_Reaktor:
                        {
                            var x = Get<PEN_Reaktor>(e.Type, e.WorldId);
                            if (x != null && e.Bool0)
                                SnapReaktor(x);
                            break;
                        }
                    case PuzzleType.DET_ServiceLock:
                        {
                            var x = Get<DET_ServiceLock>(e.Type, e.WorldId);
                            if (x != null && e.Bool0)
                                SnapServiceLock(x);
                            break;
                        }
                    case PuzzleType.EXC_Seilbahn:
                        {
                            var x = Get<EXC_Seilbahn>(e.Type, e.WorldId);
                            if (x != null && e.Bool0)
                                SnapSeilbahn(x, cinematic);
                            break;
                        }
                    case PuzzleType.EXC_Hatch:
                        {
                            var x = Get<EXC_Hatch>(e.Type, e.WorldId);
                            if (x != null && e.Bool0)
                                SnapHatch(x, cinematic);
                            break;
                        }
                    case PuzzleType.LAB_Rings:
                        {
                            var x = Get<LAB_Rings>(e.Type, e.WorldId);
                            if (x != null && e.Bool0)
                                SnapLabRings(x);
                            break;
                        }
                    case PuzzleType.BiodomeDoorLock:
                        {
                            var x = Get<BiodomeDoorLock>(e.Type, e.WorldId);
                            if (x != null)
                                SnapBiodomeLock(x, e.Bool0, e.Int0);
                            break;
                        }
                    case PuzzleType.ROT_MeatBlocker:
                        {
                            var x = Get<ROT_MeatBlocker>(e.Type, e.WorldId);
                            if (x != null)
                                SnapMeatBlocker(x, e.Bool0, e.Int0);
                            break;
                        }
                    case PuzzleType.FoldingShutterDoor:
                        { var x = Get<FoldingShutterDoor>(e.Type, e.WorldId); if (x != null) x.open = e.Float0; break; }
                    case PuzzleType.InteractionTriggered:
                        {
                            var x = Get<Interaction>(e.Type, e.WorldId);
                            if (x != null && !IsLocalTraverse(x))
                            {
                                try
                                {
                                    if (x.GetComponent<ItemPickup>() != null)
                                        break;
                                }
                                catch { }
                                x.triggered = e.Bool0;
                            }
                            break;
                        }
                    case PuzzleType.EventZoneTriggered:
                        {
                            var x = Get<EventZone>(e.Type, e.WorldId);
                            if (x != null)
                            {
                                bool was = false;
                                try { was = x.triggered; } catch { }
                                x.triggered = e.Bool0;
                                if (e.Bool0 && !was)
                                {
                                    SyncRADation.Patches.EventZonePatch.MarkFired(unchecked((ulong)e.WorldId));
                                    if (_mutateWorld && LocalInspect.InLocalRoom(x.gameObject))
                                    {
                                        try { if (x.onInRange != null) x.onInRange.Invoke(); } catch { }
                                    }
                                    else
                                        PlaytestLog.Verbose("Puzzle", "skip EventZone invoke " + x.gameObject.name);
                                }
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
                            if (x != null)
                                SnapStorageLid(x, e.Bool0, cinematic);
                            break;
                        }
                    case PuzzleType.ROT_Tarot:
                        {
                            var x = Get<ROT_Tarot>(e.Type, e.WorldId);
                            if (x != null)
                            {
                                x.darkmode = e.Bool0;
                                x.FlipSwitchPos = e.Float0;
                            }
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
                                try { x.LoadState(); } catch { }
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
                            if (x != null)
                            {
                                x.riding = e.Bool0;
                                x.stopped = e.Bool1;
                                if (e.Bool1)
                                {
                                    try { x.stopInstant(); }
                                    catch
                                    {
                                        try
                                        {
                                            if (x.mover != null)
                                            {
                                                var p = x.mover.localPosition;
                                                p.y = x.distance;
                                                x.mover.localPosition = p;
                                            }
                                        }
                                        catch { }
                                    }
                                }
                            }
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
                PlaytestLog.Warn("Puzzle", "apply " + e.Type + ": " + ex.Message);
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

        internal static bool PerPlayerUse(UseItemInteraction x)
        {
            if (x == null) return false;
            try
            {
                if (LocalInspect.AirlockCinematic(x.gameObject)) return true;
            }
            catch { }
            try { return AirlockCinematic.IsPenTitlesCard(x); } catch { return false; }
        }

        public static void SnapUseItemWorld(UseItemInteraction x)
        {
            if (x == null) return;
            bool localUse = PerPlayerUse(x);
            if (!localUse)
            {
                try { x.unlocked = true; } catch { }
            }
            else
            {
                try { AirlockCinematic.NoteRemoteUnlock(x); } catch { }
            }
            try
            {
                if (x.inter != null)
                {
                    if (localUse)
                    {
                        x.inter.triggered = false;
                        x.inter.enabled = true;
                    }
                    else
                    {
                        x.inter.triggered = true;
                        x.inter.enabled = false;
                    }
                }
            }
            catch { }
            if (!_mutateWorld)
            {
                PlaytestLog.Verbose("Puzzle", "snap UseItem flags only " + x.gameObject.name);
                return;
            }
            try
            {
                if (x.slaveInteraction != null)
                {
                    x.slaveInteraction.enabled = true;
                    x.slaveInteraction.triggered = false;
                }
            }
            catch { }
            try
            {
                var lockComp = x.GetComponent<InteractiveLock>();
                if (lockComp != null) lockComp.locked = false;
            }
            catch { }
            TryUnlockDoors(x.gameObject);
            UnlockMatchingKeyLocks(x);
        }

        static bool SameKey(AnItem a, AnItem b)
        {
            if (a == null || b == null) return false;
            try { if (a == b) return true; } catch { }
            try { return a._item == b._item; } catch { return false; }
        }

        static void UnlockMatchingKeyLocks(UseItemInteraction x)
        {
            AnItem key = null;
            try { key = x.key; } catch { }
            if (key == null) return;
            GameObject root = x.gameObject;
            try
            {
                var room = FindInParents<Room>(x.gameObject);
                if (room != null) root = room.gameObject;
            }
            catch { }
            try
            {
                var singles = root.GetComponentsInChildren<InteractiveLockSingle>(true);
                if (singles != null)
                {
                    for (int i = 0; i < singles.Length; i++)
                    {
                        var s = singles[i];
                        if (s == null || !SameKey(s.key, key)) continue;
                        try
                        {
                            if (s.door != null && DoorNative.IsFlavorSeal(s.door.gameObject))
                                continue;
                        }
                        catch { }
                        try
                        {
                            if (s.door != null) s.door.locked = false;
                        }
                        catch { }
                        DoorNative.ApplyLockPlate(s, false);
                    }
                }
            }
            catch { }
            try
            {
                var locks = root.GetComponentsInChildren<InteractiveLock>(true);
                if (locks != null)
                {
                    for (int i = 0; i < locks.Length; i++)
                    {
                        var l = locks[i];
                        if (l == null) continue;
                        try
                        {
                            if (l.key == null || DoorNative.IsFlavorSeal(l.gameObject)) continue;
                            if (SameKey(l.key, key)) l.locked = false;
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        public static void ApplyCodepadConsequences(PEN_Codepad pad)
            => ApplyCodepadConsequences(pad, playAnim: true);

        static void ApplyCodepadConsequences(PEN_Codepad pad, bool playAnim)
        {
            if (pad == null) return;
            pad.solved = true;
            DisablePad(pad);
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
                    SnapCryoLock(c, playAnim);
                }
            }
            catch { }
        }

        static void DisablePatternLock(LAB_PatternLock pad)
        {
            if (pad == null) return;
            try { pad.solved = true; } catch { }
            DisableInteractions(pad);
            try
            {
                var ctrl = pad.GetComponent<Lab_PatternLockControl>()
                    ?? pad.GetComponentInChildren<Lab_PatternLockControl>(true);
                if (ctrl == null)
                    ctrl = FindInParents<Lab_PatternLockControl>(pad.gameObject);
                if (ctrl != null)
                {
                    DisableInteractions(ctrl);
                    try
                    {
                        if (ctrl._event != null)
                        {
                            DisableInteractions(ctrl._event.transform);
                            try { ctrl._event.SetActive(false); } catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        static void DisablePad(PEN_Codepad pad)
        {
            if (pad == null) return;
            try { pad.solved = true; } catch { }
            DisableInteractions(pad);
            try
            {
                var buttons = pad.buttons;
                if (buttons != null)
                {
                    for (int i = 0; i < buttons.Length; i++)
                        DisableOne(buttons[i]);
                }
            }
            catch { }
            try
            {
                var counter = pad.counterButtons;
                if (counter != null)
                {
                    for (int i = 0; i < counter.Length; i++)
                        DisableOne(counter[i]);
                }
            }
            catch { }
        }

        static void DisableInteractions(Component root)
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

        static void DisableOne(Interaction it)
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

        // World result only. Never EventScreen — that camera-locks the remote Elster.
        internal static void SnapCryoLock(CryoDoorLock c, bool playAnim = false)
        {
            if (c == null) return;

            PlaytestLog.Event("Puzzle", "snap CryoDoorLock " + c.gameObject.name
                + (playAnim ? " anim" : " pose"));
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
                if (c.Event != null)
                {
                    DisableInteractions(c.Event);
                    try { c.Event.enabled = false; } catch { }
                }
            }
            catch { }
            if (c.puzzle != null)
                DisablePad(c.puzzle);
            try
            {
                if (c.Door != null)
                {
                    if (_mutateWorld)
                        c.Door.SetActive(true);
                    UnlockDoorObject(c.Door);
                    TryOpenCryoController(c.Door, animate: playAnim);
                }
            }
            catch { }
            TryUnlockDoors(c.gameObject);
            try
            {
                var pen = c.GetComponent<PEN_Cryo>()
                    ?? FindInParents<PEN_Cryo>(c.gameObject)
                    ?? c.GetComponentInChildren<PEN_Cryo>(true);
                if (pen != null)
                    SnapPenCryo(pen, playAnim);
            }
            catch { }
        }

        static void TryOpenCryoController(GameObject door, bool animate)
        {
            if (door == null) return;
            CryoDoorController ctrl = null;
            try { ctrl = door.GetComponent<CryoDoorController>(); } catch { }
            if (ctrl == null)
            {
                try { ctrl = door.GetComponentInChildren<CryoDoorController>(true); } catch { }
            }
            if (ctrl == null) return;
            try
            {
                if (ctrl.open) return;
                if (animate)
                    ctrl.toggleDoors();
                else
                    ctrl.open = true;
            }
            catch { ctrl.open = true; }
        }

        static void SnapPenCryo(PEN_Cryo x, bool playOpen)
        {
            if (x == null) return;

            bool active = false;
            try { active = x.gameObject.activeInHierarchy; } catch { active = true; }
            ulong id = WorldId.FromGameObject(x.gameObject);
            bool animating = AnimStarted(PuzzleType.PEN_Cryo, id);

            if (!active || !playOpen)
                StampPenCryoOpen(x);

            if (!active)
            {
                PlaytestLog.Event("Puzzle", "snap PEN_Cryo " + x.gameObject.name + " wait inactive");
                DisableCryoAccess(x);
                return;
            }

            try { DisableOne(x.interaction); } catch { }
            try { DisableInteractions(x); } catch { }
            try
            {
                if (x.RoomDoor != null)
                    UnlockDoorObject(x.RoomDoor);
            }
            catch { }
            TryUnlockDoors(x.gameObject);

            if (animating)
            {
                PlaytestLog.Event("Puzzle", "snap PEN_Cryo " + x.gameObject.name + " skip");
                DisableCryoAccess(x);
                HideClaimedAround(x.gameObject);
                return;
            }

            if (playOpen)
            {
                if (id != 0)
                    NoteAnimStarted(PuzzleType.PEN_Cryo, id);
                PlaytestLog.Event("Puzzle", "snap PEN_Cryo " + x.gameObject.name + " open");
                try { x.Open(); }
                catch
                {
                    PosePenCryoOpen(x);
                    ActivateCryoContent(x);
                }
            }
            else
            {
                PlaytestLog.Event("Puzzle", "snap PEN_Cryo " + x.gameObject.name + " pose");
                PosePenCryoOpen(x);
                ActivateCryoContent(x);
            }

            DisableCryoAccess(x);
        }

        static void StampPenCryoOpen(PEN_Cryo x)
        {
            if (x == null) return;
            try { x.opened = true; } catch { }
            try { x.doorPos = 1f; } catch { }
            try { x.coverPos = 1f; } catch { }
            try { x.moverPos = 1f; } catch { }
            try { x.openerPos = 1f; } catch { }
            try { x.fluidPos = 1f; } catch { }
            try { x.brightness = 0f; } catch { }
        }

        static void ActivateCryoContent(PEN_Cryo x)
        {
            if (x == null) return;
            if (x.contentLateActivated != null)
            {
                try { x.contentLateActivated.SetActive(true); } catch { }
                try { RevealPickups(x.contentLateActivated); } catch { }
            }
            HideClaimedAround(x.gameObject);
        }

        static void HideClaimedAround(GameObject root)
        {
            try
            {
                var net = LanNetworkManager.Instance;
                if (net != null)
                    net.PickupSync.HideClaimed(root);
            }
            catch { }
        }

        static void DisableCryoAccess(PEN_Cryo x)
        {
            if (x == null) return;
            try
            {
                var lockGo = FindInParents<CryoDoorLock>(x.gameObject);
                if (lockGo == null)
                    lockGo = x.GetComponentInChildren<CryoDoorLock>(true);
                if (lockGo == null)
                {
                    var locks = UnityEngine.Object.FindObjectsOfType<CryoDoorLock>(true);
                    if (locks != null)
                    {
                        for (int i = 0; i < locks.Length; i++)
                        {
                            var c = locks[i];
                            if (c == null || c.Door == null) continue;
                            PEN_Cryo linked = null;
                            try { linked = c.Door.GetComponent<PEN_Cryo>(); } catch { }
                            if (linked == null)
                            {
                                try { linked = c.Door.GetComponentInChildren<PEN_Cryo>(true); } catch { }
                            }
                            if (linked == null)
                                linked = FindInParents<PEN_Cryo>(c.Door);
                            if (linked == x)
                            {
                                lockGo = c;
                                break;
                            }
                        }
                    }
                }
                if (lockGo != null)
                {
                    try { lockGo.done = true; } catch { }
                    try { DisableOne(lockGo.inter); } catch { }
                    try { DisableInteractions(lockGo); } catch { }
                    try
                    {
                        if (lockGo.Event != null)
                        {
                            DisableInteractions(lockGo.Event);
                            try { lockGo.Event.enabled = false; } catch { }
                        }
                    }
                    catch { }
                    if (lockGo.puzzle != null)
                        DisablePad(lockGo.puzzle);
                }
            }
            catch { }
            try
            {
                var pads = x.GetComponentsInChildren<PEN_Codepad>(true);
                if (pads != null)
                {
                    for (int i = 0; i < pads.Length; i++)
                        DisablePad(pads[i]);
                }
            }
            catch { }
            try
            {
                var parentPad = FindInParents<PEN_Codepad>(x.gameObject);
                if (parentPad != null)
                    DisablePad(parentPad);
            }
            catch { }
            try
            {
                var patterns = x.GetComponentsInChildren<LAB_PatternLock>(true);
                if (patterns != null)
                {
                    for (int i = 0; i < patterns.Length; i++)
                        DisablePatternLock(patterns[i]);
                }
            }
            catch { }
            try
            {
                var parentPat = FindInParents<LAB_PatternLock>(x.gameObject);
                if (parentPat != null)
                    DisablePatternLock(parentPat);
            }
            catch { }
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<LAB_PatternLock>(true);
                if (all != null)
                {
                    Vector3 origin = Vector3.zero;
                    try { origin = x.transform.position; } catch { }
                    for (int i = 0; i < all.Length; i++)
                    {
                        var p = all[i];
                        if (p == null) continue;
                        if (FindInParents<PEN_Cryo>(p.gameObject) == x)
                        {
                            DisablePatternLock(p);
                            continue;
                        }
                        try
                        {
                            if ((p.transform.position - origin).sqrMagnitude < 64f)
                                DisablePatternLock(p);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        static void PosePenCryoOpen(PEN_Cryo x)
        {
            if (x == null) return;
            StampPenCryoOpen(x);
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
            try
            {
                if (x.fluid != null)
                {
                    var p = x.fluid.localPosition;
                    p.y = x.fluidLevel;
                    x.fluid.localPosition = p;
                }
            }
            catch { }
            try
            {
                if (x.Fog != null)
                {
                    for (int i = 0; i < x.Fog.Length; i++)
                    {
                        try { if (x.Fog[i] != null) x.Fog[i].Stop(true); } catch { }
                    }
                }
            }
            catch { }
            try
            {
                if (x.Steam != null)
                {
                    for (int i = 0; i < x.Steam.Length; i++)
                    {
                        try { if (x.Steam[i] != null) x.Steam[i].Stop(true); } catch { }
                    }
                }
            }
            catch { }
        }

        static string AnimKey(PuzzleType type, ulong id) => ((byte)type) + "_" + id.ToString("X");

        static bool AnimStarted(PuzzleType type, ulong id)
            => id != 0 && _worldAnimStarted.Contains(AnimKey(type, id));

        static void NoteAnimStarted(PuzzleType type, ulong id)
        {
            if (id != 0) _worldAnimStarted.Add(AnimKey(type, id));
        }

        static bool TryStartWorldAnim(PuzzleType type, GameObject go)
        {
            if (go == null) return false;
            bool active = false;
            try { active = go.activeInHierarchy; } catch { active = true; }
            if (!active) return false;
            ulong id = WorldId.FromGameObject(go);
            if (id == 0) return true;
            return _worldAnimStarted.Add(AnimKey(type, id));
        }

        static void SnapStorageLid(StorageBox x, bool open, bool cinematic)
        {
            if (x == null) return;
            if (_storageBoxOpenField != null)
                _storageBoxOpenField.SetValue(x, open);
            if (!open) return;
            if (cinematic)
            {
                try { x.StartCoroutine("Open"); return; }
                catch { }
            }
            try
            {
                if (x.lid != null)
                    x.lid.localEulerAngles = new Vector3(-90f, 0f, 0f);
            }
            catch { }
        }

        static void SnapMedPump(MED_Pump x, bool play)
        {
            if (x == null) return;
            try { x.solved = true; } catch { }
            try
            {
                if (x.flood != null)
                    SnapFlood(x.flood, play);
            }
            catch { }
            TryUnlockDoors(x.gameObject);
        }

        static void SnapFlood(MED_FloodedBathroom x, bool play)
        {
            if (x == null) return;
            if (play && TryStartWorldAnim(PuzzleType.MED_FloodedBathroom, x.gameObject))
            {
                try { x.Drain(); }
                catch { PoseFlood(x); }
            }
            else
                PoseFlood(x);
        }

        static void PoseFlood(MED_FloodedBathroom x)
        {
            if (x == null) return;
            try { x.setLevel(x.endDepth); } catch { }
            try { x.level = x.endDepth; } catch { }
            try
            {
                if (x.waterTrans != null)
                {
                    var p = x.waterTrans.localPosition;
                    p.y = x.endDepth;
                    x.waterTrans.localPosition = p;
                }
            }
            catch { }
            try { if (x.Ladder != null) x.Ladder.SetActive(true); } catch { }
            try { if (x.ObservationFlood != null) x.ObservationFlood.SetActive(false); } catch { }
        }

        static void SnapCardWriter(MED_CardWriter x, bool solved, bool hasCard)
        {
            if (x == null) return;
            try { x.solved = solved; } catch { }
            try { x.hasCard = hasCard || solved; } catch { }
            if (!solved) return;
            try { if (x.insertCard != null) x.insertCard.SetActive(false); } catch { }
            try { if (x.insertCardPrompt != null) x.insertCardPrompt.SetActive(false); } catch { }
            try { if (x.pickUpBlank != null) x.pickUpBlank.SetActive(true); } catch { }
            try { if (x.tinyCard != null) x.tinyCard.SetActive(true); } catch { }
            try { RevealPickups(x.pickUpBlank); } catch { }
            try { RevealPickups(x.gameObject); } catch { }
        }

        static void SnapShutters(RES_Shutters x)
        {
            if (x == null) return;
            try { x.unlocked = true; } catch { }
            try { if (x.Shutter != null) x.Shutter.SetActive(false); } catch { }
            try { if (x.Handle != null) x.Handle.SetActive(false); } catch { }
            try
            {
                if (x._lock != null)
                    DoorNative.ApplyConnectedDoors(x._lock, false);
            }
            catch { }
            TryUnlockDoors(x.gameObject);
        }

        static void SnapPipes(ROT_Pipes x, bool play)
        {
            if (x == null) return;
            try { x.loaded = true; } catch { }
            if (play && TryStartWorldAnim(PuzzleType.ROT_Pipes, x.gameObject))
            {
                try { x.TurnValve(); }
                catch { PosePipes(x); }
            }
            else
                PosePipes(x);
        }

        static void PosePipes(ROT_Pipes x)
        {
            if (x == null) return;
            try { if (x.Blockers != null) x.Blockers.SetActive(false); } catch { }
            try { if (x.interaction != null) x.interaction.SetActive(false); } catch { }
            try
            {
                var leaks = x.leaks;
                if (leaks != null)
                {
                    for (int i = 0; i < leaks.Length; i++)
                    {
                        try { if (leaks[i] != null) leaks[i].Stop(); } catch { }
                    }
                }
            }
            catch { }
            try
            {
                var lights = x.lights;
                if (lights != null)
                {
                    for (int i = 0; i < lights.Length; i++)
                    {
                        try { if (lights[i] != null) lights[i].enabled = false; } catch { }
                    }
                }
            }
            catch { }
            try { if (x.loopSFX != null) x.loopSFX.Stop(); } catch { }
        }

        static void SnapMagpie(ROT_Magpie x)
        {
            if (x == null) return;
            try { x.opened = true; } catch { }
            try { if (x.CardPickup != null) x.CardPickup.SetActive(true); } catch { }
            try { if (x.BoxObs != null) x.BoxObs.SetActive(false); } catch { }
            try { RevealPickups(x.CardPickup); } catch { }
            try { RevealPickups(x.gameObject); } catch { }
        }

        static void SnapReaktor(PEN_Reaktor x)
        {
            if (x == null) return;
            try { x.solved = true; } catch { }
            try
            {
                if (x.doorLock != null)
                    DoorNative.ApplyConnectedDoors(x.doorLock, false);
            }
            catch { }
            try
            {
                var singles = x.GetComponentsInChildren<InteractiveLockSingle>(true);
                if (singles != null)
                {
                    for (int i = 0; i < singles.Length; i++)
                    {
                        if (singles[i] == null) continue;
                        try { if (singles[i].door != null) singles[i].door.locked = false; } catch { }
                        DoorNative.ApplyLockPlate(singles[i], false);
                    }
                }
            }
            catch { }
            try
            {
                if (x._event != null)
                {
                    DisableInteractions(x._event);
                    try { x._event.enabled = false; } catch { }
                }
            }
            catch { }
            TryUnlockDoors(x.gameObject);
        }

        static void SnapServiceLock(DET_ServiceLock x)
        {
            if (x == null) return;
            try
            {
                if (x.solved != null)
                    x.solved.solved = true;
            }
            catch { }
            DisableInteractions(x);
            try
            {
                var buttons = x.Buttons;
                if (buttons != null)
                {
                    for (int i = 0; i < buttons.Length; i++)
                        DisableOne(buttons[i]);
                }
            }
            catch { }
            try
            {
                var counter = x.CounterButtons;
                if (counter != null)
                {
                    for (int i = 0; i < counter.Length; i++)
                        DisableOne(counter[i]);
                }
            }
            catch { }
            try { DisableOne(x.TestButton); } catch { }
            TryUnlockDoors(x.gameObject);
        }

        static void SnapSeilbahn(EXC_Seilbahn x, bool play)
        {
            if (x == null) return;
            try { x.down = true; } catch { }
            try { if (x.interaction != null) x.interaction.SetActive(false); } catch { }
            try { if (x.Red != null) x.Red.SetActive(false); } catch { }
            try { if (x.Green != null) x.Green.SetActive(true); } catch { }
            if (play && TryStartWorldAnim(PuzzleType.EXC_Seilbahn, x.gameObject))
            {
                try { x.goDown(); }
                catch { }
            }
        }

        static void SnapHatch(EXC_Hatch x, bool play)
        {
            if (x == null) return;
            try { if (x.Inter != null) x.Inter.SetActive(false); } catch { }
            try { if (x.Ladder != null) x.Ladder.SetActive(true); } catch { }
            try
            {
                if (x.doorway != null)
                    DoorNative.ApplyConnectedDoors(x.doorway, false);
            }
            catch { }
            if (play && TryStartWorldAnim(PuzzleType.EXC_Hatch, x.gameObject))
            {
                try { x.OpenHatch(); }
                catch { }
            }
        }

        static void SnapLabRings(LAB_Rings x)
        {
            if (x == null) return;
            try { x.solved = true; } catch { }
            try { if (x.solvedState != null) x.solvedState.SetActive(true); } catch { }
            try { if (x.FakePlate != null) x.FakePlate.SetActive(false); } catch { }
            try { if (x.PlatePickup != null) x.PlatePickup.SetActive(true); } catch { }
            try { RevealPickups(x.PlatePickup); } catch { }
            try { RevealPickups(x.gameObject); } catch { }
            TryUnlockDoors(x.gameObject);
        }

        static void SnapBiodomeLock(BiodomeDoorLock x, bool unlocked, int keyLevel)
        {
            if (x == null) return;
            try { x.KeyLevel = keyLevel; } catch { }
            try { x.hasLock = !unlocked; } catch { }
            try { x.setSprites(); } catch { }
            if (unlocked)
            {
                try
                {
                    if (x.door != null)
                        x.door.locked = false;
                }
                catch { }
                TryUnlockDoors(x.gameObject);
            }
        }

        static void SnapMeatBlocker(ROT_MeatBlocker x, bool unblocked, int pickups)
        {
            if (x == null) return;
            try { x.pickups = pickups; } catch { }
            try { x.blocked = !unblocked; } catch { }
            if (!unblocked) return;
            try
            {
                var blockers = x.Blockers;
                if (blockers != null)
                {
                    for (int i = 0; i < blockers.Length; i++)
                    {
                        try { if (blockers[i] != null) blockers[i].SetActive(false); } catch { }
                    }
                }
            }
            catch { }
            try
            {
                var open = x.UnBlockers;
                if (open != null)
                {
                    for (int i = 0; i < open.Length; i++)
                    {
                        try { if (open[i] != null) open[i].SetActive(true); } catch { }
                    }
                }
            }
            catch { }
            try
            {
                var locks = x.Locks;
                if (locks != null)
                {
                    for (int i = 0; i < locks.Length; i++)
                    {
                        try
                        {
                            if (locks[i] != null)
                                DoorNative.ApplyConnectedDoors(locks[i], false);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        internal static void RevealPickups(GameObject root)
        {
            if (root == null || !_mutateWorld) return;
            try
            {
                var picks = root.GetComponentsInChildren<ItemPickup>(true);
                if (picks != null)
                {
                    for (int i = 0; i < picks.Length; i++)
                    {
                        var p = picks[i];
                        if (p == null) continue;
                        ulong pid = 0;
                        try { pid = WorldId.FromGameObject(p.gameObject); } catch { }
                        try
                        {
                            var netClaim = LanNetworkManager.Instance;
                            if (netClaim != null && pid != 0 && (netClaim.PickupSync.IsClaimed(pid) || netClaim.PickupSync.IsClaimedPickup(p)))
                            {
                                netClaim.PickupSync.HidePickup(p);
                                continue;
                            }
                        }
                        catch { }
                        try { p.triggered = false; } catch { }
                        try { p.gameObject.SetActive(true); } catch { }
                        try { p.enabled = true; } catch { }
                        try
                        {
                            var it = p.GetComponent<Interaction>();
                            if (it != null)
                            {
                                it.enabled = true;
                                it.triggered = false;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            try
            {
                var net = LanNetworkManager.Instance;
                if (net != null)
                    net.PickupSync.NotifyRevealed();
            }
            catch { }
        }

        static void UnlockDoorObject(GameObject door)
        {
            if (door == null || !_mutateWorld) return;
            if (DoorNative.IsFlavorSeal(door))
            {
                PlaytestLog.Verbose("Puzzle", "skip unlock flavor seal " + door.name);
                return;
            }
            TryUnlockDoors(door);
            try
            {
                var dlc = door.GetComponent<DoorLockControl>();
                if (dlc != null)
                    DoorNative.UnsealDoorLockControl(dlc);
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
                if (cd != null && DoorNative.AllowUnlock(cd))
                    DoorNative.ApplyConnectedDoors(cd, false);
            }
            catch { }
        }

        internal static void TryUnlockDoors(GameObject go)
        {
            if (go == null || !_mutateWorld) return;
            try
            {
                var cd = FindInParents<ConnectedDoors>(go);
                if (cd != null && cd.locked && DoorNative.AllowUnlock(cd))
                    DoorNative.ApplyConnectedDoors(cd, false);
            }
            catch { }
        }
    }
}
