using LiteNetLib.Utils;

namespace SyncRADation.Networking
{
    public enum PartyLifeKind : byte
    {
        Revive = 1,
        Wipe = 2,
    }

    /// <summary>
    /// Host → all: a downed player respawns (next to a living teammate) or the whole party
    /// wiped (host reloads its save; clients restore their bag from the token's snapshot).
    /// </summary>
    public struct PartyLifeMessage
    {
        public PartyLifeKind Kind;
        public int PlayerId;
        public int Hp;
        public bool HasPos;
        public float PosX, PosY, PosZ;
        public string Room;
        public int SaveSlot;
        public int SaveCounter;
        public long SaveStamp;

        public void Serialize(NetDataWriter w)
        {
            w.Put((byte)Kind);
            w.Put(PlayerId);
            w.Put(Hp);
            w.Put(HasPos);
            w.Put(PosX);
            w.Put(PosY);
            w.Put(PosZ);
            w.Put(Room ?? "");
            w.Put(SaveSlot);
            w.Put(SaveCounter);
            w.Put(SaveStamp);
        }

        public static PartyLifeMessage Deserialize(NetDataReader r) =>
            new PartyLifeMessage
            {
                Kind = (PartyLifeKind)r.GetByte(),
                PlayerId = r.GetInt(),
                Hp = r.GetInt(),
                HasPos = r.GetBool(),
                PosX = r.GetFloat(),
                PosY = r.GetFloat(),
                PosZ = r.GetFloat(),
                Room = r.GetString(),
                SaveSlot = r.GetInt(),
                SaveCounter = r.GetInt(),
                SaveStamp = r.GetLong()
            };
    }

    /// <summary>
    /// Host → clients: the host saved (Flags bit0 clear) or, unicast during the join handshake,
    /// the save token the session started from (Flags bit0 set: restore a matching bag snapshot).
    /// </summary>
    public struct PartySaveMessage
    {
        public const byte FlagJoin = 1;

        public int Slot;
        public int Counter;
        public long Stamp;
        public byte Flags;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Slot);
            w.Put(Counter);
            w.Put(Stamp);
            w.Put(Flags);
        }

        public static PartySaveMessage Deserialize(NetDataReader r) =>
            new PartySaveMessage
            {
                Slot = r.GetInt(),
                Counter = r.GetInt(),
                Stamp = r.GetLong(),
                Flags = r.GetByte()
            };
    }

    /// <summary>Client → host: current Room.roomName (used to drop a revived teammate into the right room).</summary>
    public struct PartyRoomMessage
    {
        public int PlayerId;
        public string Room;

        public void Serialize(NetDataWriter w)
        {
            w.Put(PlayerId);
            w.Put(Room ?? "");
        }

        public static PartyRoomMessage Deserialize(NetDataReader r) =>
            new PartyRoomMessage
            {
                PlayerId = r.GetInt(),
                Room = r.GetString()
            };
    }
}
