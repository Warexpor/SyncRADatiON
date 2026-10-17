// SyncRADation wire: Story: interactions, commits, presentation.
using System;
using LiteNetLib.Utils;
using UnityEngine;

namespace SyncRADation.Networking
{
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
        CutsceneSkip = 8, // wire value only since 0.5.65: skips are per player
        DialogueContinue = 9,
        DialogueEnd = 10,
        StoragePut = 11,
        StorageTake = 12,
        Gunshot = 13,
        MultiCondition = 14,
        SceneFollowRequest = 15,
        UseItemMulti = 16, // wire value only since 0.5.66: ready() is not an item use (each part's UseItem is)
        CutsceneProceed = 17, // wire value only since 0.5.65: proceeds are per player
        BookOpen = 18,
        BookMemory = 19,
        DroppedPickup = 20,
        InspectFlag = 21,
        CutsceneDone = 22, // a client's copy of a shared cutscene is over (played out or skipped)
    }

    public enum StoryCmd : byte
    {
        None = 0,
        DialogueStart = 1,
        DialogueContinue = 2,
        DialogueEnd = 3,
        CutsceneStart = 4,
        CutsceneSkip = 5, // wire value only since 0.5.65 (per-player skips)
        CutsceneProceed = 6, // wire value only since 0.5.65 (per-player proceeds)
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
        public long ActiveWorldId;
        public byte ActiveStoryCmd;
        /// <summary>Full commit after a host SaveManager.Load / NewGame: the table replaces the client's SProgress (absent keys are removed).</summary>
        public bool Authoritative;

        public void Serialize(NetDataWriter w)
        {
            w.Put(FullRefresh);
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
            w.Put(ActiveWorldId);
            w.Put(ActiveStoryCmd);
            w.Put(Authoritative);
        }

        public static StoryCommitMessage Deserialize(NetDataReader r)
        {
            var msg = new StoryCommitMessage
            {
                FullRefresh = r.GetBool(),
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
            msg.ActiveWorldId = r.GetLong();
            msg.ActiveStoryCmd = r.GetByte();
            msg.Authoritative = r.GetBool();
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
}
