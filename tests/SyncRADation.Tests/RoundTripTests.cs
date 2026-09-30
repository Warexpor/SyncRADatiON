using System;
using System.Collections.Generic;
using System.Linq;
using LiteNetLib.Utils;
using SyncRADation.Networking;
using Xunit;

namespace SyncRADation.Tests
{
    /// <summary>Every wire struct survives NetDataWriter -> NetDataReader unchanged. Types are discovered by reflection.</summary>
    public class RoundTripTests
    {
        private const int Iterations = 40;

        public static IEnumerable<object[]> Wire() => WireFuzz.AllWireTypes().Select(t => new object[] { t.Name });
        public static IEnumerable<object[]> Messages() => WireFuzz.AllMessageTypes().Select(t => new object[] { t.Name });

        internal static Type ByName(string name) => WireFuzz.AllWireTypes().Single(t => t.Name == name);

        private static void AssertRoundTrip(object msg, string ctx)
        {
            object back = WireFuzz.RoundTrip(msg, out NetDataReader r, out int len);
            List<string> diffs = WireFuzz.Diff(msg, back);
            Assert.True(diffs.Count == 0, ctx + ": " + string.Join("; ", diffs.Take(5)));
            Assert.True(r.EndOfData, ctx + ": reader left " + r.AvailableBytes + " of " + len + " bytes unread (writer/reader layout drift)");
        }

        [Theory, MemberData(nameof(Wire))]
        public void Random_fields_round_trip(string typeName)
        {
            Type t = ByName(typeName);
            for (int seed = 0; seed < Iterations; seed++)
                AssertRoundTrip(WireFuzz.Fill(t, new Random(seed * 7919 + 1), FillMode.Random), typeName + " seed " + seed);
        }

        [Theory, MemberData(nameof(Wire))]
        public void Max_cap_counts_and_max_length_strings_round_trip(string typeName)
        {
            Type t = ByName(typeName);
            for (int seed = 0; seed < 3; seed++)
                AssertRoundTrip(WireFuzz.Fill(t, new Random(seed + 100), FillMode.Max), typeName + " max seed " + seed);
        }

        [Theory, MemberData(nameof(Wire))]
        public void Empty_lists_and_empty_strings_round_trip(string typeName)
        {
            AssertRoundTrip(WireFuzz.Fill(ByName(typeName), new Random(5), FillMode.Empty), typeName + " empty");
        }

        [Theory, MemberData(nameof(Wire))]
        public void Default_struct_with_null_strings_and_arrays_round_trips(string typeName)
        {
            AssertRoundTrip(WireFuzz.Fill(ByName(typeName), new Random(6), FillMode.Default), typeName + " default");
        }

        [Theory, MemberData(nameof(Wire))]
        public void Serialization_is_deterministic(string typeName)
        {
            object msg = WireFuzz.Fill(ByName(typeName), new Random(77), FillMode.Random);
            Assert.Equal(WireFuzz.Write(msg).CopyData(), WireFuzz.Write(msg).CopyData());
        }

        [Theory, MemberData(nameof(Messages))]
        public void Truncated_packet_throws_instead_of_yielding_a_message(string typeName)
        {
            Type t = ByName(typeName);
            object msg = WireFuzz.Fill(t, new Random(9), FillMode.Random);
            byte[] full = WireFuzz.Write(msg).CopyData();
            // Dropping the final byte must always be detected (every message ends in a fixed-size or length-checked field).
            byte[] cut = full.Take(full.Length - 1).ToArray();
            Assert.ThrowsAny<Exception>(() => WireFuzz.Read(t, new NetDataReader(cut)));
        }

        // ------------------------------------------------------------------ coverage guards

        [Fact]
        public void Every_NetMessageType_has_a_message_class_and_vice_versa()
        {
            var names = Enum.GetNames(typeof(NetMessageType)).Where(n => n != "_Highest").ToList();
            var msgTypes = WireFuzz.AllMessageTypes().ToDictionary(t => t.Name);

            foreach (string n in names)
                Assert.True(msgTypes.ContainsKey(n + "Message"),
                    "NetMessageType." + n + " has no public struct " + n + "Message with Serialize(NetDataWriter)/static Deserialize(NetDataReader)");

            foreach (string m in msgTypes.Keys)
                Assert.True(names.Contains(m.Substring(0, m.Length - "Message".Length)),
                    m + " is a wire message but has no NetMessageType entry (or is misnamed)");
        }

        [Fact]
        public void Every_array_field_on_a_wire_type_has_a_registered_cap()
        {
            var seen = new HashSet<string>();
            foreach (Type t in WireFuzz.AllWireTypes())
                foreach (var f in t.GetFields().Where(f => f.FieldType.IsArray))
                {
                    string key = t.Name + "." + f.Name;
                    seen.Add(key);
                    Assert.True(WireFuzz.ArrayCaps.ContainsKey(key), key + " is an array field without a WireFuzz.ArrayCaps entry");
                }
            foreach (string k in WireFuzz.ArrayCaps.Keys)
                Assert.True(seen.Contains(k), "ArrayCaps lists " + k + " which no longer exists");
        }

        // ------------------------------------------------------------------ FMOD emitters

        [Fact]
        public void FmodEmitter_component_index_survives_the_wire_for_every_byte_value()
        {
            for (int comp = 0; comp <= 255; comp++)
            {
                AssertRoundTrip(new FmodEmitterMessage { WorldId = -5, Play = true, Kind = 0, Path = "", Comp = (byte)comp }, "FmodEmitter comp=" + comp);
                AssertRoundTrip(new FmodEmitterRequestMessage { WorldId = long.MinValue, Play = comp % 2 == 0, Comp = (byte)comp }, "FmodEmitterRequest comp=" + comp);
            }
        }

        // ------------------------------------------------------------------ PuzzleStateEntry

        public static IEnumerable<object[]> PuzzleTypes() =>
            Enum.GetValues(typeof(PuzzleType)).Cast<PuzzleType>().Select(p => new object[] { p });

        [Theory, MemberData(nameof(PuzzleTypes))]
        public void PuzzleStateEntry_round_trips_for_every_PuzzleType(PuzzleType type)
        {
            var rng = new Random((int)type * 31);
            foreach (int seq in new[] { 0, 1, -1, int.MaxValue, int.MinValue, rng.Next() })
                foreach (int mask in new[] { 0, 1, 0x1FF, 0x00FF0000, unchecked((int)0xFFFFFFFF), 0x0001_0100, rng.Next() })
                {
                    var e = (PuzzleStateEntry)WireFuzz.Fill(typeof(PuzzleStateEntry), rng, FillMode.Random);
                    e.Type = type;
                    e.Seq = seq;
                    e.Mask = mask;
                    AssertRoundTrip(e, type + " seq=" + seq + " mask=" + mask);
                }
        }

        [Fact]
        public void PuzzleStateMessage_carries_one_entry_per_PuzzleType_with_Seq_and_Mask_intact()
        {
            var types = Enum.GetValues(typeof(PuzzleType)).Cast<PuzzleType>().ToArray();
            var entries = types.Select((t, i) => new PuzzleStateEntry
            {
                Type = t, WorldId = -(long)i - 1, Bool0 = i % 2 == 0, Bool2 = true, Int0 = i, Int3 = -i,
                Float0 = i * 0.5f, Float1 = -i, Seq = 1000 + i, Mask = 1 << (i % 24)
            }).ToArray();
            AssertRoundTrip(new PuzzleStateMessage { SenderPlayerId = 3, FullRefresh = true, Entries = entries }, "PuzzleStateMessage all types");
        }

        // ------------------------------------------------------------------ hand-checked field semantics

        [Fact]
        public void Handshake_round_trips_the_real_protocol_version_and_schema_hash()
        {
            var h = new HandshakeMessage { ProtocolVersion = PluginInfo.ProtocolVersion, AssignedPlayerId = 2, SchemaHash = NetSchema.Hash, ModVersion = PluginInfo.Version, GameBuildHash = 0xC0FFEE11u, GameBuild = "1.0.3 / Unity 6000" };
            var back = HandshakeMessage.Deserialize(new NetDataReader(WireFuzz.Write(h).CopyData()));
            Assert.Equal(PluginInfo.ProtocolVersion, back.ProtocolVersion);
            Assert.Equal(NetSchema.Hash, back.SchemaHash);
            Assert.Equal(PluginInfo.Version, back.ModVersion);
            Assert.Equal(2, back.AssignedPlayerId);
            Assert.Equal(0xC0FFEE11u, back.GameBuildHash);
            Assert.Equal("1.0.3 / Unity 6000", back.GameBuild);
        }

        [Fact]
        public void Angle_encoding_clamps_to_0_360_and_is_monotonic()
        {
            Assert.Equal((ushort)0, PlayerStateMessage.EncodeAngle(-50f));
            Assert.Equal((ushort)0, PlayerStateMessage.EncodeAngle(0f));
            Assert.Equal((ushort)65535, PlayerStateMessage.EncodeAngle(360f));
            Assert.Equal((ushort)65535, PlayerStateMessage.EncodeAngle(999f));
            Assert.Equal(0f, PlayerStateMessage.DecodeAngle(0));
            Assert.Equal(360f, PlayerStateMessage.DecodeAngle(65535));
            ushort prev = 0;
            for (float a = 0; a <= 360f; a += 0.37f)
            {
                ushort e = PlayerStateMessage.EncodeAngle(a);
                Assert.True(e >= prev);
                prev = e;
                Assert.InRange(Math.Abs(PlayerStateMessage.DecodeAngle(e) - a), 0f, 360f / 65535f * 1.01f);
            }
        }

        [Fact]
        public void PlayerState_facing_quaternion_round_trips_and_normalizes()
        {
            var s = new PlayerStateMessage();
            s.SetFacingWorld(new UnityEngine.Quaternion(0f, 0.6f, 0f, -0.8f)); // w<0 is flipped to the canonical hemisphere
            Assert.Equal(0.8f, s.RotY);
            Assert.Equal(-0.6f, s.RootY);
            UnityEngine.Quaternion q = s.GetFacingWorld();
            Assert.Equal(1f, q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w, 4);
            Assert.Equal(UnityEngine.Quaternion.identity.w, new PlayerStateMessage().GetFacingWorld().w);
        }

        [Fact]
        public void Flag_enums_keep_their_full_bit_width_on_the_wire()
        {
            var s = new PlayerStateMessage { AnimBools = (AnimBools)0xFFFFFFFFu, AnimTriggers = (AnimTriggers)0xFFFF };
            var back = PlayerStateMessage.Deserialize(new NetDataReader(WireFuzz.Write(s).CopyData()));
            Assert.Equal((uint)0xFFFFFFFFu, (uint)back.AnimBools);
            Assert.Equal((ushort)0xFFFF, (ushort)back.AnimTriggers);
            var o = AvatarOneShotMessage.Deserialize(new NetDataReader(WireFuzz.Write(new AvatarOneShotMessage { SenderPlayerId = 1, Triggers = AnimTriggers.SnapTrigger }).CopyData()));
            Assert.Equal(AnimTriggers.SnapTrigger, o.Triggers);
        }

        [Fact]
        public void Party_messages_round_trip_with_null_room_as_empty()
        {
            var life = PartyLifeMessage.Deserialize(new NetDataReader(WireFuzz.Write(new PartyLifeMessage { Kind = PartyLifeKind.Wipe, Room = null, Scene = null, SaveStamp = long.MinValue, HasPos = true, PosX = 1, PosY = 2, PosZ = 3 }).CopyData()));
            Assert.Equal(PartyLifeKind.Wipe, life.Kind);
            Assert.Equal("", life.Room);
            Assert.Equal("", life.Scene);
            Assert.Equal(long.MinValue, life.SaveStamp);
            Assert.Equal((1f, 2f, 3f), (life.PosX, life.PosY, life.PosZ));
            var save = PartySaveMessage.Deserialize(new NetDataReader(WireFuzz.Write(new PartySaveMessage { Slot = 2, Counter = -1, Stamp = 99, Flags = PartySaveMessage.FlagJoin }).CopyData()));
            Assert.Equal((2, -1, 99L, (byte)1), (save.Slot, save.Counter, save.Stamp, save.Flags));
        }
    }
}
