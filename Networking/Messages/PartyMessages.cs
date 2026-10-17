using LiteNetLib.Utils;

namespace SyncRADation.Networking
{
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
            NetWire.PutString(w, Room);
        }

        public static PartyRoomMessage Deserialize(NetDataReader r) =>
            new PartyRoomMessage
            {
                PlayerId = r.GetInt(),
                Room = r.GetString()
            };
    }
}
