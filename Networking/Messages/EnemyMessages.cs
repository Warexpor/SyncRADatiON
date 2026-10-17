// SyncRADation wire: Enemies and bosses.
using System;
using LiteNetLib.Utils;
using UnityEngine;

namespace SyncRADation.Networking
{
    public struct EnemySnapshotNet
    {
        public long WorldId; // ulong stored as long
        public byte State;
        public byte HurtState;
        public float PosX, PosY, PosZ;
        public float RotY;
        /// <summary>Animator floats the host's AI drives (ThirdPersonCharacter.UpdateAnimator Forward / Turn,
        /// EnemyController hit direction HitFromX / HitFromY); a halted puppet never sets them.</summary>
        public float AnimForward, AnimTurn, AnimHitX, AnimHitY;
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
            w.Put(AnimForward); w.Put(AnimTurn); w.Put(AnimHitX); w.Put(AnimHitY);
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
                AnimForward = r.GetFloat(), AnimTurn = r.GetFloat(), AnimHitX = r.GetFloat(), AnimHitY = r.GetFloat(),
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

    public enum EnemyActionKind : byte
    {
        Kill = 0,
        KillSilent = 1,
        Knockback = 2,
        GetPushed = 3,
        Burndown = 4,
        WakeUp = 5,
        Stomp = 6, // the client pressed a downed enemy's stomp prompt (the host's Critical / Fire coroutine consumes it)
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
}
