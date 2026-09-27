// PuzzleType → read/apply/scan tables for PuzzleSyncService.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SyncRADation.Networking
{
    public sealed partial class PuzzleSyncService
    {
        static Dictionary<PuzzleType, PuzzleReader> BuildReaders()
        {
            var d = new Dictionary<PuzzleType, PuzzleReader>();
            PuzzleReader doors = PuzzleDoorFlagsSyncService.TryRead;
            PuzzleReader locks = LockSyncService.TryRead;
            PuzzleReader codepad = CodepadSyncService.TryRead;
            PuzzleReader chapter = ChapterMachineSyncService.TryRead;
            PuzzleReader flood = PumpFloodSyncService.TryRead;
            PuzzleReader useItem = UseItemWorldSyncService.TryRead;
            PuzzleReader radio = RadioPuzzleSyncService.TryRead;
            PuzzleReader elev = ElevatorSyncService.TryRead;
            PuzzleReader story = StorySyncService.TryReadPuzzle;
            PuzzleReader cryo = CryoSyncService.TryRead;
            PuzzleReader hatch = HatchSyncService.TryRead;
            PuzzleReader boss = BossSyncService.TryReadPuzzle;
            PuzzleReader residency = ResidencyPuzzleSyncService.TryRead;
            PuzzleReader chapterExtra = ChapterExtraPuzzleSyncService.TryRead;

            d[PuzzleType.PuzzleStatus] = doors;
            d[PuzzleType.InteractiveLock] = locks;
            d[PuzzleType.InteractiveLockSingle] = locks;
            d[PuzzleType.Keypad3D] = locks;
            d[PuzzleType.ROT_Keypad] = locks;
            d[PuzzleType.PEN_Codepad] = codepad;
            d[PuzzleType.PatternLock] = codepad;
            d[PuzzleType.DialLock] = locks;
            d[PuzzleType.FlipSwitch] = chapter;
            d[PuzzleType.RES_Power] = chapter;
            d[PuzzleType.FloodControlSwitch] = flood;
            d[PuzzleType.FloodControls] = flood;
            d[PuzzleType.UseItemInteraction] = useItem;
            d[PuzzleType.UseItemMulti] = useItem;
            d[PuzzleType.NumberLockNew] = locks;
            d[PuzzleType.DoorLockPuzzle] = locks;
            d[PuzzleType.MultiLock] = locks;
            d[PuzzleType.MED_VentPuzzle] = chapter;
            d[PuzzleType.DoorwaySimple] = doors;
            d[PuzzleType.SwingDoor] = doors;
            d[PuzzleType.DoorLockControl] = doors;
            d[PuzzleType.FoldingShutterDoor] = doors;
            d[PuzzleType.DoorLockEventInteraction] = locks;
            d[PuzzleType.EvidenceLockerPuzzle] = chapter;
            d[PuzzleType.RadioStationTutorial] = radio;
            d[PuzzleType.ROT_RadioAlignment] = radio;
            d[PuzzleType.DET_RadioCodeLock] = radio;
            d[PuzzleType.CentralElevator] = elev;
            d[PuzzleType.ElevatorCallButton] = elev;
            d[PuzzleType.EXC_Elevator] = elev;
            d[PuzzleType.MultiConditionEvent] = story;
            d[PuzzleType.SaveRoomEvent] = story;
            d[PuzzleType.CutsceneCompleted] = story;
            d[PuzzleType.DialoguePlayedOnce] = story;
            d[PuzzleType.CryoDoorController] = cryo;
            d[PuzzleType.CryoDoorLock] = cryo;
            d[PuzzleType.PEN_Cryo] = cryo;
            d[PuzzleType.MED_Pump] = flood;
            d[PuzzleType.MED_FloodedBathroom] = flood;
            d[PuzzleType.MED_CardWriter] = chapter;
            d[PuzzleType.RES_Shutters] = chapter;
            d[PuzzleType.ROT_Magpie] = chapter;
            d[PuzzleType.PEN_Reaktor] = chapter;
            d[PuzzleType.LAB_Rings] = chapter;
            d[PuzzleType.ROT_MeatBlocker] = chapter;
            d[PuzzleType.ROT_Tarot] = chapter;
            d[PuzzleType.ROT_Mural] = chapter;
            d[PuzzleType.MED_Incinerator] = chapter;
            d[PuzzleType.LAB_Waage] = chapter;
            d[PuzzleType.RES_Shrine] = chapter;
            d[PuzzleType.ROT_Pipes] = ReadPipes;
            d[PuzzleType.DET_ServiceLock] = locks;
            d[PuzzleType.BiodomeDoorLock] = locks;
            d[PuzzleType.EXC_Seilbahn] = hatch;
            d[PuzzleType.EXC_Hatch] = hatch;
            d[PuzzleType.EventZoneTriggered] = ReadEventZone;
            d[PuzzleType.StorageBox] = ReadStorage;
            d[PuzzleType.KolibriManager] = boss;
            d[PuzzleType.BOS_Adler] = boss;
            d[PuzzleType.EnemyManagerState] = ReadEnemyManager;
            d[PuzzleType.RES_MusicBox] = residency;
            d[PuzzleType.RES_LibraryPC] = residency;
            d[PuzzleType.RES_Paternoster] = residency;
            d[PuzzleType.MED_KeyGrid] = residency;
            d[PuzzleType.ArianePhotoCode] = residency;
            d[PuzzleType.DET_ServiceLock_Key] = residency;
            d[PuzzleType.SafeDoorSmall] = residency;
            d[PuzzleType.MultiKeyLock] = residency;
            d[PuzzleType.OpenableDrawer] = residency;
            d[PuzzleType.GunCase] = chapterExtra;
            d[PuzzleType.AraNest] = chapterExtra;
            d[PuzzleType.LAB_RifleQuest] = chapterExtra;
            d[PuzzleType.LOV_Microfiche] = chapterExtra;
            return d;
        }

        static bool ReadPipes(PuzzleType type, Component c, long wid, out PuzzleStateEntry entry)
            => PipesSyncService.TryRead((ROT_Pipes)c, wid, out entry);

        static bool ReadEventZone(PuzzleType type, Component c, long wid, out PuzzleStateEntry entry)
            => EventZonePuzzleSyncService.TryRead((EventZone)c, wid, out entry);

        static bool ReadStorage(PuzzleType type, Component c, long wid, out PuzzleStateEntry entry)
            => StorageLidSyncService.TryRead((StorageBox)c, wid, out entry);

        static bool ReadEnemyManager(PuzzleType type, Component c, long wid, out PuzzleStateEntry entry)
            => EnemySyncService.TryReadPuzzle((EnemyManager)c, wid, out entry);

        static Dictionary<PuzzleType, PuzzleApplier> BuildAppliers()
        {
            var d = new Dictionary<PuzzleType, PuzzleApplier>();
            d[PuzzleType.PuzzleStatus] = (s, e, _) =>
                PuzzleDoorFlagsSyncService.ApplyPuzzleStatus(s.Get<PuzzleStatus>(e.Type, e.WorldId), e);
            d[PuzzleType.InteractiveLock] = (s, e, _) =>
                LockSyncService.ApplyInteractive(s.Get<InteractiveLock>(e.Type, e.WorldId), e, _mutateWorld);
            d[PuzzleType.InteractiveLockSingle] = (s, e, _) =>
                LockSyncService.ApplyInteractiveSingle(s.Get<InteractiveLockSingle>(e.Type, e.WorldId), e, _mutateWorld);
            d[PuzzleType.Keypad3D] = (s, e, _) =>
                LockSyncService.ApplyKeypad3D(s.Get<Keypad3D>(e.Type, e.WorldId), e, _mutateWorld);
            d[PuzzleType.ROT_Keypad] = (s, e, _) =>
                LockSyncService.ApplyRotKeypad(s.Get<ROT_Keypad>(e.Type, e.WorldId), e, _mutateWorld);
            d[PuzzleType.PEN_Codepad] = (s, e, cin) =>
                CodepadSyncService.ApplyCodepad(s.Get<PEN_Codepad>(e.Type, e.WorldId), e, cin);
            d[PuzzleType.PatternLock] = (s, e, _) =>
                CodepadSyncService.ApplyPatternLock(s.Get<LAB_PatternLock>(e.Type, e.WorldId), e);
            d[PuzzleType.DialLock] = (s, e, _) =>
                LockSyncService.ApplyDial(s.Get<ROT_DialLock>(e.Type, e.WorldId), e);
            d[PuzzleType.FlipSwitch] = (s, e, _) =>
                ChapterMachineSyncService.ApplyFlipSwitch(s.Get<FlipSwitch>(e.Type, e.WorldId), e, _mutateWorld);
            d[PuzzleType.FloodControlSwitch] = (s, e, _) =>
                PumpFloodSyncService.ApplyFloodSwitch(s.Get<FloodControlSwitch>(e.Type, e.WorldId), e);
            d[PuzzleType.FloodControls] = (s, e, _) =>
                PumpFloodSyncService.ApplyFloodControls(s.Get<FloodControls>(e.Type, e.WorldId), e, _mutateWorld);
            d[PuzzleType.RES_Power] = (s, e, _) =>
                ChapterMachineSyncService.ApplyPower(s.Get<RES_Power>(e.Type, e.WorldId), e);
            d[PuzzleType.UseItemInteraction] = (s, e, _) =>
                UseItemWorldSyncService.ApplyUseItem(s.Get<UseItemInteraction>(e.Type, e.WorldId), e);
            d[PuzzleType.NumberLockNew] = (s, e, _) =>
                LockSyncService.ApplyNumber(s.Get<NumberLockNew>(e.Type, e.WorldId), e);
            d[PuzzleType.DoorLockPuzzle] = (s, e, _) =>
                LockSyncService.ApplyDoorLockPuzzle(s.Get<DoorLockPuzzle>(e.Type, e.WorldId), e);
            d[PuzzleType.MultiLock] = (s, e, _) =>
                LockSyncService.ApplyMulti(s.Get<Component>(e.Type, e.WorldId), e);
            d[PuzzleType.MED_VentPuzzle] = (s, e, _) =>
                ChapterMachineSyncService.ApplyVent(s.Get<MED_VentPuzzle>(e.Type, e.WorldId), e);
            d[PuzzleType.DoorwaySimple] = (s, e, _) =>
                PuzzleDoorFlagsSyncService.ApplyDoorway(s.Get<Doorway_simple>(e.Type, e.WorldId), e, _mutateWorld);
            d[PuzzleType.SwingDoor] = (s, e, _) =>
                PuzzleDoorFlagsSyncService.ApplySwing(s.Get<SwingDoor>(e.Type, e.WorldId), e, _mutateWorld);
            d[PuzzleType.DoorLockControl] = (s, e, _) =>
                PuzzleDoorFlagsSyncService.ApplyDoorLockControl(s.Get<DoorLockControl>(e.Type, e.WorldId), e, _mutateWorld);
            d[PuzzleType.EvidenceLockerPuzzle] = (s, e, _) =>
                ChapterMachineSyncService.ApplyEvidenceLocker(s.Get<EvidenceLockerLogicPuzzle>(e.Type, e.WorldId), e);
            d[PuzzleType.RadioStationTutorial] = (s, e, _) =>
                RadioPuzzleSyncService.ApplyTutorial(s.Get<RadioStationTutorialPuzzle>(e.Type, e.WorldId), e);
            d[PuzzleType.CentralElevator] = (s, e, _) =>
                ElevatorSyncService.ApplyCentral(s.Get<CentralElevatorControl>(e.Type, e.WorldId), e);
            d[PuzzleType.ElevatorCallButton] = (s, e, _) =>
                ElevatorSyncService.ApplyCallButton(s.Get<ElevatorCallButton>(e.Type, e.WorldId), e);
            d[PuzzleType.DoorLockEventInteraction] = (s, e, _) =>
                LockSyncService.ApplyDoorLockEvent(s.Get<DoorLockEventInteraction>(e.Type, e.WorldId), e);
            d[PuzzleType.MultiConditionEvent] = (s, e, _) =>
                StorySyncService.ApplyMultiConditionEvent(s.Get<MultiConditionEvent>(e.Type, e.WorldId), e);
            d[PuzzleType.CryoDoorController] = (s, e, _) =>
                CryoSyncService.ApplyCryoDoorController(s.Get<CryoDoorController>(e.Type, e.WorldId), e);
            d[PuzzleType.CryoDoorLock] = (s, e, cin) =>
                CryoSyncService.ApplyCryoDoorLock(s.Get<CryoDoorLock>(e.Type, e.WorldId), e, cin);
            d[PuzzleType.PEN_Cryo] = (s, e, cin) =>
                CryoSyncService.ApplyPenCryo(s.Get<PEN_Cryo>(e.Type, e.WorldId), e, cin);
            d[PuzzleType.MED_Pump] = (s, e, cin) =>
                PumpFloodSyncService.ApplyPump(s.Get<MED_Pump>(e.Type, e.WorldId), e, cin);
            d[PuzzleType.MED_FloodedBathroom] = (s, e, cin) =>
                PumpFloodSyncService.ApplyFlood(s.Get<MED_FloodedBathroom>(e.Type, e.WorldId), e, cin);
            d[PuzzleType.MED_CardWriter] = (s, e, _) =>
                ChapterMachineSyncService.ApplyCardWriter(s.Get<MED_CardWriter>(e.Type, e.WorldId), e);
            d[PuzzleType.RES_Shutters] = (s, e, _) =>
                ChapterMachineSyncService.ApplyShutters(s.Get<RES_Shutters>(e.Type, e.WorldId), e);
            d[PuzzleType.ROT_Pipes] = (s, e, cin) =>
                PipesSyncService.Apply(s.Get<ROT_Pipes>(e.Type, e.WorldId), e, cin);
            d[PuzzleType.ROT_Magpie] = (s, e, _) =>
                ChapterMachineSyncService.ApplyMagpie(s.Get<ROT_Magpie>(e.Type, e.WorldId), e);
            d[PuzzleType.PEN_Reaktor] = (s, e, _) =>
                ChapterMachineSyncService.ApplyReaktor(s.Get<PEN_Reaktor>(e.Type, e.WorldId), e);
            d[PuzzleType.DET_ServiceLock] = (s, e, _) =>
                LockSyncService.ApplyServiceLock(s.Get<DET_ServiceLock>(e.Type, e.WorldId), e);
            d[PuzzleType.EXC_Seilbahn] = (s, e, cin) =>
                HatchSyncService.ApplySeilbahn(s.Get<EXC_Seilbahn>(e.Type, e.WorldId), e, cin);
            d[PuzzleType.EXC_Hatch] = (s, e, cin) =>
                HatchSyncService.ApplyHatch(s.Get<EXC_Hatch>(e.Type, e.WorldId), e, cin);
            d[PuzzleType.LAB_Rings] = (s, e, _) =>
                ChapterMachineSyncService.ApplyLabRings(s.Get<LAB_Rings>(e.Type, e.WorldId), e);
            d[PuzzleType.BiodomeDoorLock] = (s, e, _) =>
                LockSyncService.ApplyBiodome(s.Get<BiodomeDoorLock>(e.Type, e.WorldId), e);
            d[PuzzleType.ROT_MeatBlocker] = (s, e, _) =>
                ChapterMachineSyncService.ApplyMeatBlocker(s.Get<ROT_MeatBlocker>(e.Type, e.WorldId), e);
            d[PuzzleType.FoldingShutterDoor] = (s, e, _) =>
                PuzzleDoorFlagsSyncService.ApplyFoldingShutter(s.Get<FoldingShutterDoor>(e.Type, e.WorldId), e);
            d[PuzzleType.EventZoneTriggered] = (s, e, _) =>
                EventZonePuzzleSyncService.Apply(s.Get<EventZone>(e.Type, e.WorldId), e, _mutateWorld);
            d[PuzzleType.GlobalAlertStatus] = (s, e, _) => EnemySyncService.ApplyGlobalAlert(e);
            d[PuzzleType.RadioManagerState] = (s, e, _) => RadioPuzzleSyncService.ApplyManager(e);
            d[PuzzleType.StorageBox] = (s, e, cin) =>
                StorageLidSyncService.Apply(s.Get<StorageBox>(e.Type, e.WorldId), e, cin);
            d[PuzzleType.ROT_Tarot] = (s, e, _) =>
                ChapterMachineSyncService.ApplyTarot(s.Get<ROT_Tarot>(e.Type, e.WorldId), e);
            d[PuzzleType.ROT_Mural] = (s, e, _) =>
                ChapterMachineSyncService.ApplyMural(s.Get<ROT_Mural>(e.Type, e.WorldId), e);
            d[PuzzleType.MED_Incinerator] = (s, e, _) =>
                ChapterMachineSyncService.ApplyIncinerator(s.Get<MED_Incinerator>(e.Type, e.WorldId), e);
            d[PuzzleType.LAB_Waage] = (s, e, _) =>
                ChapterMachineSyncService.ApplyWaage(s.Get<LAB_Waage>(e.Type, e.WorldId), e);
            d[PuzzleType.RES_Shrine] = (s, e, _) =>
                ChapterMachineSyncService.ApplyShrine(s.Get<RES_Shrine>(e.Type, e.WorldId), e);
            d[PuzzleType.ROT_RadioAlignment] = (s, e, _) =>
                RadioPuzzleSyncService.ApplyAlignment(s.Get<ROT_RadioAlignment>(e.Type, e.WorldId), e);
            d[PuzzleType.DET_RadioCodeLock] = (s, e, _) =>
                RadioPuzzleSyncService.ApplyCode(s.Get<DET_RadioCodeLock>(e.Type, e.WorldId), e);
            d[PuzzleType.UseItemMulti] = (s, e, _) => UseItemWorldSyncService.ApplyMulti(e);
            d[PuzzleType.SaveRoomEvent] = (s, e, _) =>
                StorySyncService.ApplySaveRoomEvent(s.Get<SaveRoomEvent>(e.Type, e.WorldId), e);
            d[PuzzleType.CutsceneCompleted] = (s, e, _) =>
                StorySyncService.ApplyCutsceneCompleted(s.Get<CutsceneManager>(e.Type, e.WorldId), e);
            d[PuzzleType.DialoguePlayedOnce] = (s, e, _) =>
                StorySyncService.ApplyDialoguePlayedOnce(s.Get<Dialogue>(e.Type, e.WorldId), e);
            d[PuzzleType.EXC_Elevator] = (s, e, _) =>
                ElevatorSyncService.ApplyExc(s.Get<EXC_Elevator>(e.Type, e.WorldId), e);
            d[PuzzleType.KolibriManager] = (s, e, _) =>
                BossSyncService.ApplyKolibri(s.Get<KolibriManager>(e.Type, e.WorldId), e);
            d[PuzzleType.BOS_Adler] = (s, e, _) =>
                BossSyncService.ApplyAdler(s.Get<BOS_Adler>(e.Type, e.WorldId), e);
            d[PuzzleType.EnemyManagerState] = (s, e, _) =>
                EnemySyncService.ApplyPuzzle(s.Get<EnemyManager>(e.Type, e.WorldId), e);
            d[PuzzleType.RES_MusicBox] = (s, e, _) =>
                ResidencyPuzzleSyncService.ApplyMusicBox(s.Get<RES_MusicBox>(e.Type, e.WorldId), e);
            d[PuzzleType.RES_LibraryPC] = (s, e, _) =>
                ResidencyPuzzleSyncService.ApplyLibraryPc(s.Get<RES_LibraryPC>(e.Type, e.WorldId), e);
            d[PuzzleType.RES_Paternoster] = (s, e, _) =>
                ResidencyPuzzleSyncService.ApplyPaternoster(s.Get<RES_Paternoster>(e.Type, e.WorldId), e);
            d[PuzzleType.MED_KeyGrid] = (s, e, _) =>
                ResidencyPuzzleSyncService.ApplyKeyGrid(e);
            d[PuzzleType.ArianePhotoCode] = (s, e, _) =>
                ResidencyPuzzleSyncService.ApplyArianePhotoCode(e);
            d[PuzzleType.DET_ServiceLock_Key] = (s, e, _) =>
                ResidencyPuzzleSyncService.ApplyServiceLockKey(s.Get<DET_ServiceLock_Key>(e.Type, e.WorldId), e);
            d[PuzzleType.SafeDoorSmall] = (s, e, _) =>
                ResidencyPuzzleSyncService.ApplySafeDoorSmall(s.Get<SafeDoorSmall>(e.Type, e.WorldId), e);
            d[PuzzleType.MultiKeyLock] = (s, e, _) =>
                ResidencyPuzzleSyncService.ApplyMultiKeyLock(s.Get<MultiKeyLock>(e.Type, e.WorldId), e);
            d[PuzzleType.OpenableDrawer] = (s, e, _) =>
                ResidencyPuzzleSyncService.ApplyOpenableDrawer(s.Get<OpenableDrawer>(e.Type, e.WorldId), e);
            d[PuzzleType.GunCase] = (s, e, _) =>
                ChapterExtraPuzzleSyncService.ApplyGunCase(s.Get<GunCase>(e.Type, e.WorldId), e);
            d[PuzzleType.AraNest] = (s, e, _) =>
                ChapterExtraPuzzleSyncService.ApplyAraNest(s.Get<AraNest>(e.Type, e.WorldId), e);
            d[PuzzleType.LAB_RifleQuest] = (s, e, _) =>
                ChapterExtraPuzzleSyncService.ApplyRifleQuest(s.Get<LAB_RifleQuest>(e.Type, e.WorldId), e);
            d[PuzzleType.LOV_Microfiche] = (s, e, _) =>
                ChapterExtraPuzzleSyncService.ApplyMicrofiche(s.Get<LOV_Microfiche>(e.Type, e.WorldId), e);
            return d;
        }

        static HashSet<PuzzleType> BuildClientEmitTypes()
        {
            return new HashSet<PuzzleType>
            {
                PuzzleType.PuzzleStatus,
                PuzzleType.InteractiveLock,
                PuzzleType.InteractiveLockSingle,
                PuzzleType.Keypad3D,
                PuzzleType.ROT_Keypad,
                PuzzleType.PEN_Codepad,
                PuzzleType.PatternLock,
                PuzzleType.DialLock,
                PuzzleType.FlipSwitch,
                PuzzleType.FloodControlSwitch,
                PuzzleType.FloodControls,
                PuzzleType.RES_Power,
                PuzzleType.UseItemInteraction,
                PuzzleType.NumberLockNew,
                PuzzleType.DoorLockPuzzle,
                PuzzleType.MultiLock,
                PuzzleType.MED_VentPuzzle,
                PuzzleType.DoorLockControl,
                PuzzleType.EvidenceLockerPuzzle,
                PuzzleType.RadioStationTutorial,
                PuzzleType.ElevatorCallButton,
                PuzzleType.CentralElevator,
                PuzzleType.DoorLockEventInteraction,
                PuzzleType.CryoDoorController,
                PuzzleType.CryoDoorLock,
                PuzzleType.PEN_Cryo,
                PuzzleType.MED_Pump,
                PuzzleType.MED_FloodedBathroom,
                PuzzleType.MED_CardWriter,
                PuzzleType.RES_Shutters,
                PuzzleType.ROT_Pipes,
                PuzzleType.ROT_Magpie,
                // PEN_Reaktor: Dig AG — IsProgressed Bool0||Bool1||Int0!=0||Int1!=0
                PuzzleType.DET_ServiceLock,
                PuzzleType.EXC_Seilbahn,
                PuzzleType.EXC_Hatch,
                PuzzleType.LAB_Rings,
                PuzzleType.BiodomeDoorLock,
                PuzzleType.ROT_MeatBlocker,
                PuzzleType.UseItemMulti,
                PuzzleType.FoldingShutterDoor,
                PuzzleType.ROT_Tarot,
                PuzzleType.ROT_Mural,
                PuzzleType.MED_Incinerator,
                PuzzleType.LAB_Waage,
                PuzzleType.RES_Shrine,
                PuzzleType.ROT_RadioAlignment,
                PuzzleType.DET_RadioCodeLock,
                PuzzleType.RadioManagerState,
                PuzzleType.EXC_Elevator,
                PuzzleType.RES_MusicBox,
                PuzzleType.RES_LibraryPC,
                PuzzleType.RES_Paternoster,
                PuzzleType.DET_ServiceLock_Key,
                PuzzleType.SafeDoorSmall,
                PuzzleType.MultiKeyLock,
                PuzzleType.OpenableDrawer,
                PuzzleType.SwingDoor,
                PuzzleType.DoorwaySimple,
                PuzzleType.StorageBox,
                PuzzleType.MED_KeyGrid,
                PuzzleType.ArianePhotoCode,
                PuzzleType.GunCase,
                PuzzleType.AraNest,
                PuzzleType.LAB_RifleQuest,
                PuzzleType.LOV_Microfiche,
            };
        }

        static HashSet<PuzzleType> BuildProgressedBool0()
        {
            return new HashSet<PuzzleType>
            {
                PuzzleType.PEN_Codepad,
                PuzzleType.Keypad3D,
                PuzzleType.ROT_Keypad,
                PuzzleType.PuzzleStatus,
                PuzzleType.UseItemInteraction,
                PuzzleType.CryoDoorLock,
                PuzzleType.PEN_Cryo,
                PuzzleType.CryoDoorController,
                PuzzleType.PatternLock,
                PuzzleType.MED_Pump,
                PuzzleType.MED_FloodedBathroom,
                PuzzleType.MED_CardWriter,
                PuzzleType.RES_Shutters,
                PuzzleType.ROT_Pipes,
                PuzzleType.ROT_Magpie,
                PuzzleType.PEN_Reaktor,
                PuzzleType.DET_ServiceLock,
                PuzzleType.EXC_Seilbahn,
                PuzzleType.EXC_Hatch,
                PuzzleType.LAB_Rings,
                PuzzleType.BiodomeDoorLock,
                PuzzleType.ROT_MeatBlocker,
                PuzzleType.FlipSwitch,
                PuzzleType.FloodControls,
                PuzzleType.StorageBox,
                PuzzleType.RES_MusicBox,
                PuzzleType.RES_LibraryPC,
                PuzzleType.RES_Paternoster,
                PuzzleType.MED_KeyGrid,
                PuzzleType.ArianePhotoCode,
                PuzzleType.DET_ServiceLock_Key,
                PuzzleType.SafeDoorSmall,
                PuzzleType.MultiKeyLock,
                PuzzleType.OpenableDrawer,
                PuzzleType.DET_RadioCodeLock,
                PuzzleType.SaveRoomEvent,
                PuzzleType.DialoguePlayedOnce,
                PuzzleType.GunCase,
                PuzzleType.AraNest,
                PuzzleType.LAB_RifleQuest,
                PuzzleType.LOV_Microfiche,
                PuzzleType.ElevatorCallButton,
                PuzzleType.FloodControlSwitch,
                PuzzleType.DialLock,
                PuzzleType.MultiLock,
                PuzzleType.DoorLockEventInteraction,
                PuzzleType.MED_VentPuzzle,
                PuzzleType.RadioStationTutorial,
                PuzzleType.RES_Power,
                PuzzleType.MED_Incinerator,
                PuzzleType.ROT_Tarot,
                PuzzleType.MultiConditionEvent,
                PuzzleType.CutsceneCompleted,
            };
        }

        // Do not poll Interaction.triggered. Door/move Interactions teleport the local
        // Elster when the other peer walks through a ConnectedDoors link.
        static PuzzleScanner[] BuildScanners()
        {
            return new PuzzleScanner[]
            {
                s => s.RegisterAll<PuzzleStatus>(PuzzleType.PuzzleStatus),
                s => s.RegisterAll<InteractiveLock>(PuzzleType.InteractiveLock),
                s => s.RegisterAll<InteractiveLockSingle>(PuzzleType.InteractiveLockSingle),
                s => s.RegisterAll<Keypad3D>(PuzzleType.Keypad3D),
                s => s.RegisterAll<ROT_Keypad>(PuzzleType.ROT_Keypad),
                s => s.RegisterAll<PEN_Codepad>(PuzzleType.PEN_Codepad),
                s => s.RegisterAll<LAB_PatternLock>(PuzzleType.PatternLock),
                s => s.RegisterAll<ROT_DialLock>(PuzzleType.DialLock),
                s => s.RegisterAll<FlipSwitch>(PuzzleType.FlipSwitch),
                s => s.RegisterAll<FloodControlSwitch>(PuzzleType.FloodControlSwitch),
                s => s.RegisterAll<FloodControls>(PuzzleType.FloodControls),
                s => s.RegisterAll<RES_Power>(PuzzleType.RES_Power),
                s => s.RegisterAll<UseItemInteraction>(PuzzleType.UseItemInteraction),
                s => s.RegisterAll<NumberLockNew>(PuzzleType.NumberLockNew),
                s => s.RegisterAll<DoorLockPuzzle>(PuzzleType.DoorLockPuzzle),
                s => s.RegisterAll<MED_MultiLock>(PuzzleType.MultiLock),
                s => s.RegisterAll<LAB_MultiLock>(PuzzleType.MultiLock),
                s => s.RegisterAll<MED_VentPuzzle>(PuzzleType.MED_VentPuzzle),
                s => s.RegisterAll<Doorway_simple>(PuzzleType.DoorwaySimple),
                s => s.RegisterAll<SwingDoor>(PuzzleType.SwingDoor),
                s => s.RegisterAll<DoorLockControl>(PuzzleType.DoorLockControl),
                s => s.RegisterAll<EvidenceLockerLogicPuzzle>(PuzzleType.EvidenceLockerPuzzle),
                s => s.RegisterAll<RadioStationTutorialPuzzle>(PuzzleType.RadioStationTutorial),
                s => s.RegisterAll<CentralElevatorControl>(PuzzleType.CentralElevator),
                s => s.RegisterAll<ElevatorCallButton>(PuzzleType.ElevatorCallButton),
                s => s.RegisterAll<DoorLockEventInteraction>(PuzzleType.DoorLockEventInteraction),
                s => s.RegisterAll<MultiConditionEvent>(PuzzleType.MultiConditionEvent),
                s => s.RegisterAll<CryoDoorController>(PuzzleType.CryoDoorController),
                s => s.RegisterAll<CryoDoorLock>(PuzzleType.CryoDoorLock),
                s => s.RegisterAll<PEN_Cryo>(PuzzleType.PEN_Cryo),
                s => s.RegisterAll<MED_Pump>(PuzzleType.MED_Pump),
                s => s.RegisterAll<MED_FloodedBathroom>(PuzzleType.MED_FloodedBathroom),
                s => s.RegisterAll<MED_CardWriter>(PuzzleType.MED_CardWriter),
                s => s.RegisterAll<RES_Shutters>(PuzzleType.RES_Shutters),
                s => s.RegisterAll<ROT_Pipes>(PuzzleType.ROT_Pipes),
                s => s.RegisterAll<ROT_Magpie>(PuzzleType.ROT_Magpie),
                s => s.RegisterAll<PEN_Reaktor>(PuzzleType.PEN_Reaktor),
                s => s.RegisterAll<DET_ServiceLock>(PuzzleType.DET_ServiceLock),
                s => s.RegisterAll<EXC_Seilbahn>(PuzzleType.EXC_Seilbahn),
                s => s.RegisterAll<EXC_Hatch>(PuzzleType.EXC_Hatch),
                s => s.RegisterAll<LAB_Rings>(PuzzleType.LAB_Rings),
                s => s.RegisterAll<BiodomeDoorLock>(PuzzleType.BiodomeDoorLock),
                s => s.RegisterAll<ROT_MeatBlocker>(PuzzleType.ROT_MeatBlocker),
                s => s.RegisterAll<FoldingShutterDoor>(PuzzleType.FoldingShutterDoor),
                s => s.RegisterAll<EventZone>(PuzzleType.EventZoneTriggered),
                s => s.RegisterAll<EnemyManager>(PuzzleType.EnemyManagerState),
                s => s.RegisterAll<StorageBox>(PuzzleType.StorageBox),
                s => s.RegisterAll<ROT_Tarot>(PuzzleType.ROT_Tarot),
                s => s.RegisterAll<ROT_Mural>(PuzzleType.ROT_Mural),
                s => s.RegisterAll<MED_Incinerator>(PuzzleType.MED_Incinerator),
                s => s.RegisterAll<LAB_Waage>(PuzzleType.LAB_Waage),
                s => s.RegisterAll<RES_Shrine>(PuzzleType.RES_Shrine),
                s => s.RegisterAll<ROT_RadioAlignment>(PuzzleType.ROT_RadioAlignment),
                s => s.RegisterAll<DET_RadioCodeLock>(PuzzleType.DET_RadioCodeLock),
                s => s.RegisterAll<UseItemMultiInteraction>(PuzzleType.UseItemMulti),
                s => s.RegisterAll<SaveRoomEvent>(PuzzleType.SaveRoomEvent),
                s => s.RegisterAll<CutsceneManager>(PuzzleType.CutsceneCompleted),
                s => s.RegisterAll<Dialogue>(PuzzleType.DialoguePlayedOnce),
                s => s.RegisterAll<EXC_Elevator>(PuzzleType.EXC_Elevator),
                s => s.RegisterAll<KolibriManager>(PuzzleType.KolibriManager),
                s => s.RegisterAll<BOS_Adler>(PuzzleType.BOS_Adler),
                s => s.RegisterAll<RES_MusicBox>(PuzzleType.RES_MusicBox),
                s => s.RegisterAll<RES_LibraryPC>(PuzzleType.RES_LibraryPC),
                s => s.RegisterAll<RES_Paternoster>(PuzzleType.RES_Paternoster),
                s => s.RegisterAll<MED_KeyGrid>(PuzzleType.MED_KeyGrid),
                s => s.RegisterAll<ArianePhotoCode>(PuzzleType.ArianePhotoCode),
                s => s.RegisterAll<DET_ServiceLock_Key>(PuzzleType.DET_ServiceLock_Key),
                s => s.RegisterAll<SafeDoorSmall>(PuzzleType.SafeDoorSmall),
                s => s.RegisterAll<MultiKeyLock>(PuzzleType.MultiKeyLock),
                s => s.RegisterAll<OpenableDrawer>(PuzzleType.OpenableDrawer),
                s => s.RegisterAll<GunCase>(PuzzleType.GunCase),
                s => s.RegisterAll<AraNest>(PuzzleType.AraNest),
                s => s.RegisterAll<LAB_RifleQuest>(PuzzleType.LAB_RifleQuest),
                s => s.RegisterAll<LOV_Microfiche>(PuzzleType.LOV_Microfiche),
            };
        }
    }
}
