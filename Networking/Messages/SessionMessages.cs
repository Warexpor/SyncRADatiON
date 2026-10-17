// SyncRADation wire: Session / connection / scene messages: handshake, roster, peer scenes, world relay, scene hello/diff/digest, follow, death notice.
using System;
using LiteNetLib.Utils;
using UnityEngine;

namespace SyncRADation.Networking
{
    public struct HandshakeMessage
    {
        public int ProtocolVersion;
        public int AssignedPlayerId;
        /// <summary>Build/schema fingerprint (assembly version + message/enum id lists). See NetSchema.</summary>
        public uint SchemaHash;
        public string ModVersion;
        /// <summary>Hash of the game build (Application.version + Unity version + GameAssembly.dll head/tail). See GameBuild.</summary>
        public uint GameBuildHash;
        /// <summary>Human-readable game build label for reject messages / F2 (e.g. "1.0.3 / Unity 2021.3.x").</summary>
        public string GameBuild;

        public void Serialize(NetDataWriter w)
        {
            w.Put(ProtocolVersion);
            w.Put(AssignedPlayerId);
            w.Put(SchemaHash);
            NetWire.PutString(w, ModVersion);
            w.Put(GameBuildHash);
            NetWire.PutString(w, GameBuild);
        }

        public static HandshakeMessage Deserialize(NetDataReader r)
        {
            var h = new HandshakeMessage
            {
                ProtocolVersion = r.GetInt(),
                AssignedPlayerId = r.GetInt(),
                SchemaHash = r.GetUInt(),
                ModVersion = r.GetString()
            };
            // The game-build tail is new in protocol 14: an older peer's handshake ends after ModVersion. Reading
            // past it would throw (dispatch swallows it, the peer only sees a timeout) instead of reaching the
            // protocol-version reject with a readable reason.
            if (r.AvailableBytes >= 4)
            {
                h.GameBuildHash = r.GetUInt();
                h.GameBuild = r.AvailableBytes >= 2 ? r.GetString() : "";
            }
            else
            {
                h.GameBuild = "";
            }
            return h;
        }
    }

    /// <summary>
    /// Host → all: full session id list (includes 0). Leave is an omission; clients prune proxies.
    /// HostFlags: the host's sync toggles (ModConfig.Flag*); clients use them instead of their own prefs while connected.
    /// </summary>
    public struct PlayerRosterMessage
    {
        public int[] PlayerIds;
        public byte HostFlags;

        public void Serialize(NetDataWriter w)
        {
            int n = PlayerIds != null ? PlayerIds.Length : 0;
            n = NetWire.ClampCount(n, NetWire.MaxRoster, "PlayerRoster");
            w.Put((byte)n);
            for (int i = 0; i < n; i++)
                w.Put(PlayerIds[i]);
            w.Put(HostFlags);
        }

        public static PlayerRosterMessage Deserialize(NetDataReader r)
        {
            int n = r.GetByte();
            if (n > NetWire.MaxRoster)
                throw new System.IO.InvalidDataException("PlayerRoster count " + n);
            var ids = n > 0 ? new int[n] : Array.Empty<int>();
            for (int i = 0; i < n; i++)
                ids[i] = r.GetInt();
            return new PlayerRosterMessage { PlayerIds = ids, HostFlags = r.GetByte() };
        }
    }

    /// <summary>
    /// Host → all: every ready peer's scene (the host's own included; empty while loading, the host's pending load
    /// target while it loads). Each peer derives the owner of a scene from it: the host in its scene, otherwise the
    /// lowest player id there (SceneOwners).
    /// </summary>
    public struct PeerScenesMessage
    {
        /// <summary>Player ids; Scenes[i] is the scene of PlayerIds[i] ("" = loading / unknown).</summary>
        public int[] PlayerIds;
        public string[] Scenes;

        public void Serialize(NetDataWriter w)
        {
            int n = PlayerIds != null ? PlayerIds.Length : 0;
            n = NetWire.ClampCount(n, NetWire.MaxRoster, "PeerScenes ids");
            w.Put((byte)n);
            for (int i = 0; i < n; i++)
                w.Put(PlayerIds[i]);
            int m = Scenes != null ? Scenes.Length : 0;
            m = NetWire.ClampCount(m, NetWire.MaxRoster, "PeerScenes scenes");
            w.Put((byte)m);
            for (int i = 0; i < m; i++)
                NetWire.PutString(w, Scenes[i]);
        }

        public static PeerScenesMessage Deserialize(NetDataReader r)
        {
            int n = r.GetByte();
            if (n > NetWire.MaxRoster)
                throw new System.IO.InvalidDataException("PeerScenes count " + n);
            var ids = n > 0 ? new int[n] : Array.Empty<int>();
            for (int i = 0; i < n; i++)
                ids[i] = r.GetInt();
            int m = r.GetByte();
            if (m > NetWire.MaxRoster)
                throw new System.IO.InvalidDataException("PeerScenes count " + m);
            var scenes = m > 0 ? new string[m] : Array.Empty<string>();
            for (int i = 0; i < m; i++)
                scenes[i] = r.GetString();
            return new PeerScenesMessage { PlayerIds = ids, Scenes = scenes };
        }
    }

    /// <summary>
    /// World traffic of a scene a client owns (no host there), carried through the host. Payload is one complete
    /// message (type byte first). Modes: <see cref="ToScene"/> owner → host, the host sends the payload to every peer
    /// in the owner's scene but the owner and <see cref="Target"/>; <see cref="ToPlayer"/> owner → host, the host
    /// sends it to <see cref="Target"/>; <see cref="ToOwner"/> host → owner, a follower's request (<see cref="From"/>)
    /// the owner handles as the host would.
    /// </summary>
    public struct WorldRelayMessage
    {
        public const byte ToScene = 0;
        public const byte ToPlayer = 1;
        public const byte ToOwner = 2;

        public byte Mode;
        public int From;
        public int Target;
        /// <summary>1: continuous state (Sequenced on the State channel), 0: event (ReliableOrdered).</summary>
        public byte State;
        public byte[] Payload;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Mode);
            w.Put(From);
            w.Put(Target);
            w.Put(State);
            int n = Payload != null ? Payload.Length : 0;
            n = NetWire.ClampCount(n, NetWire.MaxRelayBytes, "WorldRelay");
            w.Put(n);
            if (n > 0) w.Put(Payload, 0, n);
        }

        public static WorldRelayMessage Deserialize(NetDataReader r)
        {
            var msg = new WorldRelayMessage
            {
                Mode = r.GetByte(),
                From = r.GetInt(),
                Target = r.GetInt(),
                State = r.GetByte()
            };
            int n = NetWire.ReadCount(r, NetWire.MaxRelayBytes, "WorldRelay");
            msg.Payload = n > 0 ? new byte[n] : Array.Empty<byte>();
            if (n > 0) r.GetBytes(msg.Payload, n);
            return msg;
        }
    }

    public struct SnapshotRequestMessage
    {
        public int SenderPlayerId;

        public void Serialize(NetDataWriter w) => w.Put(SenderPlayerId);

        public static SnapshotRequestMessage Deserialize(NetDataReader r) =>
            new SnapshotRequestMessage { SenderPlayerId = r.GetInt() };
    }

    /// <summary>One WorldRegistry category: how many ids it holds and the FNV-1a64 checksum of the sorted ids (Sync/WorldChecksum).</summary>
    public struct WorldCategoryStat
    {
        public int Count;
        public ulong Sum;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Count);
            w.Put(Sum);
        }

        public static WorldCategoryStat Deserialize(NetDataReader r) =>
            new WorldCategoryStat { Count = r.GetInt(), Sum = r.GetULong() };

        internal static void PutAll(NetDataWriter w, WorldCategoryStat[] stats)
        {
            int n = stats != null ? stats.Length : 0;
            n = NetWire.ClampCount(n, NetWire.MaxWorldCategories, "WorldCategoryStat");
            w.Put((byte)n);
            for (int i = 0; i < n; i++)
                stats[i].Serialize(w);
        }

        internal static WorldCategoryStat[] GetAll(NetDataReader r)
        {
            int n = r.GetByte();
            if (n > NetWire.MaxWorldCategories)
                throw new System.IO.InvalidDataException("WorldCategoryStat count " + n);
            var arr = n > 0 ? new WorldCategoryStat[n] : Array.Empty<WorldCategoryStat>();
            for (int i = 0; i < n; i++)
                arr[i] = Deserialize(r);
            return arr;
        }
    }

    public struct SceneHelloMessage
    {
        public int SenderPlayerId;
        public string SceneName;
        public string RoomName;
        /// <summary>Sender's WorldRegistry checksum for <see cref="SceneName"/> (empty = none yet). Client → host: diff request; host → client: self-check.</summary>
        public WorldCategoryStat[] Stats;

        public void Serialize(NetDataWriter w)
        {
            w.Put(SenderPlayerId);
            NetWire.PutString(w, SceneName);
            NetWire.PutString(w, RoomName);
            WorldCategoryStat.PutAll(w, Stats);
        }

        public static SceneHelloMessage Deserialize(NetDataReader r)
        {
            return new SceneHelloMessage
            {
                SenderPlayerId = r.GetInt(),
                SceneName = r.GetString(),
                RoomName = r.GetString(),
                Stats = WorldCategoryStat.GetAll(r)
            };
        }
    }

    /// <summary>
    /// Host → one client: the host's WorldIds (sorted) for one category whose checksum differs from the client's.
    /// Chunked: <see cref="Part"/> of <see cref="Parts"/>; <see cref="HostCount"/> is the host's full id count for the
    /// category (more than the ids sent = truncated at NetWire.MaxSceneDiffTotalIds). <see cref="CategoryMask"/> lists
    /// every category of this diff so the client knows when it has all of them.
    /// </summary>
    public struct WorldDigestMessage
    {
        /// <summary>0: digest lines from the host. 1: a client's report for the host's log.</summary>
        public byte Kind;
        public int Seq;
        public byte Part;
        public byte Parts;
        public string Scene;
        public string[] Lines;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Kind);
            w.Put(Seq);
            w.Put(Part);
            w.Put(Parts);
            NetWire.PutString(w, Scene);
            int n = Lines != null ? Lines.Length : 0;
            n = NetWire.ClampCount(n, NetWire.MaxDigestLines, "WorldDigest");
            w.Put(n);
            for (int i = 0; i < n; i++)
                NetWire.PutString(w, Lines[i]);
        }

        public static WorldDigestMessage Deserialize(NetDataReader r)
        {
            var msg = new WorldDigestMessage
            {
                Kind = r.GetByte(),
                Seq = r.GetInt(),
                Part = r.GetByte(),
                Parts = r.GetByte(),
                Scene = r.GetString()
            };
            int n = NetWire.ReadCount(r, NetWire.MaxDigestLines, "WorldDigest");
            msg.Lines = n > 0 ? new string[n] : Array.Empty<string>();
            for (int i = 0; i < n; i++)
                msg.Lines[i] = r.GetString();
            return msg;
        }
    }

    public struct SceneDiffMessage
    {
        public int SenderPlayerId;
        public string SceneName;
        public byte Category;
        public byte CategoryMask;
        public byte Part;
        public byte Parts;
        public int HostCount;
        public ulong[] Ids;

        public void Serialize(NetDataWriter w)
        {
            w.Put(SenderPlayerId);
            NetWire.PutString(w, SceneName);
            w.Put(Category);
            w.Put(CategoryMask);
            w.Put(Part);
            w.Put(Parts);
            w.Put(HostCount);
            int n = Ids != null ? Ids.Length : 0;
            n = NetWire.ClampCount(n, NetWire.MaxSceneDiffIds, "SceneDiff");
            w.Put(n);
            for (int i = 0; i < n; i++)
                w.Put(Ids[i]);
        }

        public static SceneDiffMessage Deserialize(NetDataReader r)
        {
            var msg = new SceneDiffMessage
            {
                SenderPlayerId = r.GetInt(),
                SceneName = r.GetString(),
                Category = r.GetByte(),
                CategoryMask = r.GetByte(),
                Part = r.GetByte(),
                Parts = r.GetByte(),
                HostCount = r.GetInt()
            };
            int n = NetWire.ReadCount(r, NetWire.MaxSceneDiffIds, "SceneDiff");
            msg.Ids = n > 0 ? new ulong[n] : Array.Empty<ulong>();
            for (int i = 0; i < n; i++)
                msg.Ids[i] = r.GetULong();
            return msg;
        }
    }

    public struct SceneFollowMessage
    {
        public int SenderPlayerId;
        public string SceneName;
        public bool IsRequest;
        /// <summary>Host's WorldRegistry checksum for <see cref="SceneName"/> when its registry is already built for that scene (empty otherwise, and on requests).</summary>
        public WorldCategoryStat[] Stats;

        public void Serialize(NetDataWriter w)
        {
            w.Put(SenderPlayerId);
            NetWire.PutString(w, SceneName);
            w.Put(IsRequest);
            WorldCategoryStat.PutAll(w, Stats);
        }

        public static SceneFollowMessage Deserialize(NetDataReader r) =>
            new SceneFollowMessage
            {
                SenderPlayerId = r.GetInt(),
                SceneName = r.GetString(),
                IsRequest = r.GetBool(),
                Stats = WorldCategoryStat.GetAll(r)
            };
    }

    public enum DeathKind : byte
    {
        ClientDowned = 1,
        HostWipeReload = 2,
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
}
