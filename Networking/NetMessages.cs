// SyncRADation � all protocol structs (messages, enums, snapshots) + serialization
using LiteNetLib.Utils;
using UnityEngine;

namespace SyncRADation.Networking
{
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
        SceneSync = 14,
        PuzzleState = 15,
        BossState = 17,
        _Highest = 18
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
        public float RotY;
        public float RootY;
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
        public float[] BoneRotations;

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
                Climbing = r.GetBool()
            };
            int bc = r.GetInt();
            if (bc > 0)
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

    public struct EnemySnapshotNet
    {
        public short Index;
        public byte State;
        public byte HurtState;
        public float PosX, PosY, PosZ;
        public float RotY;
        public float VelX, VelY, VelZ;
        public int AnimHash;
        public float AnimTime;
        public int HP;
        public int MaxHP;
        public int HostInstanceID;
        public bool Alive;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Index);
            w.Put(State);
            w.Put(HurtState);
            w.Put(PosX); w.Put(PosY); w.Put(PosZ);
            w.Put(RotY);
            w.Put(VelX); w.Put(VelY); w.Put(VelZ);
            w.Put(AnimHash);
            w.Put(AnimTime);
            w.Put(HP);
            w.Put(MaxHP);
            w.Put(HostInstanceID);
            w.Put(Alive);
        }

        public static EnemySnapshotNet Deserialize(NetDataReader r)
        {
            return new EnemySnapshotNet
            {
                Index = r.GetShort(),
                State = r.GetByte(),
                HurtState = r.GetByte(),
                PosX = r.GetFloat(), PosY = r.GetFloat(), PosZ = r.GetFloat(),
                RotY = r.GetFloat(),
                VelX = r.GetFloat(), VelY = r.GetFloat(), VelZ = r.GetFloat(),
                AnimHash = r.GetInt(),
                AnimTime = r.GetFloat(),
                HP = r.GetInt(),
                MaxHP = r.GetInt(),
                HostInstanceID = r.GetInt(),
                Alive = r.GetBool()
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
            var arr = new EnemySnapshotNet[cnt];
            for (int i = 0; i < cnt; i++)
                arr[i] = EnemySnapshotNet.Deserialize(r);
            return new EnemyStateMessage { Enemies = arr };
        }
    }

    public struct EnemyDamageMessage
    {
        public int AttackerPlayerId;
        public int TargetPlayerId;
        public int HostEnemyInstanceID;
        public float Damage;
        public bool IsStagger;

        public void Serialize(NetDataWriter w)
        {
            w.Put(AttackerPlayerId);
            w.Put(TargetPlayerId);
            w.Put(HostEnemyInstanceID);
            w.Put(Damage);
            w.Put(IsStagger);
        }

        public static EnemyDamageMessage Deserialize(NetDataReader r)
        {
            return new EnemyDamageMessage
            {
                AttackerPlayerId = r.GetInt(),
                TargetPlayerId = r.GetInt(),
                HostEnemyInstanceID = r.GetInt(),
                Damage = r.GetFloat(),
                IsStagger = r.GetBool()
            };
        }
    }

    public struct SceneSyncMessage
    {
        public int SenderPlayerId;
        public string SceneName;
        public string SaveSlotName;

        public void Serialize(NetDataWriter w)
        {
            w.Put(SenderPlayerId);
            w.Put(SceneName ?? "");
            w.Put(SaveSlotName ?? "");
        }

        public static SceneSyncMessage Deserialize(NetDataReader r)
        {
            return new SceneSyncMessage
            {
                SenderPlayerId = r.GetInt(),
                SceneName = r.GetString(),
                SaveSlotName = r.GetString()
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
        public short Index;
        public bool Open;
        public bool Locked;
        public bool InProgress;  // ConnectedDoors
        public bool Forwards;    // ConnectedDoors
        public bool Moving;      // EventSlidingDoor

        public void Serialize(NetDataWriter w)
        {
            w.Put(SenderPlayerId);
            w.Put((byte)Type);
            w.Put(Index);
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
                Index = r.GetShort(),
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
    }

    public struct PuzzleStateEntry
    {
        public PuzzleType Type;
        public short Index;
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
            w.Put(Index);
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
                Index = r.GetShort(),
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
            if (cnt > 0)
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
        public int HostInstanceID;
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
            w.Put(HostInstanceID);
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
                HostInstanceID = r.GetInt(),
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
            var arr = new BossSnapshotNet[cnt];
            for (int i = 0; i < cnt; i++)
                arr[i] = BossSnapshotNet.Deserialize(r);
            return new BossStateMessage { Bosses = arr };
        }
    }
}
