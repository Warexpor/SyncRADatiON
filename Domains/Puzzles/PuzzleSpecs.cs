// Everything the coordinator knows about a PuzzleType, one row per type. Adding a type = one enum value
// (NetMessages.cs, wire: never renumber) + one row here + its read/apply code. PuzzleSpecTests checks every enum
// value has a row or is listed in Retired; PuzzleSpecs.SelfCheck logs the same at startup.
using System;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    internal delegate bool PuzzleReader(Component c, long wid, out PuzzleStateEntry entry);
    internal delegate void PuzzleApplier(Component c, PuzzleStateEntry e);
    internal delegate bool PuzzleTryRead<T>(T x, long wid, out PuzzleStateEntry entry);

    [Flags]
    internal enum SpecFlags : byte
    {
        None = 0,
        /// <summary>A client may author this type (puzzle / lock input). Never Interaction.trigger, EventZone, combat, cutscenes.</summary>
        Emit = 1,
        /// <summary>A held solve survives a scene reload for the session (WorldId hashes the scene): door / lock solves.</summary>
        Durable = 2,
        /// <summary>Applied without a component of this WorldId (static flags / WorldId 0 globals).</summary>
        GlobalApply = 4,
        /// <summary>Unlocked by default: only held after a locked state was seen, so a script relock is not undone.</summary>
        LockTransition = 8,
        /// <summary>Static state read through the first scanned instance only (one entry per scene).</summary>
        FirstInstance = 16,
    }

    internal sealed class PuzzleTypeSpec
    {
        public PuzzleType Type;
        public SpecFlags Flags;
        public MergeKind Merge;
        /// <summary>Held for remount re-snap / durable memory while true (mid-puzzle state counts, not only solved).</summary>
        public Func<PuzzleStateEntry, bool> Progressed;
        /// <summary>"Already solved" for the cryo / codepad OnEnable rematch (default: Progressed).</summary>
        public Func<PuzzleStateEntry, bool> HeldSolved;
        /// <summary>Instance read (null for WorldId-0 globals).</summary>
        public PuzzleReader Read;
        public PuzzleApplier Apply;
        /// <summary>Registers this type's components by WorldId (null for globals).</summary>
        public Action<PuzzleSyncService> Scan;
        /// <summary>WorldId-0 global polled every send tick.</summary>
        public Func<PuzzleStateEntry> ReadGlobal;
        /// <summary>A client sends its global read only when this holds (null = always).</summary>
        public Func<PuzzleStateEntry, bool> ClientSendIf;
        /// <summary>The host takes a client entry only when this holds (null = always).</summary>
        public Func<PuzzleStateEntry, bool> HostAcceptIf;

        public bool ClientEmit => (Flags & SpecFlags.Emit) != 0;
        public bool Durable => (Flags & SpecFlags.Durable) != 0;
        public bool GlobalApply => (Flags & SpecFlags.GlobalApply) != 0;
        public bool LockTransition => (Flags & SpecFlags.LockTransition) != 0;
        public bool FirstInstanceOnly => (Flags & SpecFlags.FirstInstance) != 0;
        public bool CellMerge => PuzzleMerge.IsCellMerge(Merge);
    }

    internal static class PuzzleSpecs
    {
        /// <summary>Enum values that are wire-reserved but never synced.</summary>
        internal static readonly PuzzleType[] Retired =
        {
            // Polling / applying Interaction.triggered teleports the other Elster through ConnectedDoors.
            PuzzleType.InteractionTriggered,
        };

        // persistent: constant spec catalog, built once
        static readonly PuzzleTypeSpec[] _byType = new PuzzleTypeSpec[256];
        // persistent: constant spec catalog, built once
        internal static readonly PuzzleTypeSpec[] All;
        // persistent: constant spec catalog, built once
        internal static readonly PuzzleTypeSpec[] Globals;

        internal static PuzzleTypeSpec Of(PuzzleType t) => _byType[(byte)t];

        static PuzzleSpecs()
        {
            All = Table();
            int globals = 0;
            for (int i = 0; i < All.Length; i++)
            {
                var s = All[i];
                if (s.HeldSolved == null) s.HeldSolved = s.Progressed;
                _byType[(byte)s.Type] = s;
                if (s.ReadGlobal != null) globals++;
            }
            Globals = new PuzzleTypeSpec[globals];
            for (int i = 0, g = 0; i < All.Length; i++)
                if (All[i].ReadGlobal != null) Globals[g++] = All[i];
        }

        // persistent: boot-once self-check guard
        static bool _checked;

        /// <summary>Startup: one warning per enum value without a row (the unit test catches it first).</summary>
        internal static void SelfCheck()
        {
            if (_checked) return;
            _checked = true;
            foreach (PuzzleType t in Enum.GetValues(typeof(PuzzleType)))
            {
                if (Of(t) != null || Array.IndexOf(Retired, t) >= 0) continue;
                PlaytestLog.Warn("Puzzle", "no PuzzleTypeSpec for " + t + ": never read, applied or scanned");
            }
        }

        // ---- progressed rules -----------------------------------------------------------------------------------
        static readonly Func<PuzzleStateEntry, bool> Never = e => false;
        static readonly Func<PuzzleStateEntry, bool> Always = e => true;
        static readonly Func<PuzzleStateEntry, bool> Bool0 = e => e.Bool0;
        static readonly Func<PuzzleStateEntry, bool> Unlocked = e => !e.Bool0;
        static readonly Func<PuzzleStateEntry, bool> Bool0Or1 = e => e.Bool0 || e.Bool1;
        static readonly Func<PuzzleStateEntry, bool> AnyBool = e => e.Bool0 || e.Bool1 || e.Bool2;
        static readonly Func<PuzzleStateEntry, bool> Bool0OrInt0 = e => e.Bool0 || e.Int0 != 0;

        const SpecFlags Emit = SpecFlags.Emit;
        const SpecFlags EmitDurable = SpecFlags.Emit | SpecFlags.Durable;

        static PuzzleTypeSpec Row<T>(PuzzleType type, Func<T, long, PuzzleStateEntry> read, Action<T, PuzzleStateEntry> apply,
            SpecFlags flags, Func<PuzzleStateEntry, bool> progressed, MergeKind merge = MergeKind.None) where T : Component
            => TryRow<T>(type, (T x, long wid, out PuzzleStateEntry e) => { e = read(x, wid); return true; }, apply,
                flags, progressed, merge);

        static PuzzleTypeSpec TryRow<T>(PuzzleType type, PuzzleTryRead<T> read, Action<T, PuzzleStateEntry> apply,
            SpecFlags flags, Func<PuzzleStateEntry, bool> progressed, MergeKind merge = MergeKind.None) where T : Component
            => new PuzzleTypeSpec
            {
                Type = type,
                Flags = flags,
                Merge = merge,
                Progressed = progressed,
                Read = (Component c, long wid, out PuzzleStateEntry e) => read((T)c, wid, out e),
                Apply = (c, e) => apply(c as T, e),
                Scan = s => s.RegisterAll<T>(type),
            };

        static PuzzleTypeSpec Global(PuzzleType type, Func<PuzzleStateEntry> read, Action<PuzzleStateEntry> apply,
            SpecFlags flags, Func<PuzzleStateEntry, bool> progressed, MergeKind merge = MergeKind.None)
            => new PuzzleTypeSpec
            {
                Type = type,
                Flags = flags | SpecFlags.GlobalApply,
                Merge = merge,
                Progressed = progressed,
                ReadGlobal = read,
                Apply = (c, e) => apply(e),
            };

        // Interaction.triggered is never polled (Retired): door / move Interactions teleport the local Elster when
        // the other peer walks through a ConnectedDoors link.
        static PuzzleTypeSpec[] Table() => new[]
        {
            Row<PuzzleStatus>(PuzzleType.PuzzleStatus,
                PuzzleDoorFlagsSyncService.ReadPuzzleStatus, PuzzleDoorFlagsSyncService.ApplyPuzzleStatus, Emit, Bool0),
            Row<InteractiveLock>(PuzzleType.InteractiveLock,
                LockSyncService.ReadInteractive, LockSyncService.ApplyInteractive, EmitDurable, Unlocked),
            TryRow<InteractiveLockSingle>(PuzzleType.InteractiveLockSingle,
                LockSyncService.TryReadInteractiveSingle, LockSyncService.ApplyInteractiveSingle, EmitDurable, Unlocked),
            Row<Keypad3D>(PuzzleType.Keypad3D,
                LockSyncService.ReadKeypad3D, LockSyncService.ApplyKeypad3D, EmitDurable, Bool0, MergeKind.AtomicInts),
            Row<ROT_Keypad>(PuzzleType.ROT_Keypad,
                LockSyncService.ReadRotKeypad, LockSyncService.ApplyRotKeypad, EmitDurable, Bool0, MergeKind.AtomicInts),
            Row<PEN_Codepad>(PuzzleType.PEN_Codepad,
                CodepadSyncService.ReadCodepad, CodepadSyncService.ApplyCodepad, EmitDurable, Bool0, MergeKind.AtomicInts),
            // The grid is held while unsolved (Int3 = cell count), but the OnEnable rematch only asks "solved".
            Row<LAB_PatternLock>(PuzzleType.PatternLock,
                CodepadSyncService.ReadPatternLock, CodepadSyncService.ApplyPatternLock, EmitDurable,
                e => e.Bool0 || e.Int3 != 0, MergeKind.BitCells).HeldSolvedBy(Bool0),
            // Dials start at 0/0/0/5: any departure is mid-puzzle progress.
            Row<ROT_DialLock>(PuzzleType.DialLock,
                LockSyncService.ReadDial, LockSyncService.ApplyDial, EmitDurable,
                e => e.Bool0 || e.Int0 != 0 || e.Int1 != 0 || e.Int2 != 0 || e.Int3 != 5, MergeKind.Fields),
            Row<FlipSwitch>(PuzzleType.FlipSwitch,
                ChapterMachineSyncService.ReadFlipSwitch, ChapterMachineSyncService.ApplyFlipSwitch, Emit, Bool0),
            Row<FloodControlSwitch>(PuzzleType.FloodControlSwitch,
                PumpFloodSyncService.ReadFloodSwitch, PumpFloodSyncService.ApplyFloodSwitch, Emit, Bool0),
            // Int1 = input[] pack: partial switch progress.
            Row<FloodControls>(PuzzleType.FloodControls,
                PumpFloodSyncService.ReadFloodControls, PumpFloodSyncService.ApplyFloodControls, Emit,
                e => e.Bool0 || e.Int1 != 0),
            Row<RES_Power>(PuzzleType.RES_Power,
                ChapterMachineSyncService.ReadPower, ChapterMachineSyncService.ApplyPower, Emit, Bool0OrInt0),
            Row<UseItemInteraction>(PuzzleType.UseItemInteraction,
                UseItemWorldSyncService.ReadUseItem, UseItemWorldSyncService.ApplyUseItem, EmitDurable, Bool0),
            Row<NumberLockNew>(PuzzleType.NumberLockNew,
                LockSyncService.ReadNumber, LockSyncService.ApplyNumber, EmitDurable, Unlocked),
            Row<DoorLockPuzzle>(PuzzleType.DoorLockPuzzle,
                LockSyncService.ReadDoorLockPuzzle, LockSyncService.ApplyDoorLockPuzzle, EmitDurable, Unlocked),
            // MED_MultiLock and LAB_MultiLock share the type; Int0 = element-key bits.
            TryRow<Component>(PuzzleType.MultiLock,
                LockSyncService.TryReadMulti, LockSyncService.ApplyMulti, EmitDurable, Bool0OrInt0)
                .ScanOnly<MED_MultiLock>().Also<LAB_MultiLock>(),
            Row<MED_VentPuzzle>(PuzzleType.MED_VentPuzzle,
                ChapterMachineSyncService.ReadVent, ChapterMachineSyncService.ApplyVent, Emit, Bool0),
            Row<Doorway_simple>(PuzzleType.DoorwaySimple,
                PuzzleDoorFlagsSyncService.ReadDoorway, PuzzleDoorFlagsSyncService.ApplyDoorway,
                EmitDurable | SpecFlags.LockTransition, Unlocked),
            Row<SwingDoor>(PuzzleType.SwingDoor,
                PuzzleDoorFlagsSyncService.ReadSwing, PuzzleDoorFlagsSyncService.ApplySwing,
                EmitDurable | SpecFlags.LockTransition, e => e.Bool0 || !e.Bool1),
            Row<DoorLockControl>(PuzzleType.DoorLockControl,
                PuzzleDoorFlagsSyncService.ReadDoorLockControl, PuzzleDoorFlagsSyncService.ApplyDoorLockControl,
                EmitDurable | SpecFlags.LockTransition, Unlocked),
            Row<EvidenceLockerLogicPuzzle>(PuzzleType.EvidenceLockerPuzzle,
                ChapterMachineSyncService.ReadEvidenceLocker, ChapterMachineSyncService.ApplyEvidenceLocker, Emit,
                e => e.Bool0 || e.Int0 != 0 || e.Int1 != 0, MergeKind.AtomicInts),
            Row<RadioStationTutorialPuzzle>(PuzzleType.RadioStationTutorial,
                RadioPuzzleSyncService.ReadTutorial, RadioPuzzleSyncService.ApplyTutorial, Emit, Bool0),
            // The cabin floor / state must survive a room remount.
            Row<CentralElevatorControl>(PuzzleType.CentralElevator,
                ElevatorSyncService.ReadCentral, ElevatorSyncService.ApplyCentral, Emit, Always),
            Row<ElevatorCallButton>(PuzzleType.ElevatorCallButton,
                ElevatorSyncService.ReadCallButton, ElevatorSyncService.ApplyCallButton, Emit, Bool0),
            Row<DoorLockEventInteraction>(PuzzleType.DoorLockEventInteraction,
                LockSyncService.ReadDoorLockEvent, LockSyncService.ApplyDoorLockEvent, EmitDurable, Bool0),
            // Int0 = tried: a partial count before triedOnce.
            Row<MultiConditionEvent>(PuzzleType.MultiConditionEvent,
                StoryFlagPuzzleSyncService.ReadMultiCondition, StoryFlagPuzzleSyncService.ApplyMultiCondition,
                SpecFlags.None, Bool0OrInt0),
            Row<CryoDoorController>(PuzzleType.CryoDoorController,
                CryoSyncService.ReadController, CryoSyncService.ApplyController, Emit, Bool0),
            Row<FoldingShutterDoor>(PuzzleType.FoldingShutterDoor,
                PuzzleDoorFlagsSyncService.ReadFoldingShutter, PuzzleDoorFlagsSyncService.ApplyFoldingShutter, Emit,
                e => e.Float0 > 0.01f),
            Row<EventZone>(PuzzleType.EventZoneTriggered,
                EventZonePuzzleSyncService.Read, EventZonePuzzleSyncService.Apply, SpecFlags.None, Never),
            // Host-only: a client poll would clobber the alarm.
            Global(PuzzleType.GlobalAlertStatus,
                EnemySyncService.ReadGlobalAlert, EnemySyncService.ApplyGlobalAlert, SpecFlags.None, Never),
            // A client only sends moduleInstalled = true: an unacquired false would race the host's latch.
            Global(PuzzleType.RadioManagerState,
                RadioPuzzleSyncService.ReadManagerState, RadioPuzzleSyncService.ApplyManager, Emit, Never)
                .ClientSendsIf(e => (e.Int0 & 1) != 0),
            TryRow<EnemyManager>(PuzzleType.EnemyManagerState,
                EnemySyncService.TryReadPuzzle, EnemySyncService.ApplyPuzzle, SpecFlags.None, Never),
            Row<StorageBox>(PuzzleType.StorageBox,
                StorageLidSyncService.Read, StorageLidSyncService.Apply, Emit, Bool0),
            // Int3 = slot count (0xFF = empty slot): held from the first successful read.
            Row<ROT_Tarot>(PuzzleType.ROT_Tarot,
                ChapterMachineSyncService.ReadTarot, ChapterMachineSyncService.ApplyTarot, Emit,
                e => e.Bool0 || e.Int3 != 0, MergeKind.Tarot),
            // Moon pack carries DesiredPos bits, so it is non-zero (held) from load: the dump always carries it.
            Row<ROT_Mural>(PuzzleType.ROT_Mural,
                ChapterMachineSyncService.ReadMural, ChapterMachineSyncService.ApplyMural, Emit,
                e => e.Bool0 || e.Bool1 || e.Int0 != 0 || e.Int1 != 0 || e.Int2 != 0 || e.Int3 != 0, MergeKind.HalfInts),
            // Dials start at 10/10/10.
            Row<MED_Incinerator>(PuzzleType.MED_Incinerator,
                ChapterMachineSyncService.ReadIncinerator, ChapterMachineSyncService.ApplyIncinerator, Emit,
                e => e.Bool0 || e.Int0 != 10 || e.Int1 != 10 || e.Int2 != 10, MergeKind.Fields),
            // Bool0 = MultiInteraction gate active: held even at weight 0 so a remount re-opens the gate.
            Row<LAB_Waage>(PuzzleType.LAB_Waage,
                ChapterMachineSyncService.ReadWaage, ChapterMachineSyncService.ApplyWaage, Emit,
                e => e.Bool0 || !Mathf.Approximately(e.Float0, 0f)),
            Row<RES_Shrine>(PuzzleType.RES_Shrine,
                ChapterMachineSyncService.ReadShrine, ChapterMachineSyncService.ApplyShrine, Emit,
                e => e.Bool0 || e.Int0 != 0 || e.Int1 != 0 || e.Int2 != 0, MergeKind.Fields),
            Row<ROT_RadioAlignment>(PuzzleType.ROT_RadioAlignment,
                RadioPuzzleSyncService.ReadAlignment, RadioPuzzleSyncService.ApplyAlignment, Emit,
                e => e.Bool0 || e.Int0 != 0 || e.Int1 != 0, MergeKind.Fields),
            // Int0..2 = the host's frequency / code / hint (0/0/0 before Start): held so a remount never keeps a
            // second local roll. A client's own Start() roll is unsolved: the host only takes its solve.
            Row<DET_RadioCodeLock>(PuzzleType.DET_RadioCodeLock,
                RadioPuzzleSyncService.ReadCode, RadioPuzzleSyncService.ApplyCode, EmitDurable,
                e => e.Bool0 || e.Int0 != 0 || e.Int1 != 0 || e.Int2 != 0, MergeKind.Fields).HostAcceptsIf(Bool0),
            // UseItemMultiInteraction.blocked is static: one entry through the first instance.
            Row<UseItemMultiInteraction>(PuzzleType.UseItemMulti,
                UseItemWorldSyncService.ReadMulti, UseItemWorldSyncService.ApplyMulti,
                Emit | SpecFlags.GlobalApply | SpecFlags.FirstInstance, Never),
            Row<SaveRoomEvent>(PuzzleType.SaveRoomEvent,
                StoryFlagPuzzleSyncService.ReadSaveRoom, StoryFlagPuzzleSyncService.ApplySaveRoom, SpecFlags.None, Bool0),
            Row<CutsceneManager>(PuzzleType.CutsceneCompleted,
                StoryFlagPuzzleSyncService.ReadCutscene, StoryFlagPuzzleSyncService.ApplyCutscene, SpecFlags.None, Bool0),
            TryRow<Dialogue>(PuzzleType.DialoguePlayedOnce,
                StoryFlagPuzzleSyncService.TryReadDialogue, StoryFlagPuzzleSyncService.ApplyDialogue, SpecFlags.None, Bool0),
            Row<EXC_Elevator>(PuzzleType.EXC_Elevator,
                ElevatorSyncService.ReadExc, ElevatorSyncService.ApplyExc, Emit, Bool0Or1),
            TryRow<KolibriManager>(PuzzleType.KolibriManager,
                (KolibriManager x, long wid, out PuzzleStateEntry e) => BossSyncService.TryReadPuzzle(PuzzleType.KolibriManager, x, wid, out e),
                BossSyncService.ApplyKolibri, SpecFlags.None, Never),
            TryRow<BOS_Adler>(PuzzleType.BOS_Adler,
                (BOS_Adler x, long wid, out PuzzleStateEntry e) => BossSyncService.TryReadPuzzle(PuzzleType.BOS_Adler, x, wid, out e),
                BossSyncService.ApplyAdler, SpecFlags.None, Never),
            Row<CryoDoorLock>(PuzzleType.CryoDoorLock,
                CryoSyncService.ReadLock, CryoSyncService.ApplyLock, Emit, Bool0),
            Row<PEN_Cryo>(PuzzleType.PEN_Cryo,
                CryoSyncService.ReadPenCryo, CryoSyncService.ApplyPenCryo, Emit, Bool0),
            // Water a/b/c starts at 12/0/0.
            Row<MED_Pump>(PuzzleType.MED_Pump,
                PumpFloodSyncService.ReadPump, PumpFloodSyncService.ApplyPump, Emit,
                e => e.Bool0 || e.Int0 != 12 || e.Int1 != 0 || e.Int2 != 0),
            Row<MED_FloodedBathroom>(PuzzleType.MED_FloodedBathroom,
                PumpFloodSyncService.ReadFlood, PumpFloodSyncService.ApplyFlood, Emit, Bool0),
            // Int3 = node count (so an all-off trace still holds), Bool1 = card inserted.
            Row<MED_CardWriter>(PuzzleType.MED_CardWriter,
                ChapterMachineSyncService.ReadCardWriter, ChapterMachineSyncService.ApplyCardWriter, Emit,
                e => e.Bool0 || e.Bool1 || e.Int3 != 0),
            Row<RES_Shutters>(PuzzleType.RES_Shutters,
                ChapterMachineSyncService.ReadShutters, ChapterMachineSyncService.ApplyShutters, Emit, Bool0),
            Row<ROT_Pipes>(PuzzleType.ROT_Pipes,
                PipesSyncService.Read, PipesSyncService.Apply, Emit, Bool0),
            Row<ROT_Magpie>(PuzzleType.ROT_Magpie,
                ChapterMachineSyncService.ReadMagpie, ChapterMachineSyncService.ApplyMagpie, Emit, Bool0),
            // Rod pack starts at [0,4,3,1] (Int0 = 736): held from load, so the dump always carries the rods.
            Row<PEN_Reaktor>(PuzzleType.PEN_Reaktor,
                ChapterMachineSyncService.ReadReaktor, ChapterMachineSyncService.ApplyReaktor, Emit,
                e => e.Bool0 || e.Bool1 || e.Int0 != 0 || e.Int1 != 0, MergeKind.Rods),
            Row<DET_ServiceLock>(PuzzleType.DET_ServiceLock,
                LockSyncService.ReadServiceLock, LockSyncService.ApplyServiceLock, EmitDurable, Bool0OrInt0),
            Row<EXC_Seilbahn>(PuzzleType.EXC_Seilbahn,
                HatchSyncService.ReadSeilbahn, HatchSyncService.ApplySeilbahn, Emit, Bool0),
            Row<EXC_Hatch>(PuzzleType.EXC_Hatch,
                HatchSyncService.ReadHatch, HatchSyncService.ApplyHatch, Emit, Bool0),
            Row<LAB_Rings>(PuzzleType.LAB_Rings,
                ChapterMachineSyncService.ReadLabRings, ChapterMachineSyncService.ApplyLabRings, Emit, Bool0OrInt0),
            // Int0 = KeyLevel: partial key progress before !hasLock.
            Row<BiodomeDoorLock>(PuzzleType.BiodomeDoorLock,
                LockSyncService.ReadBiodome, LockSyncService.ApplyBiodome, EmitDurable, Bool0OrInt0),
            Row<ROT_MeatBlocker>(PuzzleType.ROT_MeatBlocker,
                ChapterMachineSyncService.ReadMeatBlocker, ChapterMachineSyncService.ApplyMeatBlocker, Emit, Bool0OrInt0),
            // Bool1 = cassette inserted, before the lid opens.
            Row<RES_MusicBox>(PuzzleType.RES_MusicBox,
                ResidencyPuzzleSyncService.ReadMusicBox, ResidencyPuzzleSyncService.ApplyMusicBox, Emit, Bool0Or1),
            // Bool1 = robotPos pack valid: the spawn cell (Float may be 0) still holds.
            Row<RES_LibraryPC>(PuzzleType.RES_LibraryPC,
                ResidencyPuzzleSyncService.ReadLibraryPc, ResidencyPuzzleSyncService.ApplyLibraryPc, Emit, Bool0Or1),
            Row<RES_Paternoster>(PuzzleType.RES_Paternoster,
                ResidencyPuzzleSyncService.ReadPaternoster, ResidencyPuzzleSyncService.ApplyPaternoster, Emit, Bool0),
            // MED_KeyGrid.solved is static: WorldId-0 global. Int1 = node count (an all-off grid still holds).
            Global(PuzzleType.MED_KeyGrid,
                ResidencyPuzzleSyncService.ReadKeyGridGlobal, ResidencyPuzzleSyncService.ApplyKeyGrid, Emit,
                e => e.Bool0 || e.Int1 != 0, MergeKind.BitCells),
            Global(PuzzleType.ArianePhotoCode,
                ResidencyPuzzleSyncService.ReadArianePhotoCodeGlobal, ResidencyPuzzleSyncService.ApplyArianePhotoCode, Emit,
                e => e.Int0 != 0 || e.Bool0),
            Row<DET_ServiceLock_Key>(PuzzleType.DET_ServiceLock_Key,
                ResidencyPuzzleSyncService.ReadServiceLockKey, ResidencyPuzzleSyncService.ApplyServiceLockKey, EmitDurable, Bool0),
            Row<SafeDoorSmall>(PuzzleType.SafeDoorSmall,
                ResidencyPuzzleSyncService.ReadSafeDoor, ResidencyPuzzleSyncService.ApplySafeDoor, EmitDurable, Bool0),
            // Int0 = keys[] pack: inserted keys before every key is in.
            Row<MultiKeyLock>(PuzzleType.MultiKeyLock,
                ResidencyPuzzleSyncService.ReadMultiKeyLock, ResidencyPuzzleSyncService.ApplyMultiKeyLock, EmitDurable, Bool0OrInt0),
            Row<OpenableDrawer>(PuzzleType.OpenableDrawer,
                ResidencyPuzzleSyncService.ReadDrawer, ResidencyPuzzleSyncService.ApplyDrawer, Emit, Bool0),
            Row<GunCase>(PuzzleType.GunCase,
                ChapterExtraPuzzleSyncService.ReadGunCase, ChapterExtraPuzzleSyncService.ApplyGunCase, Emit, Bool0),
            Row<AraNest>(PuzzleType.AraNest,
                ChapterExtraPuzzleSyncService.ReadAraNest, ChapterExtraPuzzleSyncService.ApplyAraNest, Emit,
                e => e.Bool0 || e.Bool2),
            Row<LAB_RifleQuest>(PuzzleType.LAB_RifleQuest,
                ChapterExtraPuzzleSyncService.ReadRifleQuest, ChapterExtraPuzzleSyncService.ApplyRifleQuest, Emit, AnyBool),
            Row<LOV_Microfiche>(PuzzleType.LOV_Microfiche,
                ChapterExtraPuzzleSyncService.ReadMicrofiche, ChapterExtraPuzzleSyncService.ApplyMicrofiche, Emit, AnyBool),
            // Durable state is the DoorL / DoorR local X (Float0 / Float1).
            Row<MED_Adler_EVdoors>(PuzzleType.MED_Adler_EVdoors,
                ChapterExtraPuzzleSyncService.ReadAdlerEvDoors, ChapterExtraPuzzleSyncService.ApplyAdlerEvDoors, Emit,
                e => e.Bool0 || !Mathf.Approximately(e.Float0, 0f) || !Mathf.Approximately(e.Float1, 0f)),
            Row<ROT_DiskManager>(PuzzleType.ROT_DiskManager,
                WorldObjectPuzzleSyncService.ReadDiskManager, WorldObjectPuzzleSyncService.ApplyDiskManager, EmitDurable,
                Bool0Or1),
            // Partial damage (HP below the creature's undamaged HP) survives a remount, not only the dead state.
            TryRow<DET_WallCreature>(PuzzleType.DET_WallCreature,
                WorldObjectPuzzleSyncService.TryReadWallCreature, WorldObjectPuzzleSyncService.ApplyWallCreature, Emit,
                e => e.Bool0 || WorldObjectPuzzleSyncService.WallDamaged(e.WorldId, e.Int0)),
            Row<MapRevealInteraction>(PuzzleType.MapReveal,
                WorldObjectPuzzleSyncService.ReadMapReveal, WorldObjectPuzzleSyncService.ApplyMapReveal, Emit, Bool0),
            Row<MEM_ChecklistLogic>(PuzzleType.MEM_ChecklistLogic,
                WorldObjectPuzzleSyncService.ReadChecklist, WorldObjectPuzzleSyncService.ApplyChecklist, EmitDurable,
                e => e.Int0 != 0 || e.Bool0, MergeKind.Grow),
        };

        // ---- row modifiers --------------------------------------------------------------------------------------

        /// <summary>Scan these component types instead of the row's T (several classes share one PuzzleType).</summary>
        static PuzzleTypeSpec ScanOnly<T>(this PuzzleTypeSpec s) where T : Component
        {
            var type = s.Type;
            s.Scan = x => x.RegisterAll<T>(type);
            return s;
        }

        static PuzzleTypeSpec Also<T>(this PuzzleTypeSpec s) where T : Component
        {
            var type = s.Type;
            s.Scan += x => x.RegisterAll<T>(type);
            return s;
        }

        static PuzzleTypeSpec HeldSolvedBy(this PuzzleTypeSpec s, Func<PuzzleStateEntry, bool> heldSolved)
        {
            s.HeldSolved = heldSolved;
            return s;
        }

        static PuzzleTypeSpec ClientSendsIf(this PuzzleTypeSpec s, Func<PuzzleStateEntry, bool> send)
        {
            s.ClientSendIf = send;
            return s;
        }

        static PuzzleTypeSpec HostAcceptsIf(this PuzzleTypeSpec s, Func<PuzzleStateEntry, bool> accept)
        {
            s.HostAcceptIf = accept;
            return s;
        }
    }
}
