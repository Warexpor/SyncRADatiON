// SyncRADation — all protocol structs (messages, enums, snapshots) + serialization
using System;
using LiteNetLib.Utils;
using UnityEngine;

namespace SyncRADation.Networking
{
    // Protocol v10 wire types. Host relays gameplay; host owns world/story.
// Additive history: v8 base messages; v9 PuzzleType 64–72 + Boss/Story/FMOD fields;
// v10 PuzzleType 73–76 + client-emit door/storage/keygrid/photo.
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
        EnemySpawn = 35,
        PartyLife = 40,
        PartySave = 41,
        PartyRoom = 42,
        /// <summary>Host → client: world pickup claim refused (another player got it first).</summary>
        WorldPickupDeny = 60,
        /// <summary>Peer → all (host relays): reliable avatar one-shot triggers (Fire/Hurt/Die/...).</summary>
        AvatarOneShot = 61,
        /// <summary>Client → host: boss hitbox damage / Falke stab request.</summary>
        BossHit = 62,
        /// <summary>Client → host: authoritative enemy side effect (stomp Kill, push, burn, wake).</summary>
        EnemyAction = 63,
        // 67-69: Puzzles / Doors / Audio domain.
        /// <summary>Client → host: a world StudioEventEmitter the client triggered locally (host relays to the other clients).</summary>
        FmodEmitterRequest = 67,
        // 64-66, 68-72: reserved for other domains. 73-75: pickups / inventory / combat.
        /// <summary>Host → all: a departed peer's floor drop moved into the host key namespace.</summary>
        DropRekey = 73,
        /// <summary>Host → one client: the host's WorldIds of the categories whose registry checksum differs (chunked).</summary>
        SceneDiff = 74,
        _Highest = 74
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
        InspectFlag = 21,
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
        // 20-39: Story domain. Requests reuse InteractionKind.InspectFlag with Int0 = 100 + StoryCmd.
        GoToPenny = 20,
        PartyCheat = 21,
        EndDelta = 22,
        EndGraves = 23,
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
            n = NetWire.ClampCount(n, NetWire.MaxRoster, "PlayerRoster");
            w.Put((byte)n);
            for (int i = 0; i < n; i++)
                w.Put(PlayerIds[i]);
        }

        public static PlayerRosterMessage Deserialize(NetDataReader r)
        {
            int n = r.GetByte();
            if (n > NetWire.MaxRoster)
                throw new System.IO.InvalidDataException("PlayerRoster count " + n);
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
            n = NetWire.ClampCount(n, NetWire.MaxPickupEntries, "WorldPickupState");
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
            int n = NetWire.ReadCount(r, NetWire.MaxPickupEntries, "WorldPickupState");
            if (n > 0)
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
            return new HandshakeMessage
            {
                ProtocolVersion = r.GetInt(),
                AssignedPlayerId = r.GetInt(),
                SchemaHash = r.GetUInt(),
                ModVersion = r.GetString(),
                GameBuildHash = r.GetUInt(),
                GameBuild = r.GetString()
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
            bc = NetWire.ClampCount(bc, NetWire.MaxBones, "PlayerState bones");
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
            int bc = NetWire.ReadCount(r, NetWire.MaxBones, "PlayerState bones");
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

    /// <summary>Sequenced bone chunk. LiteNetLib sequenced MTU is 1020 — never pack the full tree into PlayerState.</summary>
    public struct BonePoseMessage
    {
        public int SenderPlayerId;
        public ushort TotalBones;
        public ushort StartBone;
        public float[] Eulers; // Count * 3, Count = Eulers.Length / 3

        /// <summary>Largest bone count Deserialize accepts in one chunk.</summary>
        public const int MaxChunkBones = 1023;

        public int Count => Eulers != null ? Eulers.Length / 3 : 0;

        public void Serialize(NetDataWriter w)
        {
            w.Put(SenderPlayerId);
            w.Put(TotalBones);
            w.Put(StartBone);
            // Reader accepts 1..MaxChunkBones; a longer chunk would leave count*3 ushorts unread and desync the stream.
            int count = Count;
            if (count > MaxChunkBones)
            {
                NetWire.WarnOnce("bonepose", "BonePose chunk of " + count + " bones clamped to " + MaxChunkBones);
                count = MaxChunkBones;
            }
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
            if (count > 0 && count <= MaxChunkBones)
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
            cnt = NetWire.ClampCount(cnt, NetWire.MaxEnemies, "EnemyState");
            w.Put(cnt);
            for (int i = 0; i < cnt; i++)
                Enemies[i].Serialize(w);
        }

        public static EnemyStateMessage Deserialize(NetDataReader r)
        {
            int cnt = NetWire.ReadCount(r, NetWire.MaxEnemies, "EnemyState");
            var arr = cnt > 0 ? new EnemySnapshotNet[cnt] : System.Array.Empty<EnemySnapshotNet>();
            for (int i = 0; i < arr.Length; i++)
                arr[i] = EnemySnapshotNet.Deserialize(r);
            return new EnemyStateMessage { Enemies = arr };
        }
    }

    /// <summary>Seq 0 = client request (host assigns). Seq &gt; 0 = host-authoritative spawn.</summary>
    public struct EnemySpawnMessage
    {
        public int Seq;
        public string TypeKey;
        public float PosX;
        public float PosY;
        public float PosZ;
        public float RotY;

        public void Serialize(NetDataWriter w)
        {
            w.Put(Seq);
            NetWire.PutString(w, TypeKey);
            w.Put(PosX);
            w.Put(PosY);
            w.Put(PosZ);
            w.Put(RotY);
        }

        public static EnemySpawnMessage Deserialize(NetDataReader r) =>
            new EnemySpawnMessage
            {
                Seq = r.GetInt(),
                TypeKey = r.GetString(),
                PosX = r.GetFloat(),
                PosY = r.GetFloat(),
                PosZ = r.GetFloat(),
                RotY = r.GetFloat()
            };
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
        /// <summary>Player who took the drop (host-stamped for client-originated copies).</summary>
        public byte ClaimerPlayerId;

        public void Serialize(NetDataWriter w)
        {
            w.Put(SenderID);
            w.Put(LocalIndex);
            w.Put(ItemEnum);
            w.Put(Count);
            w.Put(GrantToReceiver);
            w.Put(ClaimerPlayerId);
        }

        public static ItemPickedUpMessage Deserialize(NetDataReader r)
        {
            return new ItemPickedUpMessage
            {
                SenderID = r.GetByte(),
                LocalIndex = r.GetUShort(),
                ItemEnum = r.GetUShort(),
                Count = r.GetInt(),
                GrantToReceiver = r.GetBool(),
                ClaimerPlayerId = r.GetByte()
            };
        }
    }

    /// <summary>Host → all: drop key (OldOwner,OldIndex) is now (NewOwner,NewIndex).</summary>
    public struct DropRekeyMessage
    {
        public byte OldOwner;
        public ushort OldIndex;
        public byte NewOwner;
        public ushort NewIndex;

        public void Serialize(NetDataWriter w)
        {
            w.Put(OldOwner);
            w.Put(OldIndex);
            w.Put(NewOwner);
            w.Put(NewIndex);
        }

        public static DropRekeyMessage Deserialize(NetDataReader r) =>
            new DropRekeyMessage
            {
                OldOwner = r.GetByte(),
                OldIndex = r.GetUShort(),
                NewOwner = r.GetByte(),
                NewIndex = r.GetUShort()
            };
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

    public enum EnemyActionKind : byte
    {
        Kill = 0,
        KillSilent = 1,
        Knockback = 2,
        GetPushed = 3,
        Burndown = 4,
        WakeUp = 5,
    }

    /// <summary>Client → host: native EnemyController side effect a puppeted client cannot apply itself.</summary>
    public struct EnemyActionMessage
    {
        public int SenderPlayerId;
        public long EnemyWorldId;
        public EnemyActionKind Action;

        public void Serialize(NetDataWriter w)
        {
            w.Put(SenderPlayerId);
            w.Put(EnemyWorldId);
            w.Put((byte)Action);
        }

        public static EnemyActionMessage Deserialize(NetDataReader r) =>
            new EnemyActionMessage
            {
                SenderPlayerId = r.GetInt(),
                EnemyWorldId = r.GetLong(),
                Action = (EnemyActionKind)r.GetByte()
            };
    }

    /// <summary>Host → claimer: pickup was already claimed — roll back any native bag add.</summary>
    public struct WorldPickupDenyMessage
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

        public static WorldPickupDenyMessage Deserialize(NetDataReader r) =>
            new WorldPickupDenyMessage
            {
                TargetPlayerId = r.GetInt(),
                WorldId = r.GetLong(),
                ItemEnum = r.GetUShort(),
                Count = r.GetInt()
            };
    }

    /// <summary>Reliable one-shot avatar triggers so Fire/Hurt/Die never ride the lossy sequenced pose.</summary>
    public struct AvatarOneShotMessage
    {
        public int SenderPlayerId;
        public AnimTriggers Triggers;

        public void Serialize(NetDataWriter w)
        {
            w.Put(SenderPlayerId);
            w.Put((ushort)Triggers);
        }

        public static AvatarOneShotMessage Deserialize(NetDataReader r) =>
            new AvatarOneShotMessage
            {
                SenderPlayerId = r.GetInt(),
                Triggers = (AnimTriggers)r.GetUShort()
            };
    }

    public enum BossHitKind : byte
    {
        Damage = 0,
        Stab = 1,
        /// <summary>Falke PickupSpears[Amount] taken (client request; host relays to all as presentation).</summary>
        TakeSpear = 2,
        /// <summary>Host → clients: LAB Chimera Isa rifle fired (LateUpdate presentation on clients).</summary>
        ChimeraShot = 3,
    }

    /// <summary>Client → host: boss HP delta (Hitbox.HP is mutated directly by PlayerAttack), Falke Stab/takeSpear. Host → clients: spear taken, Chimera shot.</summary>
    public struct BossHitMessage
    {
        public int SenderPlayerId;
        public long WorldId;
        public BossHitKind Kind;
        public int Amount;

        public void Serialize(NetDataWriter w)
        {
            w.Put(SenderPlayerId);
            w.Put(WorldId);
            w.Put((byte)Kind);
            w.Put(Amount);
        }

        public static BossHitMessage Deserialize(NetDataReader r) =>
            new BossHitMessage
            {
                SenderPlayerId = r.GetInt(),
                WorldId = r.GetLong(),
                Kind = (BossHitKind)r.GetByte(),
                Amount = r.GetInt()
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
        public int Int0, Int1, Int2;
        public float Float0, Float1, Float2;
        public int AnimHash;
        public float AnimTime;
        /// <summary>v9: END_Boss.hitbox.HP (0 if unused).</summary>
        public int Hp;
        /// <summary>v9: END_Boss.corrupt.</summary>
        public bool Corrupt;

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
            w.Put(Int0); w.Put(Int1); w.Put(Int2);
            w.Put(Float0); w.Put(Float1); w.Put(Float2);
            w.Put(AnimHash);
            w.Put(AnimTime);
            w.Put(Hp);
            w.Put(Corrupt);
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
                Int0 = r.GetInt(), Int1 = r.GetInt(), Int2 = r.GetInt(),
                Float0 = r.GetFloat(), Float1 = r.GetFloat(), Float2 = r.GetFloat(),
                AnimHash = r.GetInt(),
                AnimTime = r.GetFloat(),
                Hp = r.GetInt(),
                Corrupt = r.GetBool()
            };
        }
    }

    public struct BossStateMessage
    {
        public BossSnapshotNet[] Bosses;

        public void Serialize(NetDataWriter w)
        {
            int cnt = Bosses != null ? Bosses.Length : 0;
            cnt = NetWire.ClampCount(cnt, NetWire.MaxBosses, "BossState");
            w.Put(cnt);
            for (int i = 0; i < cnt; i++)
                Bosses[i].Serialize(w);
        }

        public static BossStateMessage Deserialize(NetDataReader r)
        {
            int cnt = NetWire.ReadCount(r, NetWire.MaxBosses, "BossState");
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
            NetWire.PutString(w, Text);
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
            NetWire.PutString(w, Reason);
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
            NetWire.PutString(w, Key);
            w.Put(BoolVal);
            w.Put(IntVal);
            w.Put(FloatVal);
            NetWire.PutString(w, StringVal);
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
        // v9 playstyle (END_Manager CalculatePlaystyle inputs)
        public int EndNpc;
        public float EndHealedTime;
        public int EndHealedSegments;
        public float EndMemoryTime;
        public int EndDoors;
        public StoryFlagEntry[] Flags;
        public byte ActiveGameState;
        public long ActiveWorldId;
        public byte ActiveStoryCmd;

        public void Serialize(NetDataWriter w)
        {
            w.Put(FullRefresh);
            NetWire.PutLongString(w, DialoguerXml);
            w.Put(EndCircle);
            w.Put(EndDeath);
            w.Put(EndGraves);
            w.Put(EndLeave);
            w.Put(EndingId);
            w.Put(EndNpc);
            w.Put(EndHealedTime);
            w.Put(EndHealedSegments);
            w.Put(EndMemoryTime);
            w.Put(EndDoors);
            int n = Flags != null ? Flags.Length : 0;
            n = NetWire.ClampCount(n, NetWire.MaxStoryFlags, "StoryCommit flags");
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
                DialoguerXml = NetWire.GetLongString(r),
                EndCircle = r.GetInt(),
                EndDeath = r.GetInt(),
                EndGraves = r.GetInt(),
                EndLeave = r.GetInt(),
                EndingId = r.GetInt(),
                EndNpc = r.GetInt(),
                EndHealedTime = r.GetFloat(),
                EndHealedSegments = r.GetInt(),
                EndMemoryTime = r.GetFloat(),
                EndDoors = r.GetInt()
            };
            int n = NetWire.ReadCount(r, NetWire.MaxStoryFlags, "StoryCommit flags");
            if (n > 0)
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
            NetWire.PutString(w, Text);
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
            n = NetWire.ClampCount(n, NetWire.MaxStorageItems, "StorageBoxBlob");
            w.Put(n);
            for (int i = 0; i < n; i++)
                Items[i].Serialize(w);
        }

        public static StorageBoxBlobMessage Deserialize(NetDataReader r)
        {
            int n = NetWire.ReadCount(r, NetWire.MaxStorageItems, "StorageBoxBlob");
            var items = n > 0 ? new StorageBoxItem[n] : new StorageBoxItem[0];
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
            n = NetWire.ClampCount(n, NetWire.MaxKeyRing, "PartyKeyRing");
            w.Put(n);
            for (int i = 0; i < n; i++)
                w.Put(ItemEnums[i]);
        }

        public static PartyKeyRingMessage Deserialize(NetDataReader r)
        {
            int n = NetWire.ReadCount(r, NetWire.MaxKeyRing, "PartyKeyRing");
            var arr = n > 0 ? new ushort[n] : new ushort[0];
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
