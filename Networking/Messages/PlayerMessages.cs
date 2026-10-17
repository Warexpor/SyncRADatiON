// SyncRADation wire: Avatar pose, bones, one-shot cues, vitals, friendly fire.
using System;
using LiteNetLib.Utils;
using UnityEngine;

namespace SyncRADation.Networking
{
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

    /// <summary>PlayerState state bits: exactly what the proxy's sounds, laser and outfit read.</summary>
    [System.Flags]
    public enum PoseFlags : byte
    {
        None = 0,
        /// <summary>Aiming (AimingTime past half, or PlayerState.aiming): laser on, draw cue on the rising edge.</summary>
        Aiming = 1 << 0,
        /// <summary>Run gait: footstep loudness and the FMOD run parameter.</summary>
        Running = 1 << 1,
        /// <summary>Trigger pressed on an empty magazine while able to fire: dry-fire click on the rising edge.</summary>
        EmptyClick = 1 << 2,
        /// <summary>A footstep landed since the previous pose (base-layer loop crossed its half or wrapped).</summary>
        Step = 1 << 3,
        /// <summary>Traversing (ladder) or crawling: ladder cue on the rising edge.</summary>
        Climbing = 1 << 4,
        /// <summary>CharacterModelType.wearHat.</summary>
        WearHat = 1 << 5,
        /// <summary>HipsX/Y/Z carry the sender's humanoid hips localPosition.</summary>
        HasHips = 1 << 6,
        /// <summary>PlayerState.gameState is cutscene: scripted motion (the hole jump), so the proxy height is not floor-locked.</summary>
        Scripted = 1 << 7,
    }

    /// <summary>Edge events sent reliably (AvatarOneShot), never on the lossy sequenced pose.</summary>
    [System.Flags]
    public enum AvatarCue : byte
    {
        None = 0,
        /// <summary>A live round left the equipped weapon (magAmmo dropped): shot sound, muzzle FX, impact.</summary>
        Fire = 1 << 0,
        /// <summary>Reload started (or the magazine refilled): reload sound and FX.</summary>
        Reload = 1 << 1,
        /// <summary>Hit taken (Hurt pulse) or became Injured: hurt sound.</summary>
        Hurt = 1 << 2,
    }

    /// <summary>
    /// Sequenced ~30 Hz avatar pose (protocol 18). Root motion + the sender's full bone rotations + humanoid hips
    /// position: the proxy has no Animator, these fields are the whole visible body. 57 bytes before the bones
    /// (type byte included), 6 per bone.
    /// </summary>
    public struct PlayerStateMessage
    {
        public int SenderPlayerId;
        public float PosX;
        public float PosY;
        public float PosZ;
        /// <summary>Planar velocity (SIGNALIS walks XY; Z is height and never extrapolated).</summary>
        public float VelX;
        public float VelY;
        /// <summary>Facing-pivot world rotation x/y/z; w is implied (canonical w >= 0, see SetFacingWorld).</summary>
        public float FacingX;
        public float FacingY;
        public float FacingZ;
        public WeaponType Weapon;
        public PoseFlags Flags;
        /// <summary>CharacterModelType.ElsterType (outfit).</summary>
        public byte ModelState;
        /// <summary>|Animator Forward| quantized to 0..255 (footstep loudness).</summary>
        public byte Forward;
        /// <summary>Humanoid hips localPosition (valid with PoseFlags.HasHips): bone sync is rotation-only.</summary>
        public float HipsX;
        public float HipsY;
        public float HipsZ;
        /// <summary>Local euler angles of every armature bone (BoneSyncManager order), 3 per bone.</summary>
        public float[] BoneRotations;

        public void SetFacingWorld(Quaternion q)
        {
            if (q.w < 0f)
                q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
            FacingX = q.x;
            FacingY = q.y;
            FacingZ = q.z;
        }

        public Quaternion GetFacingWorld()
        {
            float xyz = FacingX * FacingX + FacingY * FacingY + FacingZ * FacingZ;
            if (float.IsNaN(xyz) || float.IsInfinity(xyz))
                return Quaternion.identity;
            if (xyz > 1f)
            {
                float inv = 1f / Mathf.Sqrt(xyz);
                return new Quaternion(FacingX * inv, FacingY * inv, FacingZ * inv, 0f);
            }
            return new Quaternion(FacingX, FacingY, FacingZ, Mathf.Sqrt(1f - xyz));
        }

        public void SetForward(float animatorForward)
        {
            float f = animatorForward < 0f ? -animatorForward : animatorForward;
            Forward = (byte)(Mathf.Clamp(f, 0f, 1f) * 255f + 0.5f);
        }

        public float ForwardAmount => Forward / 255f;

        public void Serialize(NetDataWriter w)
        {
            w.Put(SenderPlayerId);
            w.Put(PosX);
            w.Put(PosY);
            w.Put(PosZ);
            w.Put(VelX);
            w.Put(VelY);
            w.Put(FacingX);
            w.Put(FacingY);
            w.Put(FacingZ);
            w.Put((byte)Weapon);
            w.Put((byte)Flags);
            w.Put(ModelState);
            w.Put(Forward);
            w.Put(HipsX);
            w.Put(HipsY);
            w.Put(HipsZ);
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
                VelX = r.GetFloat(),
                VelY = r.GetFloat(),
                FacingX = r.GetFloat(),
                FacingY = r.GetFloat(),
                FacingZ = r.GetFloat(),
                Weapon = (WeaponType)r.GetByte(),
                Flags = (PoseFlags)r.GetByte(),
                ModelState = r.GetByte(),
                Forward = r.GetByte(),
                HipsX = r.GetFloat(),
                HipsY = r.GetFloat(),
                HipsZ = r.GetFloat()
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

        public void Serialize(NetDataWriter w) => Write(w, SenderPlayerId, TotalBones, StartBone, Eulers, 0, Count);

        /// <summary>
        /// Writes bones [startBone, startBone + count) straight from the full euler buffer (offset in bones), so the
        /// sender's chunking needs no per-chunk array.
        /// </summary>
        public static void Write(NetDataWriter w, int senderPlayerId, ushort totalBones, ushort startBone,
            float[] eulers, int offsetBones, int count)
        {
            w.Put(senderPlayerId);
            w.Put(totalBones);
            w.Put(startBone);
            // Reader accepts 1..MaxChunkBones; a longer chunk would leave count*3 ushorts unread and desync the stream.
            if (count > MaxChunkBones)
            {
                NetWire.WarnOnce("bonepose", "BonePose chunk of " + count + " bones clamped to " + MaxChunkBones);
                count = MaxChunkBones;
            }
            w.Put((ushort)count);
            int from = offsetBones * 3;
            for (int i = 0; i < count * 3; i++)
                w.Put(PlayerStateMessage.EncodeAngle(eulers[from + i]));
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

    /// <summary>Reliable avatar edge cues (shot / reload / hurt) so they never ride the lossy sequenced pose.</summary>
    public struct AvatarOneShotMessage
    {
        public int SenderPlayerId;
        public AvatarCue Cues;

        public void Serialize(NetDataWriter w)
        {
            w.Put(SenderPlayerId);
            w.Put((byte)Cues);
        }

        public static AvatarOneShotMessage Deserialize(NetDataReader r) =>
            new AvatarOneShotMessage
            {
                SenderPlayerId = r.GetInt(),
                Cues = (AvatarCue)r.GetByte()
            };
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
}
