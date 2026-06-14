using LiteNetLib.Utils;

namespace SyncRADation.Networking
{
    public enum NetMessageType : byte
    {
        Handshake = 1,
        PlayerState = 2,
        WorldSession = 3,
        EntityState = 4,
        PlayerAnimation = 5,
        PlayerFacing = 6,
        PlayerAction = 7,
        DoorState = 8,
        _Highest = 9
    }

    public struct HandshakeMessage
    {
        public int ProtocolVersion;

        public void Serialize(NetDataWriter w)
        {
            w.Put(ProtocolVersion);
        }

        public static HandshakeMessage Deserialize(NetDataReader r)
        {
            return new HandshakeMessage { ProtocolVersion = r.GetInt() };
        }
    }

    public struct WorldSessionMessage
    {
        public string SaveSlotName;
        public int WorldSeed;

        public void Serialize(NetDataWriter w)
        {
            w.Put(SaveSlotName ?? "");
            w.Put(WorldSeed);
        }

        public static WorldSessionMessage Deserialize(NetDataReader r)
        {
            return new WorldSessionMessage
            {
                SaveSlotName = r.GetString(),
                WorldSeed = r.GetInt()
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

        public void Serialize(NetDataWriter w)
        {
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
        }

        public static PlayerStateMessage Deserialize(NetDataReader r)
        {
            return new PlayerStateMessage
            {
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
        }
    }

    public struct EntitySnapshotNet
    {
        public short Index;
        public float PosX;
        public float PosY;
        public float PosZ;
        public float RotY;
        public bool Alive;
        public string EntityName;
        public string Clip;
        public short ClipFrame;
        public byte HealthPct;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Index);
            w.Put(PosX);
            w.Put(PosY);
            w.Put(PosZ);
            w.Put(RotY);
            w.Put(Alive);
            w.Put(EntityName ?? "");
            w.Put(Clip ?? "");
            w.Put(ClipFrame);
            w.Put(HealthPct);
        }

        public static EntitySnapshotNet Deserialize(NetDataReader r)
        {
            return new EntitySnapshotNet
            {
                Index = r.GetShort(),
                PosX = r.GetFloat(),
                PosY = r.GetFloat(),
                PosZ = r.GetFloat(),
                RotY = r.GetFloat(),
                Alive = r.GetBool(),
                EntityName = r.GetString(),
                Clip = r.GetString(),
                ClipFrame = r.GetShort(),
                HealthPct = r.GetByte()
            };
        }
    }

    public struct EntityStateMessage
    {
        public EntitySnapshotNet[] Entities;

        public void Serialize(NetDataWriter w)
        {
            int count = Entities != null ? Entities.Length : 0;
            w.Put(count);
            for (int i = 0; i < count; i++)
                Entities[i].Serialize(w);
        }

        public static EntityStateMessage Deserialize(NetDataReader r)
        {
            int count = r.GetInt();
            var arr = new EntitySnapshotNet[count];
            for (int i = 0; i < count; i++)
                arr[i] = EntitySnapshotNet.Deserialize(r);
            return new EntityStateMessage { Entities = arr };
        }
    }

    public struct PlayerAnimationMessage
    {
        public string StateName;

        public void Serialize(NetDataWriter w)
        {
            w.Put(StateName ?? "");
        }

        public static PlayerAnimationMessage Deserialize(NetDataReader r)
        {
            return new PlayerAnimationMessage { StateName = r.GetString() };
        }
    }

    public struct PlayerFacingMessage
    {
        public byte Facing;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Facing);
        }

        public static PlayerFacingMessage Deserialize(NetDataReader r)
        {
            return new PlayerFacingMessage { Facing = r.GetByte() };
        }
    }

    public struct PlayerActionMessage
    {
        public byte ActionType;
        public float PosX;
        public float PosY;
        public float PosZ;

        public void Serialize(NetDataWriter w)
        {
            w.Put(ActionType);
            w.Put(PosX);
            w.Put(PosY);
            w.Put(PosZ);
        }

        public static PlayerActionMessage Deserialize(NetDataReader r)
        {
            return new PlayerActionMessage
            {
                ActionType = r.GetByte(),
                PosX = r.GetFloat(),
                PosY = r.GetFloat(),
                PosZ = r.GetFloat()
            };
        }
    }

    public enum DoorType : byte
    {
        DoorwayDouble = 0,
        DoorwaySimple = 1,
        EventSlidingDoor = 2,
    }

    public struct DoorStateMessage
    {
        public DoorType Type;
        public short Index;
        public bool Open;
        public bool Locked;

        public void Serialize(NetDataWriter w)
        {
            w.Put((byte)Type);
            w.Put(Index);
            w.Put(Open);
            w.Put(Locked);
        }

        public static DoorStateMessage Deserialize(NetDataReader r)
        {
            return new DoorStateMessage
            {
                Type = (DoorType)r.GetByte(),
                Index = r.GetShort(),
                Open = r.GetBool(),
                Locked = r.GetBool()
            };
        }
    }
}
