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
        _Highest = 8
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

    public struct PlayerStateMessage
    {
        public float PosX;
        public float PosY;
        public float PosZ;
        public float RotY;
        public float RootY;
        public float VelX;
        public float VelZ;
        public byte CharState;
        public byte Facing;
        public bool Aiming;
        public bool Shooting;
        public bool Running;

        public void Serialize(NetDataWriter w)
        {
            w.Put(PosX);
            w.Put(PosY);
            w.Put(PosZ);
            w.Put(RotY);
            w.Put(RootY);
            w.Put(VelX);
            w.Put(VelZ);
            w.Put(CharState);
            w.Put(Facing);
            w.Put(Aiming);
            w.Put(Shooting);
            w.Put(Running);
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
                CharState = r.GetByte(),
                Facing = r.GetByte(),
                Aiming = r.GetBool(),
                Shooting = r.GetBool(),
                Running = r.GetBool()
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
}
