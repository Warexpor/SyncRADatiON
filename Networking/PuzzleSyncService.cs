// SyncRADation � puzzle/interactive object state sync (33+ types), host diff every 0.5s
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace SyncRADation.Networking
{
    public sealed class PuzzleSyncService
    {
        private float _sendTimer;
        private const float SendInterval = 0.5f;

        private bool _scanned;
        private bool _needFullSend = true;

        // Store host-side component lists
        private PuzzleStatus[] _puzzleStatuses;
        private InteractiveLock[] _interactiveLocks;
        private InteractiveLockSingle[] _interactiveLockSingles;
        private Keypad3D[] _keypads;
        private ROT_Keypad[] _rotKeypads;
        private PEN_Codepad[] _penCodepads;
        private LAB_PatternLock[] _patternLocks;
        private ROT_DialLock[] _dialLocks;
        private FlipSwitch[] _flipSwitches;
        private FloodControlSwitch[] _floodControlSwitches;
        private FloodControls[] _floodControlsList;
        private RES_Power[] _resPowers;
        private UseItemInteraction[] _useItemInteractions;
        private NumberLockNew[] _numberLocks;
        private DoorLockPuzzle[] _doorLockPuzzles;
        private MED_MultiLock[] _medMultiLocks;
        private LAB_MultiLock[] _labMultiLocks;
        private MED_VentPuzzle[] _ventPuzzles;
        private Doorway_simple[] _simpleDoors;
        private SwingDoor[] _swingDoors;
        private DoorLockControl[] _doorLockControls;
        private EvidenceLockerLogicPuzzle[] _evidenceLockerPuzzles;
        private RadioStationTutorialPuzzle[] _tutorialPuzzles;
        private CentralElevatorControl[] _elevatorControls;
        private ElevatorCallButton[] _elevatorButtons;
        private DoorLockEventInteraction[] _doorLockEvents;
        private MultiConditionEvent[] _multiConditionEvents;
        private CryoDoorController[] _cryoDoors;
        private FoldingShutterDoor[] _foldingDoors;
        private Interaction[] _interactions;
        private EventZone[] _eventZones;
        private EnemyManager[] _enemyManagers;
        private StorageBox[] _storageBoxes;

        // Diff tracking
        private readonly Dictionary<string, PuzzleStateEntry> _lastSent = new Dictionary<string, PuzzleStateEntry>();

        private static FieldInfo _storageBoxOpenField;

        public void RefreshScene()
        {
            _scanned = false;
            _lastSent.Clear();
            _needFullSend = true;
            ModRuntime.Log?.Msg("[PuzzleSync] Scene refreshed");
        }

        private void EnsureScanned()
        {
            if (_scanned) return;

            _puzzleStatuses = GameObject.FindObjectsOfType<PuzzleStatus>();
            _interactiveLocks = GameObject.FindObjectsOfType<InteractiveLock>();
            _interactiveLockSingles = GameObject.FindObjectsOfType<InteractiveLockSingle>();
            _keypads = GameObject.FindObjectsOfType<Keypad3D>();
            _rotKeypads = GameObject.FindObjectsOfType<ROT_Keypad>();
            _penCodepads = GameObject.FindObjectsOfType<PEN_Codepad>();
            _patternLocks = GameObject.FindObjectsOfType<LAB_PatternLock>();
            _dialLocks = GameObject.FindObjectsOfType<ROT_DialLock>();
            _flipSwitches = GameObject.FindObjectsOfType<FlipSwitch>();
            _floodControlSwitches = GameObject.FindObjectsOfType<FloodControlSwitch>();
            _floodControlsList = GameObject.FindObjectsOfType<FloodControls>();
            _resPowers = GameObject.FindObjectsOfType<RES_Power>();
            _useItemInteractions = GameObject.FindObjectsOfType<UseItemInteraction>();
            _numberLocks = GameObject.FindObjectsOfType<NumberLockNew>();
            _doorLockPuzzles = GameObject.FindObjectsOfType<DoorLockPuzzle>();
            _medMultiLocks = GameObject.FindObjectsOfType<MED_MultiLock>();
            _labMultiLocks = GameObject.FindObjectsOfType<LAB_MultiLock>();
            _ventPuzzles = GameObject.FindObjectsOfType<MED_VentPuzzle>();
            _simpleDoors = GameObject.FindObjectsOfType<Doorway_simple>();
            _swingDoors = GameObject.FindObjectsOfType<SwingDoor>();
            _doorLockControls = GameObject.FindObjectsOfType<DoorLockControl>();
            _evidenceLockerPuzzles = GameObject.FindObjectsOfType<EvidenceLockerLogicPuzzle>();
            _tutorialPuzzles = GameObject.FindObjectsOfType<RadioStationTutorialPuzzle>();
            _elevatorControls = GameObject.FindObjectsOfType<CentralElevatorControl>();
            _elevatorButtons = GameObject.FindObjectsOfType<ElevatorCallButton>();
            _doorLockEvents = GameObject.FindObjectsOfType<DoorLockEventInteraction>();
            _multiConditionEvents = GameObject.FindObjectsOfType<MultiConditionEvent>();
            _cryoDoors = GameObject.FindObjectsOfType<CryoDoorController>();
            _foldingDoors = GameObject.FindObjectsOfType<FoldingShutterDoor>();
            _interactions = GameObject.FindObjectsOfType<Interaction>();
            _eventZones = GameObject.FindObjectsOfType<EventZone>();
            _enemyManagers = GameObject.FindObjectsOfType<EnemyManager>();
            _storageBoxes = GameObject.FindObjectsOfType<StorageBox>();

            _scanned = true;
            int total = CountAll();
            ModRuntime.Log?.Msg("[PuzzleSync] Scanned " + total + " puzzle components");
        }

        private int CountAll()
        {
            int c = 0;
            if (_puzzleStatuses != null) c += _puzzleStatuses.Length;
            if (_interactiveLocks != null) c += _interactiveLocks.Length;
            if (_interactiveLockSingles != null) c += _interactiveLockSingles.Length;
            if (_keypads != null) c += _keypads.Length;
            if (_rotKeypads != null) c += _rotKeypads.Length;
            if (_penCodepads != null) c += _penCodepads.Length;
            if (_patternLocks != null) c += _patternLocks.Length;
            if (_dialLocks != null) c += _dialLocks.Length;
            if (_flipSwitches != null) c += _flipSwitches.Length;
            if (_floodControlSwitches != null) c += _floodControlSwitches.Length;
            if (_floodControlsList != null) c += _floodControlsList.Length;
            if (_resPowers != null) c += _resPowers.Length;
            if (_useItemInteractions != null) c += _useItemInteractions.Length;
            if (_numberLocks != null) c += _numberLocks.Length;
            if (_doorLockPuzzles != null) c += _doorLockPuzzles.Length;
            if (_medMultiLocks != null) c += _medMultiLocks.Length;
            if (_labMultiLocks != null) c += _labMultiLocks.Length;
            if (_ventPuzzles != null) c += _ventPuzzles.Length;
            if (_simpleDoors != null) c += _simpleDoors.Length;
            if (_swingDoors != null) c += _swingDoors.Length;
            if (_doorLockControls != null) c += _doorLockControls.Length;
            if (_evidenceLockerPuzzles != null) c += _evidenceLockerPuzzles.Length;
            if (_tutorialPuzzles != null) c += _tutorialPuzzles.Length;
            if (_elevatorControls != null) c += _elevatorControls.Length;
            if (_elevatorButtons != null) c += _elevatorButtons.Length;
            if (_doorLockEvents != null) c += _doorLockEvents.Length;
            if (_multiConditionEvents != null) c += _multiConditionEvents.Length;
            if (_cryoDoors != null) c += _cryoDoors.Length;
            if (_foldingDoors != null) c += _foldingDoors.Length;
            if (_interactions != null) c += _interactions.Length;
            if (_eventZones != null) c += _eventZones.Length;
            if (_enemyManagers != null) c += _enemyManagers.Length;
            if (_storageBoxes != null) c += _storageBoxes.Length;
            return c;
        }

        public void TickHost(LanNetworkManager net)
        {
            if (net.Role != NetworkRole.Host) return;
            if (!net.IsConnected) return;

            _sendTimer += Mathf.Min(Time.deltaTime, 0.1f);
            if (_sendTimer < SendInterval && !_needFullSend) return;
            _sendTimer = 0f;

            EnsureScanned();

            var entries = new List<PuzzleStateEntry>();
            bool fullRefresh = _needFullSend;
            ReadAll(entries, fullRefresh);
            _needFullSend = false;

            if (entries.Count == 0) return;
            net.SendPuzzleState(entries.ToArray(), fullRefresh);
        }

        private void ReadAll(List<PuzzleStateEntry> entries, bool fullRefresh)
        {
            for (int i = 0; i < Len(_puzzleStatuses); i++)
            {
                var ps = _puzzleStatuses[i];
                if (ps == null) continue;
                var entry = Mk(PuzzleType.PuzzleStatus, (short)i, ps.solved, false, false, 0, 0, 0, 0, 0);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }

            for (int i = 0; i < Len(_interactiveLocks); i++)
            {
                var l = _interactiveLocks[i];
                if (l == null) continue;
                var entry = Mk(PuzzleType.InteractiveLock, (short)i, l.locked, false, false, 0, 0, 0, 0, 0);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }

            for (int i = 0; i < Len(_interactiveLockSingles); i++)
            {
                var l = _interactiveLockSingles[i];
                if (l == null) continue;
                bool locked = l.timedOut || l.door == null || l.door.locked;
                var entry = Mk(PuzzleType.InteractiveLockSingle, (short)i, locked, false, false, 0, 0, 0, 0, 0);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }

            for (int i = 0; i < Len(_keypads); i++)
            {
                var k = _keypads[i];
                if (k == null) continue;
                var entry = Mk(PuzzleType.Keypad3D, (short)i, k.solved || k.opening, k.opening, k.blocked, 0, 0, 0, 0, 0);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }

            for (int i = 0; i < Len(_rotKeypads); i++)
            {
                var k = _rotKeypads[i];
                if (k == null) continue;
                var entry = Mk(PuzzleType.ROT_Keypad, (short)i, k.solved || k.opening, k.opening, k.blocked, 0, 0, 0, 0, 0);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }

            for (int i = 0; i < Len(_penCodepads); i++)
            {
                var p = _penCodepads[i];
                if (p == null) continue;
                var entry = Mk(PuzzleType.PEN_Codepad, (short)i, p.solved, false, false, 0, 0, 0, 0, 0);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }

            for (int i = 0; i < Len(_patternLocks); i++)
            {
                var pl = _patternLocks[i];
                if (pl == null) continue;
                var entry = Mk(PuzzleType.PatternLock, (short)i, pl.solved, false, false, 0, 0, 0, 0, 0);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }

            for (int i = 0; i < Len(_dialLocks); i++)
            {
                var d = _dialLocks[i];
                if (d == null) continue;
                var entry = Mk(PuzzleType.DialLock, (short)i, d.solved, false, false, d.A, d.B, d.C, d.D, 0);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }

            for (int i = 0; i < Len(_flipSwitches); i++)
            {
                var fs = _flipSwitches[i];
                if (fs == null) continue;
                var entry = Mk(PuzzleType.FlipSwitch, (short)i, fs.flipped, false, false, 0, 0, 0, 0, 0);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }

            for (int i = 0; i < Len(_floodControlSwitches); i++)
            {
                var fcs = _floodControlSwitches[i];
                if (fcs == null) continue;
                var entry = Mk(PuzzleType.FloodControlSwitch, (short)i, fcs.state, false, false, 0, 0, 0, 0, 0);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }

            for (int i = 0; i < Len(_floodControlsList); i++)
            {
                var fc = _floodControlsList[i];
                if (fc == null) continue;
                var entry = Mk(PuzzleType.FloodControls, (short)i, fc.done, fc.locked, false, 0, 0, 0, 0, 0);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }

            for (int i = 0; i < Len(_resPowers); i++)
            {
                var rp = _resPowers[i];
                if (rp == null) continue;
                var entry = Mk(PuzzleType.RES_Power, (short)i, rp.solved, rp.powered, false, 0, 0, 0, 0, 0);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }

            for (int i = 0; i < Len(_useItemInteractions); i++)
            {
                var uii = _useItemInteractions[i];
                if (uii == null) continue;
                var entry = Mk(PuzzleType.UseItemInteraction, (short)i, uii.unlocked, false, false, 0, 0, 0, 0, 0);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }

            for (int i = 0; i < Len(_numberLocks); i++)
            {
                var nl = _numberLocks[i];
                if (nl == null) continue;
                var entry = Mk(PuzzleType.NumberLockNew, (short)i, nl.locked, false, false, 0, 0, 0, 0, 0);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }

            for (int i = 0; i < Len(_doorLockPuzzles); i++)
            {
                var dlp = _doorLockPuzzles[i];
                if (dlp == null) continue;
                var entry = Mk(PuzzleType.DoorLockPuzzle, (short)i, dlp.locked, false, false, 0, 0, 0, 0, 0);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }

            // MED_MultiLock
            for (int i = 0; i < Len(_medMultiLocks); i++)
            {
                var ml = _medMultiLocks[i];
                if (ml == null) continue;
                var entry = Mk(PuzzleType.MultiLock, (short)i, ml.unlocked, false, false, 0, 0, 0, 0, 0);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }
            // LAB_MultiLock — continue index from MED_MultiLock
            int medCount = Len(_medMultiLocks);
            for (int i = 0; i < Len(_labMultiLocks); i++)
            {
                var ml = _labMultiLocks[i];
                if (ml == null) continue;
                var entry = Mk(PuzzleType.MultiLock, (short)(medCount + i), ml.unlocked, false, false, 0, 0, 0, 0, 0);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }

            for (int i = 0; i < Len(_ventPuzzles); i++)
            {
                var vp = _ventPuzzles[i];
                if (vp == null) continue;
                var entry = Mk(PuzzleType.MED_VentPuzzle, (short)i, vp.uncovered, false, false, 0, 0, 0, 0, 0);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }

            for (int i = 0; i < Len(_simpleDoors); i++)
            {
                var sd = _simpleDoors[i];
                if (sd == null) continue;
                var entry = Mk(PuzzleType.DoorwaySimple, (short)i, sd.locked, false, false, 0, 0, 0, 0, 0);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }

            for (int i = 0; i < Len(_swingDoors); i++)
            {
                var sw = _swingDoors[i];
                if (sw == null) continue;
                var entry = Mk(PuzzleType.SwingDoor, (short)i, sw.Open, sw.locked, false, 0, 0, 0, 0, 0);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }

            for (int i = 0; i < Len(_doorLockControls); i++)
            {
                var dlc = _doorLockControls[i];
                if (dlc == null) continue;
                var entry = Mk(PuzzleType.DoorLockControl, (short)i, dlc.locked, false, false, 0, 0, 0, 0, 0);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }

            for (int i = 0; i < Len(_evidenceLockerPuzzles); i++)
            {
                var elp = _evidenceLockerPuzzles[i];
                if (elp == null) continue;
                var entry = Mk(PuzzleType.EvidenceLockerPuzzle, (short)i, elp.solved, false, false, 0, 0, 0, 0, 0);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }

            for (int i = 0; i < Len(_tutorialPuzzles); i++)
            {
                var rtp = _tutorialPuzzles[i];
                if (rtp == null) continue;
                var entry = Mk(PuzzleType.RadioStationTutorial, (short)i, rtp.solved, false, false, 0, 0, 0, 0, 0);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }

            for (int i = 0; i < Len(_elevatorControls); i++)
            {
                var ec = _elevatorControls[i];
                if (ec == null) continue;
                var entry = Mk(PuzzleType.CentralElevator, (short)i, false, false, false, ec.floor, (int)ec.state, 0, 0, 0);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }

            for (int i = 0; i < Len(_elevatorButtons); i++)
            {
                var eb = _elevatorButtons[i];
                if (eb == null) continue;
                var entry = Mk(PuzzleType.ElevatorCallButton, (short)i, eb.called, false, false, 0, 0, 0, 0, 0);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }

            for (int i = 0; i < Len(_doorLockEvents); i++)
            {
                var dle = _doorLockEvents[i];
                if (dle == null) continue;
                var entry = Mk(PuzzleType.DoorLockEventInteraction, (short)i, dle.done, false, false, 0, 0, 0, 0, 0);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }

            for (int i = 0; i < Len(_multiConditionEvents); i++)
            {
                var mce = _multiConditionEvents[i];
                if (mce == null) continue;
                var entry = Mk(PuzzleType.MultiConditionEvent, (short)i, false, false, false, mce.tried, 0, 0, 0, 0);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }

            for (int i = 0; i < Len(_cryoDoors); i++)
            {
                var cd = _cryoDoors[i];
                if (cd == null) continue;
                var entry = Mk(PuzzleType.CryoDoorController, (short)i, cd.open, false, false, 0, 0, 0, 0, 0);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }

            for (int i = 0; i < Len(_foldingDoors); i++)
            {
                var fd = _foldingDoors[i];
                if (fd == null) continue;
                var entry = Mk(PuzzleType.FoldingShutterDoor, (short)i, false, false, false, 0, 0, 0, 0, fd.open);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }

            // Interaction.triggered (one-shot interactions)
            for (int i = 0; i < Len(_interactions); i++)
            {
                var it = _interactions[i];
                if (it == null) continue;
                var entry = Mk(PuzzleType.InteractionTriggered, (short)i, it.triggered, false, false, 0, 0, 0, 0, 0);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }

            // EventZone.triggered
            for (int i = 0; i < Len(_eventZones); i++)
            {
                var ez = _eventZones[i];
                if (ez == null) continue;
                var entry = Mk(PuzzleType.EventZoneTriggered, (short)i, ez.triggered, false, false, 0, 0, 0, 0, 0);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }

            // GlobalAlertStatus (static, single entry at index 0)
            int alarmVal = (int)GlobalAlertStatus.currentStatus;
            var alarmEntry = Mk(PuzzleType.GlobalAlertStatus, 0, false, false, false, alarmVal, 0, 0, 0, 0);
            if (ChangedOrFirst(alarmEntry, fullRefresh)) entries.Add(alarmEntry);

            // RadioManager (static fields, single entry at index 0)
            int radioBools = 0;
            if (RadioManager.moduleInstalled) radioBools |= 1;
            if (RadioManager.tuner) radioBools |= 2;
            if (RadioManager.tuning) radioBools |= 4;
            if (RadioManager.signal) radioBools |= 8;
            if (RadioManager.data) radioBools |= 16;
            if (RadioManager.audible) radioBools |= 32;
            var radioEntry = Mk(PuzzleType.RadioManagerState, 0,
                radioBools != 0, false, false,
                radioBools, 0, 0, 0,
                RadioManager.frequency);
            if (ChangedOrFirst(radioEntry, fullRefresh)) entries.Add(radioEntry);

            // StorageBox.open (private bool, use reflection)
            for (int i = 0; i < Len(_storageBoxes); i++)
            {
                var sb = _storageBoxes[i];
                if (sb == null) continue;
                if (_storageBoxOpenField == null)
                    _storageBoxOpenField = typeof(StorageBox).GetField("open", BindingFlags.NonPublic | BindingFlags.Instance);
                bool open = _storageBoxOpenField != null && (bool)_storageBoxOpenField.GetValue(sb);
                var entry = Mk(PuzzleType.StorageBox, (short)i, open, false, false, 0, 0, 0, 0, 0);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }

            // EnemyManager (per-instance cleared/inOperation + static inCombat/enemyPresence)
            for (int i = 0; i < Len(_enemyManagers); i++)
            {
                var em = _enemyManagers[i];
                if (em == null) continue;
                int staticBits = 0;
                if (EnemyManager.inCombat) staticBits |= 1;
                if (EnemyManager.enemyPresence) staticBits |= 2;
                var entry = Mk(PuzzleType.EnemyManagerState, (short)i, em.cleared, em.inOperation, false, staticBits, 0, 0, 0, 0);
                if (ChangedOrFirst(entry, fullRefresh)) entries.Add(entry);
            }
        }

        private static PuzzleStateEntry Mk(PuzzleType type, short index, bool b0, bool b1, bool b2, int i0, int i1, int i2, int i3, float f0)
        {
            return new PuzzleStateEntry
            {
                Type = type,
                Index = index,
                Bool0 = b0, Bool1 = b1, Bool2 = b2,
                Int0 = i0, Int1 = i1, Int2 = i2, Int3 = i3,
                Float0 = f0
            };
        }

        private bool ChangedOrFirst(PuzzleStateEntry entry, bool fullRefresh)
        {
            string key = (byte)entry.Type + "_" + entry.Index;
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

        // --- Client-side apply ---

        public void ApplyPuzzleState(PuzzleStateMessage msg)
        {
            if (msg.Entries == null || msg.Entries.Length == 0) return;
            EnsureScanned();
            for (int ei = 0; ei < msg.Entries.Length; ei++)
                ApplyEntry(msg.Entries[ei]);
        }

        private void ApplyEntry(PuzzleStateEntry e)
        {
            switch (e.Type)
            {
                case PuzzleType.PuzzleStatus: ApplyBool(_puzzleStatuses, e.Index, (x, v) => x.solved = v, e.Bool0); break;
                case PuzzleType.InteractiveLock: ApplyBool(_interactiveLocks, e.Index, (x, v) => x.locked = v, e.Bool0); break;
                case PuzzleType.InteractiveLockSingle:
                    var ils = SafeAt(_interactiveLockSingles, e.Index);
                    if (ils != null) { ils.timedOut = e.Bool0; if (ils.door != null) ils.door.locked = e.Bool0; }
                    break;
                case PuzzleType.Keypad3D:
                    var k3 = SafeAt(_keypads, e.Index);
                    if (k3 != null) { k3.solved = e.Bool0; k3.opening = e.Bool1; k3.blocked = e.Bool2; }
                    break;
                case PuzzleType.ROT_Keypad:
                    var rk = SafeAt(_rotKeypads, e.Index);
                    if (rk != null) { rk.solved = e.Bool0; rk.opening = e.Bool1; rk.blocked = e.Bool2; }
                    break;
                case PuzzleType.PEN_Codepad: ApplyBool(_penCodepads, e.Index, (x, v) => x.solved = v, e.Bool0); break;
                case PuzzleType.PatternLock: ApplyBool(_patternLocks, e.Index, (x, v) => x.solved = v, e.Bool0); break;
                case PuzzleType.DialLock:
                    var dl = SafeAt(_dialLocks, e.Index);
                    if (dl != null)
                    {
                        dl.solved = e.Bool0;
                        dl.A = e.Int0; dl.B = e.Int1; dl.C = e.Int2; dl.D = e.Int3;
                        // Sync visual dial rotation (36° per step, 0-9)
                        if (dl.a != null) dl.a.localEulerAngles = new Vector3(0, e.Int0 * 36, 0);
                        if (dl.b != null) dl.b.localEulerAngles = new Vector3(0, e.Int1 * 36, 0);
                        if (dl.c != null) dl.c.localEulerAngles = new Vector3(0, e.Int2 * 36, 0);
                        if (dl.d != null) dl.d.localEulerAngles = new Vector3(0, e.Int3 * 36, 0);
                    }
                    break;
                case PuzzleType.FlipSwitch: ApplyBool(_flipSwitches, e.Index, (x, v) => x.flipped = v, e.Bool0); break;
                case PuzzleType.FloodControlSwitch: ApplyBool(_floodControlSwitches, e.Index, (x, v) => x.state = v, e.Bool0); break;
                case PuzzleType.FloodControls:
                    var fc = SafeAt(_floodControlsList, e.Index);
                    if (fc != null) { fc.done = e.Bool0; fc.locked = e.Bool1; }
                    break;
                case PuzzleType.RES_Power:
                    var rp = SafeAt(_resPowers, e.Index);
                    if (rp != null) { rp.solved = e.Bool0; rp.powered = e.Bool1; }
                    break;
                case PuzzleType.UseItemInteraction: ApplyBool(_useItemInteractions, e.Index, (x, v) => x.unlocked = v, e.Bool0); break;
                case PuzzleType.NumberLockNew: ApplyBool(_numberLocks, e.Index, (x, v) => x.locked = v, e.Bool0); break;
                case PuzzleType.DoorLockPuzzle: ApplyBool(_doorLockPuzzles, e.Index, (x, v) => x.locked = v, e.Bool0); break;
                case PuzzleType.MultiLock:
                    var mlMed = SafeAt(_medMultiLocks, e.Index);
                    if (mlMed != null) { mlMed.unlocked = e.Bool0; break; }
                    int offset = Len(_medMultiLocks);
                    var mlLab = SafeAt(_labMultiLocks, e.Index - offset);
                    if (mlLab != null) mlLab.unlocked = e.Bool0;
                    break;
                case PuzzleType.MED_VentPuzzle: ApplyBool(_ventPuzzles, e.Index, (x, v) => x.uncovered = v, e.Bool0); break;
                case PuzzleType.DoorwaySimple: ApplyBool(_simpleDoors, e.Index, (x, v) => x.locked = v, e.Bool0); break;
                case PuzzleType.SwingDoor:
                    var sw = SafeAt(_swingDoors, e.Index);
                    if (sw != null) { sw.Open = e.Bool0; sw.locked = e.Bool1; }
                    break;
                case PuzzleType.DoorLockControl: ApplyBool(_doorLockControls, e.Index, (x, v) => x.locked = v, e.Bool0); break;
                case PuzzleType.EvidenceLockerPuzzle: ApplyBool(_evidenceLockerPuzzles, e.Index, (x, v) => x.solved = v, e.Bool0); break;
                case PuzzleType.RadioStationTutorial: ApplyBool(_tutorialPuzzles, e.Index, (x, v) => x.solved = v, e.Bool0); break;
                case PuzzleType.CentralElevator:
                    var ec = SafeAt(_elevatorControls, e.Index);
                    if (ec != null) { ec.floor = e.Int0; ec.state = (CentralElevatorControl.evState)e.Int1; }
                    break;
                case PuzzleType.ElevatorCallButton: ApplyBool(_elevatorButtons, e.Index, (x, v) => x.called = v, e.Bool0); break;
                case PuzzleType.DoorLockEventInteraction: ApplyBool(_doorLockEvents, e.Index, (x, v) => x.done = v, e.Bool0); break;
                case PuzzleType.MultiConditionEvent:
                    var mce = SafeAt(_multiConditionEvents, e.Index);
                    if (mce != null) mce.tried = e.Int0;
                    break;
                case PuzzleType.CryoDoorController: ApplyBool(_cryoDoors, e.Index, (x, v) => x.open = v, e.Bool0); break;
                case PuzzleType.FoldingShutterDoor:
                    var fd = SafeAt(_foldingDoors, e.Index);
                    if (fd != null) fd.open = e.Float0;
                    break;
                case PuzzleType.InteractionTriggered:
                    ApplyBool(_interactions, e.Index, (x, v) => x.triggered = v, e.Bool0);
                    break;
                case PuzzleType.EventZoneTriggered:
                    ApplyBool(_eventZones, e.Index, (x, v) => x.triggered = v, e.Bool0);
                    break;
                case PuzzleType.GlobalAlertStatus:
                    GlobalAlertStatus.currentStatus = (GlobalAlertStatus.alarm)e.Int0;
                    break;
                case PuzzleType.RadioManagerState:
                    RadioManager.moduleInstalled = (e.Int0 & 1) != 0;
                    RadioManager.tuner = (e.Int0 & 2) != 0;
                    RadioManager.tuning = (e.Int0 & 4) != 0;
                    RadioManager.signal = (e.Int0 & 8) != 0;
                    RadioManager.data = (e.Int0 & 16) != 0;
                    RadioManager.audible = (e.Int0 & 32) != 0;
                    RadioManager.frequency = e.Float0;
                    if (RadioManager.instance != null)
                        RadioManager.instance._frequency = e.Float0;
                    break;
                case PuzzleType.StorageBox:
                    var sb = SafeAt(_storageBoxes, e.Index);
                    if (sb != null && _storageBoxOpenField != null)
                        _storageBoxOpenField.SetValue(sb, e.Bool0);
                    break;
                case PuzzleType.EnemyManagerState:
                    var em = SafeAt(_enemyManagers, e.Index);
                    if (em != null) { em.cleared = e.Bool0; em.inOperation = e.Bool1; }
                    EnemyManager.inCombat = (e.Int0 & 1) != 0;
                    EnemyManager.enemyPresence = (e.Int0 & 2) != 0;
                    break;
            }
        }

        private static void ApplyBool<T>(T[] arr, int idx, System.Action<T, bool> setter, bool val) where T : class
        {
            var obj = SafeAt(arr, idx);
            if (obj != null) setter(obj, val);
        }

        private static T SafeAt<T>(T[] arr, int idx) where T : class
        {
            if (arr == null || idx < 0 || idx >= arr.Length) return null;
            return arr[idx];
        }

        private static int Len<T>(T[] arr) { return arr != null ? arr.Length : 0; }

        public void Reset()
        {
            _scanned = false;
            _needFullSend = true;
            _lastSent.Clear();
            _sendTimer = 0f;
            _puzzleStatuses = null;
            _interactiveLocks = null;
            _interactiveLockSingles = null;
            _keypads = null;
            _rotKeypads = null;
            _penCodepads = null;
            _patternLocks = null;
            _dialLocks = null;
            _flipSwitches = null;
            _floodControlSwitches = null;
            _floodControlsList = null;
            _resPowers = null;
            _useItemInteractions = null;
            _numberLocks = null;
            _doorLockPuzzles = null;
            _medMultiLocks = null;
            _labMultiLocks = null;
            _ventPuzzles = null;
            _simpleDoors = null;
            _swingDoors = null;
            _doorLockControls = null;
            _evidenceLockerPuzzles = null;
            _tutorialPuzzles = null;
            _elevatorControls = null;
            _elevatorButtons = null;
            _doorLockEvents = null;
            _multiConditionEvents = null;
            _cryoDoors = null;
            _foldingDoors = null;
            _interactions = null;
            _eventZones = null;
            _enemyManagers = null;
            _storageBoxes = null;
        }
    }
}
