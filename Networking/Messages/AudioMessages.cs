// SyncRADation wire: World FMOD emitters.
using System;
using LiteNetLib.Utils;
using UnityEngine;

namespace SyncRADation.Networking
{
    public struct FmodEmitterMessage
    {
        public long WorldId;
        public bool Play;
        public byte Kind; // 0 = StudioEventEmitter, 1 = PlayOneShot path
        public float PosX, PosY, PosZ;
        public string Path;
        /// <summary>Index of the StudioEventEmitter among the GameObject's StudioEventEmitters (serialized order, identical on every peer).</summary>
        public byte Comp;

        public void Serialize(NetDataWriter w)
        {
            w.Put(WorldId);
            w.Put(Play);
            w.Put(Kind);
            w.Put(PosX);
            w.Put(PosY);
            w.Put(PosZ);
            NetWire.PutString(w, Path);
            w.Put(Comp);
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
                Path = r.GetString(),
                Comp = r.GetByte()
            };
    }

    /// <summary>Client → host: world emitter Play/Stop the client triggered itself. The host plays it and relays it as FmodEmitter to everyone but the sender.</summary>
    public struct FmodEmitterRequestMessage
    {
        public long WorldId;
        public bool Play;
        public byte Comp;

        public void Serialize(NetDataWriter w)
        {
            w.Put(WorldId);
            w.Put(Play);
            w.Put(Comp);
        }

        public static FmodEmitterRequestMessage Deserialize(NetDataReader r) =>
            new FmodEmitterRequestMessage
            {
                WorldId = r.GetLong(),
                Play = r.GetBool(),
                Comp = r.GetByte()
            };
    }
}
