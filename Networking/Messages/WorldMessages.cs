// SyncRADation wire: Doors and puzzles (PuzzleType rows: Domains/Puzzles/PuzzleSpecs.cs).
using System;
using LiteNetLib.Utils;
using UnityEngine;

namespace SyncRADation.Networking
{
    public enum DoorType : byte
    {
        DoorwayDouble = 0,
        DoorwaySimple = 1,
        EventSlidingDoor = 2,
        ConnectedDoors = 3,
    }

    public struct DoorStateMessage
    {
        public int SenderPlayerId;
        public DoorType Type;
        public long WorldId;
        public bool Open;
        public bool Locked;
        public bool InProgress;  // ConnectedDoors
        public bool Forwards;    // ConnectedDoors
        public bool Moving;      // EventSlidingDoor

        public void Serialize(NetDataWriter w)
        {
            w.Put(SenderPlayerId);
            w.Put((byte)Type);
            w.Put(WorldId);
            w.Put(Open);
            w.Put(Locked);
            w.Put(InProgress);
            w.Put(Forwards);
            w.Put(Moving);
        }

        public static DoorStateMessage Deserialize(NetDataReader r)
        {
            return new DoorStateMessage
            {
                SenderPlayerId = r.GetInt(),
                Type = (DoorType)r.GetByte(),
                WorldId = r.GetLong(),
                Open = r.GetBool(),
                Locked = r.GetBool(),
                InProgress = r.GetBool(),
                Forwards = r.GetBool(),
                Moving = r.GetBool()
            };
        }
    }

    public enum PuzzleType : byte
    {
        PuzzleStatus = 1,
        InteractiveLock = 2,
        InteractiveLockSingle = 3,
        Keypad3D = 4,
        ROT_Keypad = 5,
        PEN_Codepad = 6,
        PatternLock = 7,
        DialLock = 8,
        FlipSwitch = 9,
        FloodControlSwitch = 10,
        FloodControls = 11,
        RES_Power = 12,
        UseItemInteraction = 13,
        NumberLockNew = 14,
        DoorLockPuzzle = 15,
        MultiLock = 16,
        MED_VentPuzzle = 17,
        DoorwaySimple = 18,
        SwingDoor = 19,
        DoorLockControl = 20,
        EvidenceLockerPuzzle = 21,
        RadioStationTutorial = 22,
        CentralElevator = 23,
        ElevatorCallButton = 24,
        DoorLockEventInteraction = 25,
        MultiConditionEvent = 26,
        CryoDoorController = 27,
        FoldingShutterDoor = 28,
        // Intentional: never register Reader/Applier/Scanner — ConnectedDoors teleport risk
        // if Interaction.triggered is polled/applied across peers.
        InteractionTriggered = 29,
        EventZoneTriggered = 30,
        GlobalAlertStatus = 31,
        RadioManagerState = 32,
        EnemyManagerState = 33,
        StorageBox = 34,
        ROT_Tarot = 35,
        ROT_Mural = 36,
        MED_Incinerator = 37,
        LAB_Waage = 38,
        RES_Shrine = 39,
        ROT_RadioAlignment = 40,
        DET_RadioCodeLock = 41,
        UseItemMulti = 42,
        SaveRoomEvent = 43,
        CutsceneCompleted = 44,
        DialoguePlayedOnce = 45,
        EXC_Elevator = 46,
        KolibriManager = 47,
        BOS_Adler = 48,
        CryoDoorLock = 49,
        PEN_Cryo = 50,
        MED_Pump = 51,
        MED_FloodedBathroom = 52,
        MED_CardWriter = 53,
        RES_Shutters = 54,
        ROT_Pipes = 55,
        ROT_Magpie = 56,
        PEN_Reaktor = 57,
        DET_ServiceLock = 58,
        EXC_Seilbahn = 59,
        EXC_Hatch = 60,
        LAB_Rings = 61,
        BiodomeDoorLock = 62,
        ROT_MeatBlocker = 63,
        // v9 — decompile coverage (was parked)
        RES_MusicBox = 64,
        RES_LibraryPC = 65,
        RES_Paternoster = 66,
        MED_KeyGrid = 67,
        ArianePhotoCode = 68,
        DET_ServiceLock_Key = 69,
        SafeDoorSmall = 70,
        MultiKeyLock = 71,
        OpenableDrawer = 72,
        // v10 — decompile coverage loop
        GunCase = 73,
        AraNest = 74,
        LAB_RifleQuest = 75,
        LOV_Microfiche = 76,
        // v11 — MED_Adler_EVdoors DoorL/DoorR local X pose
        MED_Adler_EVdoors = 77,
        // uncovered durable world objects (puzzle worker)
        ROT_DiskManager = 78,
        DET_WallCreature = 79,
        MapReveal = 80,
        // MEM_ChecklistLogic: Int0 = checked-item bitmask (monotonic, host ORs concurrent edits), Bool0 = complete
        MEM_ChecklistLogic = 81,
    }

    public struct PuzzleStateEntry
    {
        public PuzzleType Type;
        /// <summary>Stable WorldId (FNV scene+path). 0 = global singleton (radio/alert).</summary>
        public long WorldId;
        public bool Bool0;
        public bool Bool1;
        public bool Bool2;
        public int Int0;
        public int Int1;
        public int Int2;
        public int Int3;
        public float Float0;
        /// <summary>v9: Kolibri radioIntensity / Adler progress.</summary>
        public float Float1;
        /// <summary>
        /// Host-stamped version of the resulting state (monotonic per type+WorldId). 0 = unversioned.
        /// Client-authored entries carry the last host Seq the client had applied (its base version).
        /// </summary>
        public int Seq;
        /// <summary>
        /// Client-authored edit mask vs the sender's previous state: bits 0..8 = Bool0,Bool1,Bool2,Int0..Int3,Float0,Float1
        /// changed; bits 16..23 = 16-bit half of Int0..Int3 changed (lo,hi per int). The host merges only those
        /// cells onto its current state. 0 = no merge info (whole entry, arrival order).
        /// </summary>
        public int Mask;

        public void Serialize(NetDataWriter w)
        {
            w.Put((byte)Type);
            w.Put(WorldId);
            w.Put(Bool0);
            w.Put(Bool1);
            w.Put(Bool2);
            w.Put(Int0);
            w.Put(Int1);
            w.Put(Int2);
            w.Put(Int3);
            w.Put(Float0);
            w.Put(Float1);
            w.Put(Seq);
            w.Put(Mask);
        }

        public static PuzzleStateEntry Deserialize(NetDataReader r)
        {
            return new PuzzleStateEntry
            {
                Type = (PuzzleType)r.GetByte(),
                WorldId = r.GetLong(),
                Bool0 = r.GetBool(),
                Bool1 = r.GetBool(),
                Bool2 = r.GetBool(),
                Int0 = r.GetInt(),
                Int1 = r.GetInt(),
                Int2 = r.GetInt(),
                Int3 = r.GetInt(),
                Float0 = r.GetFloat(),
                Float1 = r.GetFloat(),
                Seq = r.GetInt(),
                Mask = r.GetInt()
            };
        }
    }

    public struct PuzzleStateMessage
    {
        public int SenderPlayerId;
        public PuzzleStateEntry[] Entries;
        public bool FullRefresh;

        public void Serialize(NetDataWriter w)
        {
            w.Put(SenderPlayerId);
            w.Put(FullRefresh);
            int cnt = Entries != null ? Entries.Length : 0;
            cnt = NetWire.ClampCount(cnt, NetWire.MaxPuzzleEntries, "PuzzleState");
            w.Put(cnt);
            for (int i = 0; i < cnt; i++)
                Entries[i].Serialize(w);
        }

        public static PuzzleStateMessage Deserialize(NetDataReader r)
        {
            var msg = new PuzzleStateMessage
            {
                SenderPlayerId = r.GetInt(),
                FullRefresh = r.GetBool()
            };
            int cnt = NetWire.ReadCount(r, NetWire.MaxPuzzleEntries, "PuzzleState");
            if (cnt > 0)
            {
                msg.Entries = new PuzzleStateEntry[cnt];
                for (int i = 0; i < cnt; i++)
                    msg.Entries[i] = PuzzleStateEntry.Deserialize(r);
            }
            return msg;
        }
    }
}
