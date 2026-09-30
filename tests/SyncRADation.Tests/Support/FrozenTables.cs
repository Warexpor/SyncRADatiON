// Frozen wire snapshots. Changing any value here is a PROTOCOL CHANGE: bump PluginInfo.ProtocolVersion,
// the ProtocolVersion/SchemaHash assertions in FrozenConstantsTests, and CHANGELOG in the same commit.
using System;

namespace SyncRADation.Tests
{
    internal static class FrozenTables
    {
        public static readonly (string Name, int Value)[] T_NetMessageType =
        {
            ("Handshake", 1), ("PlayerState", 2), ("DoorState", 8), ("DropItemSpawn", 9),
            ("ItemPickedUp", 10), ("FriendlyFire", 11), ("EnemyState", 12), ("EnemyDamage", 13),
            ("SceneHello", 14), ("PuzzleState", 15), ("BossState", 17), ("SnapshotRequest", 19),
            ("WorldPickupState", 20), ("PlayerVital", 21), ("WorldPickupClaim", 22), ("WorldPickupGrant", 23),
            ("SceneFollow", 24), ("InteractionRequest", 25), ("InteractionAck", 26), ("StoryCommit", 27),
            ("StoryPresentation", 28), ("StorageBoxBlob", 29), ("PartyKeyRing", 30), ("DeathPolicy", 31),
            ("FmodEmitter", 32), ("PlayerRoster", 33), ("BonePose", 34), ("EnemySpawn", 35),
            ("PartyLife", 40), ("PartySave", 41), ("PartyRoom", 42), ("WorldPickupDeny", 60),
            ("AvatarOneShot", 61), ("BossHit", 62), ("EnemyAction", 63), ("FmodEmitterRequest", 67), ("DropRekey", 73),
        };

        public static readonly (string Name, int Value)[] T_InteractionKind =
        {
            ("None", 0), ("EventZone", 1), ("UseItem", 2), ("KeypadSubmit", 3),
            ("DialogueStart", 4), ("CutsceneStart", 5), ("EventScreenStart", 6), ("EventScreenExit", 7),
            ("CutsceneSkip", 8), ("DialogueContinue", 9), ("DialogueEnd", 10), ("StoragePut", 11),
            ("StorageTake", 12), ("Gunshot", 13), ("MultiCondition", 14), ("SceneFollowRequest", 15),
            ("UseItemMulti", 16), ("CutsceneProceed", 17), ("BookOpen", 18), ("BookMemory", 19),
            ("DroppedPickup", 20), ("InspectFlag", 21),
        };

        public static readonly (string Name, int Value)[] T_StoryCmd =
        {
            ("None", 0), ("DialogueStart", 1), ("DialogueContinue", 2), ("DialogueEnd", 3),
            ("CutsceneStart", 4), ("CutsceneSkip", 5), ("CutsceneProceed", 6), ("EventScreenStart", 7),
            ("EventScreenExit", 8), ("OpenBookMemory", 9), ("DetermineEnding", 10), ("DialoguerStartId", 11),
            ("EventZoneFire", 12), ("MultiConditionFire", 13), ("BookOpen", 14), ("GoToPenny", 20),
            ("PartyCheat", 21), ("EndDelta", 22), ("EndGraves", 23),
        };

        public static readonly (string Name, int Value)[] T_DeathKind =
        {
            ("ClientDowned", 1), ("HostWipeReload", 2),
        };

        public static readonly (string Name, int Value)[] T_WeaponType =
        {
            ("None", 0), ("Handgun", 1), ("Melee", 2), ("Pistol", 3),
            ("Revolver", 4), ("Shotgun", 5), ("Rifle", 6), ("SMG", 7),
            ("Flare", 8), ("CAR", 9),
        };

        public static readonly (string Name, int Value)[] T_DoorType =
        {
            ("DoorwayDouble", 0), ("DoorwaySimple", 1), ("EventSlidingDoor", 2), ("ConnectedDoors", 3),
        };

        public static readonly (string Name, int Value)[] T_PuzzleType =
        {
            ("PuzzleStatus", 1), ("InteractiveLock", 2), ("InteractiveLockSingle", 3), ("Keypad3D", 4),
            ("ROT_Keypad", 5), ("PEN_Codepad", 6), ("PatternLock", 7), ("DialLock", 8),
            ("FlipSwitch", 9), ("FloodControlSwitch", 10), ("FloodControls", 11), ("RES_Power", 12),
            ("UseItemInteraction", 13), ("NumberLockNew", 14), ("DoorLockPuzzle", 15), ("MultiLock", 16),
            ("MED_VentPuzzle", 17), ("DoorwaySimple", 18), ("SwingDoor", 19), ("DoorLockControl", 20),
            ("EvidenceLockerPuzzle", 21), ("RadioStationTutorial", 22), ("CentralElevator", 23), ("ElevatorCallButton", 24),
            ("DoorLockEventInteraction", 25), ("MultiConditionEvent", 26), ("CryoDoorController", 27), ("FoldingShutterDoor", 28),
            ("InteractionTriggered", 29), ("EventZoneTriggered", 30), ("GlobalAlertStatus", 31), ("RadioManagerState", 32),
            ("EnemyManagerState", 33), ("StorageBox", 34), ("ROT_Tarot", 35), ("ROT_Mural", 36),
            ("MED_Incinerator", 37), ("LAB_Waage", 38), ("RES_Shrine", 39), ("ROT_RadioAlignment", 40),
            ("DET_RadioCodeLock", 41), ("UseItemMulti", 42), ("SaveRoomEvent", 43), ("CutsceneCompleted", 44),
            ("DialoguePlayedOnce", 45), ("EXC_Elevator", 46), ("KolibriManager", 47), ("BOS_Adler", 48),
            ("CryoDoorLock", 49), ("PEN_Cryo", 50), ("MED_Pump", 51), ("MED_FloodedBathroom", 52),
            ("MED_CardWriter", 53), ("RES_Shutters", 54), ("ROT_Pipes", 55), ("ROT_Magpie", 56),
            ("PEN_Reaktor", 57), ("DET_ServiceLock", 58), ("EXC_Seilbahn", 59), ("EXC_Hatch", 60),
            ("LAB_Rings", 61), ("BiodomeDoorLock", 62), ("ROT_MeatBlocker", 63), ("RES_MusicBox", 64),
            ("RES_LibraryPC", 65), ("RES_Paternoster", 66), ("MED_KeyGrid", 67), ("ArianePhotoCode", 68),
            ("DET_ServiceLock_Key", 69), ("SafeDoorSmall", 70), ("MultiKeyLock", 71), ("OpenableDrawer", 72),
            ("GunCase", 73), ("AraNest", 74), ("LAB_RifleQuest", 75), ("LOV_Microfiche", 76),
            ("MED_Adler_EVdoors", 77), ("ROT_DiskManager", 78), ("DET_WallCreature", 79), ("MapReveal", 80),
            ("MEM_ChecklistLogic", 81),
        };

        public static readonly (string Name, int Value)[] T_EnemyActionKind =
        {
            ("Kill", 0), ("KillSilent", 1), ("Knockback", 2), ("GetPushed", 3),
            ("Burndown", 4), ("WakeUp", 5),
        };

        public static readonly (string Name, int Value)[] T_BossHitKind =
        {
            ("Damage", 0), ("Stab", 1), ("TakeSpear", 2), ("ChimeraShot", 3),
        };

        public static readonly (string Name, int Value)[] T_BossType =
        {
            ("END_Boss", 0), ("LAB_ChimeraBoss", 1), ("MED_MynahBoss", 2),
        };

        public static readonly (string Name, int Value)[] T_PartyLifeKind =
        {
            ("Revive", 1), ("Wipe", 2),
        };
    }
}
