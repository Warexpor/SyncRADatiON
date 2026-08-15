// SyncRADation — all protocol structs (messages, enums, snapshots) + serialization
using System;
using LiteNetLib.Utils;
using UnityEngine;

namespace SyncRADation.Networking
{
    // Protocol v7 wire types. Host relays gameplay; host owns world/story.
    public enum NetMessageType : byte
    {
        Handshake = 1,
        PlayerState = 2,
        DoorState = 8,
        DropItemSpawn = 9,
        ItemPickedUp = 10,
        FriendlyFire = 11,
        EnemyState = 12,
        EnemyDamage = 13,
        SceneHello = 14,
        PuzzleState = 15,
        BossState = 17,
        SnapshotRequest = 19,
        WorldPickupState = 20,
        PlayerVital = 21,
        WorldPickupClaim = 22,
        WorldPickupGrant = 23,
        SceneFollow = 24,
        InteractionRequest = 25,
        InteractionAck = 26,
        StoryCommit = 27,
        StoryPresentation = 28,
        StorageBoxBlob = 29,
        PartyKeyRing = 30,
        DeathPolicy = 31,
        FmodEmitter = 32,
        PlayerRoster = 33,
        BonePose = 34,
        _Highest = 35
    }

    public enum InteractionKind : byte
    {
        None = 0,
        EventZone = 1,
        UseItem = 2,
        KeypadSubmit = 3,
        DialogueStart = 4,
        CutsceneStart = 5,
        EventScreenStart = 6,
        EventScreenExit = 7,
        CutsceneSkip = 8,
        DialogueContinue = 9,
        DialogueEnd = 10,
        StoragePut = 11,
        StorageTake = 12,
        Gunshot = 13,
        MultiCondition = 14,
        SceneFollowRequest = 15,
        UseItemMulti = 16,
        CutsceneProceed = 17,
        BookOpen = 18,
        BookMemory = 19,
        DroppedPickup = 20,
    }

    public enum StoryCmd : byte
    {
        None = 0,
        DialogueStart = 1,
        DialogueContinue = 2,
        DialogueEnd = 3,
        CutsceneStart = 4,
        CutsceneSkip = 5,
        CutsceneProceed = 6,
        EventScreenStart = 7,
        EventScreenExit = 8,
        OpenBookMemory = 9,
        DetermineEnding = 10,
        DialoguerStartId = 11,
        EventZoneFire = 12,
        MultiConditionFire = 13,
        BookOpen = 14,
    }

    public enum DeathKind : byte
    {
        ClientDowned = 1,
        HostWipeReload = 2,
    }

    /// <summary>Host → all: full session id list (includes 0). Leave is an omission; clients prune proxies.</summary>
    public struct PlayerRosterMessage
    {
        public int[] PlayerIds;

        public void Serialize(NetDataWriter w)
        {
            int n = PlayerIds != null ? PlayerIds.Length : 0;
            if (n > 32) n = 32;
            w.Put((byte)n);
            for (int i = 0; i < n; i++)
                w.Put(PlayerIds[i]);
        }

        public static PlayerRosterMessage Deserialize(NetDataReader r)
        {
            int n = r.GetByte();
            var ids = n > 0 ? new int[n] : Array.Empty<int>();
            for (int i = 0; i < n; i++)
                ids[i] = r.GetInt();
            return new PlayerRosterMessage { PlayerIds = ids };
        }
    }

    public struct SnapshotRequestMessage
    {
        public int SenderPlayerId;

        public void Serialize(NetDataWriter w) => w.Put(SenderPlayerId);

        public static SnapshotRequestMessage Deserialize(NetDataReader r) =>
            new SnapshotRequestMessage { SenderPlayerId = r.GetInt() };
    }

    public struct WorldPickupEntry
    {
        public long WorldId;
        public bool Triggered;
        public bool Active;

        public void Serialize(NetDataWriter w)
        {
            w.Put(WorldId);
            w.Put(Triggered);
            w.Put(Active);
        }

        public static WorldPickupEntry Deserialize(NetDataReader r) =>
            new WorldPickupEntry
            {
                WorldId = r.GetLong(),
                Triggered = r.GetBool(),
                Active = r.GetBool()
            };
    }

    public struct WorldPickupStateMessage
    {
        public int SenderPlayerId;
        public bool FullRefresh;
        public WorldPickupEntry[] Entries;

        public void Serialize(NetDataWriter w)
        {
            w.Put(SenderPlayerId);
            w.Put(FullRefresh);
            int n = Entries != null ? Entries.Length : 0;
            w.Put(n);
            for (int i = 0; i < n; i++)
                Entries[i].Serialize(w);
        }

        public static WorldPickupStateMessage Deserialize(NetDataReader r)
        {
            var msg = new WorldPickupStateMessage
            {
                SenderPlayerId = r.GetInt(),
                FullRefresh = r.GetBool()
            };
            int n = r.GetInt();
            if (n > 0 && n < 8192)
            {
                msg.Entries = new WorldPickupEntry[n];
                for (int i = 0; i < n; i++)
                    msg.Entries[i] = WorldPickupEntry.Deserialize(r);
            }
            return msg;
        }
    }

    public struct PlayerVitalMessage
    {
        public int SenderPlayerId;
        public int Hp;
        public int MaxHp;
        public byte GameState;
        public byte CharState;
        public bool Dead;

        public void Serialize(NetDataWriter w)
        {
            w.Put(SenderPlayerId);
            w.Put(Hp);
            w.Put(MaxHp);
            w.Put(GameState);
            w.Put(CharState);
            w.Put(Dead);
        }

        public static PlayerVitalMessage Deserialize(NetDataReader r) =>
            new PlayerVitalMessage
            {
                SenderPlayerId = r.GetInt(),
                Hp = r.GetInt(),
                MaxHp = r.GetInt(),
                GameState = r.GetByte(),
                CharState = r.GetByte(),
                Dead = r.GetBool()
            };
    }

    public struct HandshakeMessage
    {
        public int ProtocolVersion;
        public int AssignedPlayerId;

        public void Serialize(NetDataWriter w)
        {
            w.Put(ProtocolVersion);
            w.Put(AssignedPlayerId);
        }

        public static HandshakeMessage Deserialize(NetDataReader r)
        {
            return new HandshakeMessage
            {
                ProtocolVersion = r.GetInt(),
                AssignedPlayerId = r.GetInt()
            };
        }
    }

    public enum WeaponType : byte
    {
        None = 0,
        Handgun = 1,
        Melee = 2,
        Pistol = 3,
        Revolver = 4,
        Shotgun = 5,
        Rifle = 6,
        SMG = 7,
        Flare = 8,
        CAR = 9,
    }

    [System.Flags]
    public enum AnimBools : uint
    {
        Aiming = 1 << 0,
        Shooting = 1 << 1,
        Running = 1 << 2,
        Grounded = 1 << 3,
        Crouch = 1 << 4,
        Blocked = 1 << 5,
        Dead = 1 << 6,
        Inventory = 1 << 7,
        Attack = 1 << 8,
        Injured = 1 << 9,
        Stomp = 1 << 10,
        Push = 1 << 11,
        Melee = 1 << 12,
        Snap = 1 << 13,
        Reload = 1 << 14,
        Swap = 1 << 15,
        Burst = 1 << 16,
        Taser = 1 << 17,
        Random = 1 << 18,
        Hugged = 1 << 19,
        ReloadRounds = 1 << 20,
        ReloadChamber = 1 << 21,
        EmptyClick = 1 << 22,
    }

    [System.Flags]
    public enum AnimTriggers : ushort
    {
        None = 0,
        Hurt = 1 << 0,
        Die = 1 << 1,
        Fire = 1 << 2,
        Pickup = 1 << 3,
        Radio = 1 << 4,
        Drop = 1 << 5,
        Sleep = 1 << 6,
        Injector = 1 << 7,
        InjectorCancel = 1 << 8,
        ReloadTrigger = 1 << 9,
        AttackTrigger = 1 << 10,
        SwapTrigger = 1 << 11,
        BurstTrigger = 1 << 12,
        StompTrigger = 1 << 13,
        PushTrigger = 1 << 14,
        SnapTrigger = 1 << 15,
    }

    public struct PlayerStateMessage
    {
        public int SenderPlayerId;
        public float PosX;
        public float PosY;
        public float PosZ;
        public float RotY;   // facing-pivot world quat.w (was fAngle)
        public float RootY;  // quat.y
        public float VelX;
        public float VelZ;
        public float Forward;
        public float Turn;
        public float AimingTime;
        public float Stamina;
        public float Blend;
        public float IKwalk;
        public float InputX;
        public float InputY;
        public float HurtTime;
        public byte CharState;
        public byte Facing;
        public WeaponType Weapon;
        public AnimBools AnimBools;
        public AnimTriggers AnimTriggers;
        public bool StepHappened;
        public bool Climbing;
        public byte ModelState;   // CharacterModelType.ElsterType
        public bool WearHat;
        public float RootX;  // quat.x
        public float RootZ;  // quat.z
        public float[] BoneRotations;

        public void SetFacingWorld(Quaternion q)
        {
            if (q.w < 0f)
                q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
            RootX = q.x;
            RootY = q.y;
            RootZ = q.z;
            RotY = q.w;
        }

        public Quaternion GetFacingWorld()
        {
            var q = new Quaternion(RootX, RootY, RootZ, RotY);
            float mag = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (mag < 0.0001f)
                return Quaternion.identity;
            mag = Mathf.Sqrt(mag);
            return new Quaternion(q.x / mag, q.y / mag, q.z / mag, q.w / mag);
        }

        public void Serialize(NetDataWriter w)
        {
            w.Put(SenderPlayerId);
            w.Put(PosX);
            w.Put(PosY);
            w.Put(PosZ);
            w.Put(RotY);
            w.Put(RootY);
            w.Put(VelX);
            w.Put(VelZ);
            w.Put(Forward);
            w.Put(Turn);
            w.Put(AimingTime);
            w.Put(Stamina);
            w.Put(Blend);
            w.Put(IKwalk);
            w.Put(InputX);
            w.Put(InputY);
            w.Put(HurtTime);
            w.Put(CharState);
            w.Put(Facing);
            w.Put((byte)Weapon);
            w.Put((uint)AnimBools);
            w.Put((ushort)AnimTriggers);
            w.Put(StepHappened);
            w.Put(Climbing);
            w.Put(ModelState);
            w.Put(WearHat);
            w.Put(RootX);
            w.Put(RootZ);
            int bc = (BoneRotations != null) ? BoneRotations.Length : 0;
            w.Put(bc);
            for (int i = 0; i < bc; i++)
                w.Put(EncodeAngle(BoneRotations[i]));
        }

        public static PlayerStateMessage Deserialize(NetDataReader r)
        {
            var msg = new PlayerStateMessage
            {
                SenderPlayerId = r.GetInt(),
                PosX = r.GetFloat(),
                PosY = r.GetFloat(),
                PosZ = r.GetFloat(),
                RotY = r.GetFloat(),
                RootY = r.GetFloat(),
                VelX = r.GetFloat(),
                VelZ = r.GetFloat(),
                Forward = r.GetFloat(),
                Turn = r.GetFloat(),
                AimingTime = r.GetFloat(),
                Stamina = r.GetFloat(),
                Blend = r.GetFloat(),
                IKwalk = r.GetFloat(),
                InputX = r.GetFloat(),
                InputY = r.GetFloat(),
                HurtTime = r.GetFloat(),
                CharState = r.GetByte(),
                Facing = r.GetByte(),
                Weapon = (WeaponType)r.GetByte(),
                AnimBools = (AnimBools)r.GetUInt(),
                AnimTriggers = (AnimTriggers)r.GetUShort(),
                StepHappened = r.GetBool(),
                Climbing = r.GetBool(),
                ModelState = r.GetByte(),
                WearHat = r.GetBool(),
                RootX = r.GetFloat(),
                RootZ = r.GetFloat()
            };
            int bc = r.GetInt();
            if (bc > 0 && bc < 4096)
            {
                msg.BoneRotations = new float[bc];
                for (int i = 0; i < bc; i++)
                    msg.BoneRotations[i] = DecodeAngle(r.GetUShort());
            }
            return msg;
        }

        public static ushort EncodeAngle(float angle)
        {
            return (ushort)(Mathf.Clamp(angle, 0f, 360f) / 360f * 65535f);
        }

        public static float DecodeAngle(ushort encoded)
        {
            return (float)encoded / 65535f * 360f;
        }
    }

    /// <summary>Sequenced bone chunk. LiteNetLib sequenced MTU is 1020 — never pack the full tree into PlayerState.</summary>
    public struct BonePoseMessage
    {
        public int SenderPlayerId;
        public ushort TotalBones;
        public ushort StartBone;
        public float[] Eulers; // Count * 3, Count = Eulers.Length / 3

        public int Count => Eulers != null ? Eulers.Length / 3 : 0;

        public void Serialize(NetDataWriter w)
        {
            w.Put(SenderPlayerId);
            w.Put(TotalBones);
            w.Put(StartBone);
            int count = Count;
            w.Put((ushort)count);
            for (int i = 0; i < count * 3; i++)
                w.Put(PlayerStateMessage.EncodeAngle(Eulers[i]));
        }

        public static BonePoseMessage Deserialize(NetDataReader r)
        {
            var msg = new BonePoseMessage
            {
                SenderPlayerId = r.GetInt(),
                TotalBones = r.GetUShort(),
                StartBone = r.GetUShort()
            };
            int count = r.GetUShort();
            if (count > 0 && count < 1024)
            {
                msg.Eulers = new float[count * 3];
                for (int i = 0; i < msg.Eulers.Length; i++)
                    msg.Eulers[i] = PlayerStateMessage.DecodeAngle(r.GetUShort());
            }
            return msg;
        }
    }

    public struct EnemySnapshotNet
    {
        public long WorldId; // ulong stored as long
        public byte State;
        public byte HurtState;
        public float PosX, PosY, PosZ;
        public float RotY;
        public float VelX, VelY, VelZ;
        public int AnimHash;
        public float AnimTime;
        public int HP;
        public int MaxHP;
        public bool Alive;
        public sbyte TargetPlayerId;

        public void Serialize(NetDataWriter w)
        {
            w.Put(WorldId);
            w.Put(State);
            w.Put(HurtState);
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(RotY);
            w.Put(VelX); w.Put(VelY); w.Put(VelZ);
            w.Put(AnimHash);
            w.Put(AnimTime);
            w.Put(HP);
            w.Put(MaxHP);
            w.Put(Alive);
            w.Put(TargetPlayerId);
        }

        public static EnemySnapshotNet Deserialize(NetDataReader r)
        {
            return new EnemySnapshotNet
            {
                WorldId = r.GetLong(),
                State = r.GetByte(),
                HurtState = r.GetByte(),
                PosX = r.GetFloat(), PosY = r.GetFloat(), PosZ = r.GetFloat(),
                RotY = r.GetFloat(),
                VelX = r.GetFloat(), VelY = r.GetFloat(), VelZ = r.GetFloat(),
                AnimHash = r.GetInt(),
                AnimTime = r.GetFloat(),
                HP = r.GetInt(),
                MaxHP = r.GetInt(),
                Alive = r.GetBool(),
                TargetPlayerId = r.GetSByte()
            };
        }
    }

    public struct EnemyStateMessage
    {
        public EnemySnapshotNet[] Enemies;

        public void Serialize(NetDataWriter w)
        {
            int cnt = Enemies != null ? Enemies.Length : 0;
            w.Put(cnt);
            for (int i = 0; i < cnt; i++)
                Enemies[i].Serialize(w);
        }

        public static EnemyStateMessage Deserialize(NetDataReader r)
        {
            int cnt = r.GetInt();
            if (cnt < 0 || cnt > 512) cnt = 0;
            var arr = cnt > 0 ? new EnemySnapshotNet[cnt] : System.Array.Empty<EnemySnapshotNet>();
            for (int i = 0; i < arr.Length; i++)
                arr[i] = EnemySnapshotNet.Deserialize(r);
            return new EnemyStateMessage { Enemies = arr };
        }
    }

    public struct EnemyDamageMessage
    {
        public int AttackerPlayerId;
        public int TargetPlayerId;
        public long EnemyWorldId;
        public float Damage;
        public bool IsStagger;
        /// <summary>When true, host applies EnemyController.TakeDamage(fire,crit,hurt,noSneak).</summary>
        public bool NativeTakeDamage;
        public float FireChance;
        public float CriticalChance;
        public float HurtChance;
        public bool NoSneak;

        public void Serialize(NetDataWriter w)
        {
            w.Put(AttackerPlayerId);
            w.Put(TargetPlayerId);
            w.Put(EnemyWorldId);
            w.Put(Damage);
            w.Put(IsStagger);
            w.Put(NativeTakeDamage);
            w.Put(FireChance);
            w.Put(CriticalChance);
            w.Put(HurtChance);
            w.Put(NoSneak);
        }

        public static EnemyDamageMessage Deserialize(NetDataReader r)
        {
            return new EnemyDamageMessage
            {
                AttackerPlayerId = r.GetInt(),
                TargetPlayerId = r.GetInt(),
                EnemyWorldId = r.GetLong(),
                Damage = r.GetFloat(),
                IsStagger = r.GetBool(),
                NativeTakeDamage = r.GetBool(),
                FireChance = r.GetFloat(),
                CriticalChance = r.GetFloat(),
                HurtChance = r.GetFloat(),
                NoSneak = r.GetBool()
            };
        }
    }

    public struct SceneHelloMessage
    {
        public int SenderPlayerId;
        public string SceneName;
        public string RoomName;

        public void Serialize(NetDataWriter w)
        {
            w.Put(SenderPlayerId);
            w.Put(SceneName ?? "");
            w.Put(RoomName ?? "");
        }

        public static SceneHelloMessage Deserialize(NetDataReader r)
        {
            return new SceneHelloMessage
            {
                SenderPlayerId = r.GetInt(),
                SceneName = r.GetString(),
                RoomName = r.GetString()
            };
        }
    }

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

    public struct DropItemSpawnMessage
    {
        public byte SenderID;
        public ushort LocalIndex;
        public ushort ItemEnum;
        public int Count;
        public float PosX;
        public float PosY;
        public float PosZ;

        public void Serialize(NetDataWriter w)
        {
            w.Put(SenderID);
            w.Put(LocalIndex);
            w.Put(ItemEnum);
            w.Put(Count);
            w.Put(PosX);
            w.Put(PosY);
            w.Put(PosZ);
        }

        public static DropItemSpawnMessage Deserialize(NetDataReader r)
        {
            return new DropItemSpawnMessage
            {
                SenderID = r.GetByte(),
                LocalIndex = r.GetUShort(),
                ItemEnum = r.GetUShort(),
                Count = r.GetInt(),
                PosX = r.GetFloat(),
                PosY = r.GetFloat(),
                PosZ = r.GetFloat()
            };
        }
    }

    public struct ItemPickedUpMessage
    {
        public byte SenderID;
        public ushort LocalIndex;
        public ushort ItemEnum;
        public int Count;
        public bool GrantToReceiver;

        public void Serialize(NetDataWriter w)
        {
            w.Put(SenderID);
            w.Put(LocalIndex);
            w.Put(ItemEnum);
            w.Put(Count);
            w.Put(GrantToReceiver);
        }

        public static ItemPickedUpMessage Deserialize(NetDataReader r)
        {
            return new ItemPickedUpMessage
            {
                SenderID = r.GetByte(),
                LocalIndex = r.GetUShort(),
                ItemEnum = r.GetUShort(),
                Count = r.GetInt(),
                GrantToReceiver = r.GetBool()
            };
        }
    }

    public struct FriendlyFireMessage
    {
        public int TargetPlayerId;
        public int AttackerPlayerId;
        public float Damage;
        public float HitPosX;
        public float HitPosY;
        public float HitPosZ;

        public void Serialize(NetDataWriter w)
        {
            w.Put(TargetPlayerId);
            w.Put(AttackerPlayerId);
            w.Put(Damage);
            w.Put(HitPosX);
            w.Put(HitPosY);
            w.Put(HitPosZ);
        }

        public static FriendlyFireMessage Deserialize(NetDataReader r)
        {
            return new FriendlyFireMessage
            {
                TargetPlayerId = r.GetInt(),
                AttackerPlayerId = r.GetInt(),
                Damage = r.GetFloat(),
                HitPosX = r.GetFloat(),
                HitPosY = r.GetFloat(),
                HitPosZ = r.GetFloat()
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
                Float0 = r.GetFloat()
            };
        }
    }

    public struct WorldPickupClaimMessage
    {
        public int ClaimerPlayerId;
        public long WorldId;
        public ushort ItemEnum;
        public int Count;

        public void Serialize(NetDataWriter w)
        {
            w.Put(ClaimerPlayerId);
            w.Put(WorldId);
            w.Put(ItemEnum);
            w.Put(Count);
        }

        public static WorldPickupClaimMessage Deserialize(NetDataReader r) =>
            new WorldPickupClaimMessage
            {
                ClaimerPlayerId = r.GetInt(),
                WorldId = r.GetLong(),
                ItemEnum = r.GetUShort(),
                Count = r.GetInt()
            };
    }

    public struct WorldPickupGrantMessage
    {
        public int TargetPlayerId;
        public long WorldId;
        public ushort ItemEnum;
        public int Count;

        public void Serialize(NetDataWriter w)
        {
            w.Put(TargetPlayerId);
            w.Put(WorldId);
            w.Put(ItemEnum);
            w.Put(Count);
        }

        public static WorldPickupGrantMessage Deserialize(NetDataReader r) =>
            new WorldPickupGrantMessage
            {
                TargetPlayerId = r.GetInt(),
                WorldId = r.GetLong(),
                ItemEnum = r.GetUShort(),
                Count = r.GetInt()
            };
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
            int cnt = r.GetInt();
            if (cnt > 0 && cnt < 8192)
            {
                msg.Entries = new PuzzleStateEntry[cnt];
                for (int i = 0; i < cnt; i++)
                    msg.Entries[i] = PuzzleStateEntry.Deserialize(r);
            }
            return msg;
        }
    }

    public enum BossType : byte
    {
        END_Boss = 0,
        LAB_ChimeraBoss = 1,
        MED_MynahBoss = 2,
    }

    public struct BossSnapshotNet
    {
        public short Index;
        public byte BossType;
        public float PosX, PosY, PosZ;
        public float RotY;
        public long WorldId;
        public bool Alive;
        public byte StateEnum;
        public bool Bool0, Bool1, Bool2, Bool3, Bool4;
        public int Int0, Int1;
        public float Float0, Float1, Float2;
        public int AnimHash;
        public float AnimTime;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Index);
            w.Put(BossType);
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(RotY);
            w.Put(WorldId);
            w.Put(Alive);
            w.Put(StateEnum);
            w.Put(Bool0); w.Put(Bool1); w.Put(Bool2); w.Put(Bool3); w.Put(Bool4);
            w.Put(Int0); w.Put(Int1);
            w.Put(Float0); w.Put(Float1); w.Put(Float2);
            w.Put(AnimHash);
            w.Put(AnimTime);
        }

        public static BossSnapshotNet Deserialize(NetDataReader r)
        {
            return new BossSnapshotNet
            {
                Index = r.GetShort(),
                BossType = r.GetByte(),
                PosX = r.GetFloat(), PosY = r.GetFloat(), PosZ = r.GetFloat(),
                RotY = r.GetFloat(),
                WorldId = r.GetLong(),
                Alive = r.GetBool(),
                StateEnum = r.GetByte(),
                Bool0 = r.GetBool(), Bool1 = r.GetBool(), Bool2 = r.GetBool(), Bool3 = r.GetBool(), Bool4 = r.GetBool(),
                Int0 = r.GetInt(), Int1 = r.GetInt(),
                Float0 = r.GetFloat(), Float1 = r.GetFloat(), Float2 = r.GetFloat(),
                AnimHash = r.GetInt(),
                AnimTime = r.GetFloat()
            };
        }
    }

    public struct BossStateMessage
    {
        public BossSnapshotNet[] Bosses;

        public void Serialize(NetDataWriter w)
        {
            int cnt = Bosses != null ? Bosses.Length : 0;
            w.Put(cnt);
            for (int i = 0; i < cnt; i++)
                Bosses[i].Serialize(w);
        }

        public static BossStateMessage Deserialize(NetDataReader r)
        {
            int cnt = r.GetInt();
            if (cnt < 0 || cnt > 64) cnt = 0;
            var arr = cnt > 0 ? new BossSnapshotNet[cnt] : System.Array.Empty<BossSnapshotNet>();
            for (int i = 0; i < arr.Length; i++)
                arr[i] = BossSnapshotNet.Deserialize(r);
            return new BossStateMessage { Bosses = arr };
        }
    }

    public struct SceneFollowMessage
    {
        public int SenderPlayerId;
        public string SceneName;
        public bool IsRequest;

        public void Serialize(NetDataWriter w)
        {
            w.Put(SenderPlayerId);
            w.Put(SceneName ?? "");
            w.Put(IsRequest);
        }

        public static SceneFollowMessage Deserialize(NetDataReader r) =>
            new SceneFollowMessage
            {
                SenderPlayerId = r.GetInt(),
                SceneName = r.GetString(),
                IsRequest = r.GetBool()
            };
    }

    public struct InteractionRequestMessage
    {
        public int SenderPlayerId;
        public long WorldId;
        public InteractionKind Kind;
        public int Int0;
        public int Int1;
        public float Float0;
        public float Float1;
        public float Float2;
        public string Text;

        public void Serialize(NetDataWriter w)
        {
            w.Put(SenderPlayerId);
            w.Put(WorldId);
            w.Put((byte)Kind);
            w.Put(Int0);
            w.Put(Int1);
            w.Put(Float0);
            w.Put(Float1);
            w.Put(Float2);
            w.Put(Text ?? "");
        }

        public static InteractionRequestMessage Deserialize(NetDataReader r) =>
            new InteractionRequestMessage
            {
                SenderPlayerId = r.GetInt(),
                WorldId = r.GetLong(),
                Kind = (InteractionKind)r.GetByte(),
                Int0 = r.GetInt(),
                Int1 = r.GetInt(),
                Float0 = r.GetFloat(),
                Float1 = r.GetFloat(),
                Float2 = r.GetFloat(),
                Text = r.GetString()
            };
    }

    public struct InteractionAckMessage
    {
        public int TargetPlayerId;
        public long WorldId;
        public InteractionKind Kind;
        public bool Ok;
        public string Reason;

        public void Serialize(NetDataWriter w)
        {
            w.Put(TargetPlayerId);
            w.Put(WorldId);
            w.Put((byte)Kind);
            w.Put(Ok);
            w.Put(Reason ?? "");
        }

        public static InteractionAckMessage Deserialize(NetDataReader r) =>
            new InteractionAckMessage
            {
                TargetPlayerId = r.GetInt(),
                WorldId = r.GetLong(),
                Kind = (InteractionKind)r.GetByte(),
                Ok = r.GetBool(),
                Reason = r.GetString()
            };
    }

    public struct StoryFlagEntry
    {
        public byte Kind; // 0 bool, 1 int, 2 float, 3 string, 4 vector
        public string Key;
        public bool BoolVal;
        public int IntVal;
        public float FloatVal;
        public string StringVal;
        public float VecY;
        public float VecZ;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Kind);
            w.Put(Key ?? "");
            w.Put(BoolVal);
            w.Put(IntVal);
            w.Put(FloatVal);
            w.Put(StringVal ?? "");
            w.Put(VecY);
            w.Put(VecZ);
        }

        public static StoryFlagEntry Deserialize(NetDataReader r) =>
            new StoryFlagEntry
            {
                Kind = r.GetByte(),
                Key = r.GetString(),
                BoolVal = r.GetBool(),
                IntVal = r.GetInt(),
                FloatVal = r.GetFloat(),
                StringVal = r.GetString(),
                VecY = r.GetFloat(),
                VecZ = r.GetFloat()
            };
    }

    public struct StoryCommitMessage
    {
        public bool FullRefresh;
        public string DialoguerXml;
        public int EndCircle;
        public int EndDeath;
        public int EndGraves;
        public int EndLeave;
        public int EndingId;
        public StoryFlagEntry[] Flags;
        public byte ActiveGameState;
        public long ActiveWorldId;
        public byte ActiveStoryCmd;

        public void Serialize(NetDataWriter w)
        {
            w.Put(FullRefresh);
            w.Put(DialoguerXml ?? "");
            w.Put(EndCircle);
            w.Put(EndDeath);
            w.Put(EndGraves);
            w.Put(EndLeave);
            w.Put(EndingId);
            int n = Flags != null ? Flags.Length : 0;
            w.Put(n);
            for (int i = 0; i < n; i++)
                Flags[i].Serialize(w);
            w.Put(ActiveGameState);
            w.Put(ActiveWorldId);
            w.Put(ActiveStoryCmd);
        }

        public static StoryCommitMessage Deserialize(NetDataReader r)
        {
            var msg = new StoryCommitMessage
            {
                FullRefresh = r.GetBool(),
                DialoguerXml = r.GetString(),
                EndCircle = r.GetInt(),
                EndDeath = r.GetInt(),
                EndGraves = r.GetInt(),
                EndLeave = r.GetInt(),
                EndingId = r.GetInt()
            };
            int n = r.GetInt();
            if (n > 0 && n < 8192)
            {
                msg.Flags = new StoryFlagEntry[n];
                for (int i = 0; i < n; i++)
                    msg.Flags[i] = StoryFlagEntry.Deserialize(r);
            }
            msg.ActiveGameState = r.GetByte();
            msg.ActiveWorldId = r.GetLong();
            msg.ActiveStoryCmd = r.GetByte();
            return msg;
        }
    }

    public struct StoryPresentationMessage
    {
        public long WorldId;
        public StoryCmd Cmd;
        public int Int0;
        public string Text;

        public void Serialize(NetDataWriter w)
        {
            w.Put(WorldId);
            w.Put((byte)Cmd);
            w.Put(Int0);
            w.Put(Text ?? "");
        }

        public static StoryPresentationMessage Deserialize(NetDataReader r) =>
            new StoryPresentationMessage
            {
                WorldId = r.GetLong(),
                Cmd = (StoryCmd)r.GetByte(),
                Int0 = r.GetInt(),
                Text = r.GetString()
            };
    }

    public struct StorageBoxItem
    {
        public ushort ItemEnum;
        public int Count;

        public void Serialize(NetDataWriter w)
        {
            w.Put(ItemEnum);
            w.Put(Count);
        }

        public static StorageBoxItem Deserialize(NetDataReader r) =>
            new StorageBoxItem { ItemEnum = r.GetUShort(), Count = r.GetInt() };
    }

    public struct StorageBoxBlobMessage
    {
        public StorageBoxItem[] Items;

        public void Serialize(NetDataWriter w)
        {
            int n = Items != null ? Items.Length : 0;
            w.Put(n);
            for (int i = 0; i < n; i++)
                Items[i].Serialize(w);
        }

        public static StorageBoxBlobMessage Deserialize(NetDataReader r)
        {
            int n = r.GetInt();
            var items = n > 0 && n < 512 ? new StorageBoxItem[n] : new StorageBoxItem[0];
            for (int i = 0; i < items.Length; i++)
                items[i] = StorageBoxItem.Deserialize(r);
            return new StorageBoxBlobMessage { Items = items };
        }
    }

    public struct PartyKeyRingMessage
    {
        public ushort[] ItemEnums;

        public void Serialize(NetDataWriter w)
        {
            int n = ItemEnums != null ? ItemEnums.Length : 0;
            w.Put(n);
            for (int i = 0; i < n; i++)
                w.Put(ItemEnums[i]);
        }

        public static PartyKeyRingMessage Deserialize(NetDataReader r)
        {
            int n = r.GetInt();
            var arr = n > 0 && n < 512 ? new ushort[n] : new ushort[0];
            for (int i = 0; i < arr.Length; i++)
                arr[i] = r.GetUShort();
            return new PartyKeyRingMessage { ItemEnums = arr };
        }
    }

    public struct DeathPolicyMessage
    {
        public int SenderPlayerId;
        public DeathKind Kind;

        public void Serialize(NetDataWriter w)
        {
            w.Put(SenderPlayerId);
            w.Put((byte)Kind);
        }

        public static DeathPolicyMessage Deserialize(NetDataReader r) =>
            new DeathPolicyMessage
            {
                SenderPlayerId = r.GetInt(),
                Kind = (DeathKind)r.GetByte()
            };
    }

    public struct FmodEmitterMessage
    {
        public long WorldId;
        public bool Play;
        public byte Kind; // 0 = StudioEventEmitter, 1 = PlayOneShot path
        public float PosX, PosY, PosZ;
        public string Path;

        public void Serialize(NetDataWriter w)
        {
            w.Put(WorldId);
            w.Put(Play);
            w.Put(Kind);
            w.Put(PosX);
            w.Put(PosY);
            w.Put(PosZ);
            w.Put(Path ?? "");
        }

        public static FmodEmitterMessage Deserialize(NetDataReader r) =>
            new FmodEmitterMessage
            {
                WorldId = r.GetLong(),
                Play = r.GetBool(),
                Kind = r.GetByte(),
                PosX = r.GetFloat(),
                PosY = r.GetFloat(),
                PosZ = r.GetFloat(),
                Path = r.GetString()
            };
    }
}
