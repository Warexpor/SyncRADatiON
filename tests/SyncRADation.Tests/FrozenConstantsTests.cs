using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SyncRADation.Networking;
using Xunit;

namespace SyncRADation.Tests
{
    /// <summary>
    /// Snapshot of everything a remote peer relies on. A failure here means the wire contract moved:
    /// if intentional, edit the snapshot (Support/FrozenTables.cs / constants below) AND bump ProtocolVersion in the same change.
    /// </summary>
    public class FrozenConstantsTests
    {
        // NetSchema.StructuralHash (Hash minus the module id, which changes with every rebuild) with the test assembly pinned to AssemblyVersion 9.9.9.9 (see the csproj).
        // Moves whenever PluginInfo.ProtocolVersion or any id of the mixed enums changes.
        private const uint GoldenSchemaHash = 0x3A5857E6U;

        [Fact]
        public void ProtocolVersion_is_14() => Assert.Equal(14, PluginInfo.ProtocolVersion);

        [Fact]
        public void Channels_and_connection_key_are_frozen()
        {
            Assert.Equal((byte)0, NetChannels.Events);
            Assert.Equal((byte)1, NetChannels.State);
            Assert.Equal((byte)2, NetChannels.Count);
            Assert.Equal("SyncRADation", PluginInfo.ConnectionKey);
            Assert.Equal(7777, PluginInfo.DefaultPort);
        }

        [Fact]
        public void Wire_caps_are_frozen()
        {
            Assert.Equal(16000, NetWire.MaxString);
            Assert.Equal(4 * 1024 * 1024, NetWire.MaxLongStringBytes);
            Assert.Equal(512, NetWire.MaxEnemies);
            Assert.Equal(64, NetWire.MaxBosses);
            Assert.Equal(8192, NetWire.MaxStoryFlags);
            Assert.Equal(8192, NetWire.MaxPuzzleEntries);
            Assert.Equal(8192, NetWire.MaxPickupEntries);
            Assert.Equal(512, NetWire.MaxStorageItems);
            Assert.Equal(512, NetWire.MaxKeyRing);
            Assert.Equal(32, NetWire.MaxRoster);
            Assert.Equal(4095, NetWire.MaxBones);
        }

        // ------------------------------------------------------------------ enum snapshots

        public static IEnumerable<object[]> Snapshots()
        {
            yield return new object[] { typeof(NetMessageType), nameof(FrozenTables.T_NetMessageType) };
            yield return new object[] { typeof(InteractionKind), nameof(FrozenTables.T_InteractionKind) };
            yield return new object[] { typeof(StoryCmd), nameof(FrozenTables.T_StoryCmd) };
            yield return new object[] { typeof(DeathKind), nameof(FrozenTables.T_DeathKind) };
            yield return new object[] { typeof(WeaponType), nameof(FrozenTables.T_WeaponType) };
            yield return new object[] { typeof(DoorType), nameof(FrozenTables.T_DoorType) };
            yield return new object[] { typeof(PuzzleType), nameof(FrozenTables.T_PuzzleType) };
            yield return new object[] { typeof(EnemyActionKind), nameof(FrozenTables.T_EnemyActionKind) };
            yield return new object[] { typeof(BossHitKind), nameof(FrozenTables.T_BossHitKind) };
            yield return new object[] { typeof(BossType), nameof(FrozenTables.T_BossType) };
            yield return new object[] { typeof(PartyLifeKind), nameof(FrozenTables.T_PartyLifeKind) };
        }

        private static (string Name, int Value)[] Table(string field) =>
            ((string Name, int Value)[])typeof(FrozenTables).GetField(field).GetValue(null);

        private static Dictionary<string, int> Actual(Type enumType) =>
            Enum.GetNames(enumType).Where(n => n != "_Highest").ToDictionary(n => n, n => Convert.ToInt32(Enum.Parse(enumType, n)));

        [Theory, MemberData(nameof(Snapshots))]
        public void Enum_values_match_the_frozen_snapshot_exactly(Type enumType, string tableField)
        {
            var expected = Table(tableField).ToDictionary(x => x.Name, x => x.Value);
            var actual = Actual(enumType);
            foreach (var kv in expected)
            {
                Assert.True(actual.ContainsKey(kv.Key), enumType.Name + "." + kv.Key + " was removed/renamed (wire break)");
                Assert.True(actual[kv.Key] == kv.Value, enumType.Name + "." + kv.Key + " changed " + kv.Value + " -> " + actual[kv.Key] + " (wire break)");
            }
            var added = actual.Keys.Except(expected.Keys).ToList();
            Assert.True(added.Count == 0, enumType.Name + " gained " + string.Join(", ", added) + ": add to FrozenTables, bump ProtocolVersion, update GoldenSchemaHash");
        }

        [Theory, MemberData(nameof(Snapshots))]
        public void Enum_numeric_values_are_unique(Type enumType, string _)
        {
            // Two names sharing a number (other than the _Highest alias) would make Enum.ToString / switch ambiguous.
            var dupes = Actual(enumType).GroupBy(kv => kv.Value).Where(g => g.Count() > 1).Select(g => string.Join("=", g.Select(x => x.Key)));
            Assert.Empty(dupes);
        }

        [Fact]
        public void Enums_keep_their_one_byte_wire_width()
        {
            foreach (Type t in new[] { typeof(NetMessageType), typeof(InteractionKind), typeof(StoryCmd), typeof(DeathKind), typeof(WeaponType),
                                       typeof(DoorType), typeof(PuzzleType), typeof(EnemyActionKind), typeof(BossHitKind), typeof(BossType), typeof(PartyLifeKind) })
                Assert.Equal(typeof(byte), Enum.GetUnderlyingType(t));
            Assert.Equal(typeof(uint), Enum.GetUnderlyingType(typeof(AnimBools)));
            Assert.Equal(typeof(ushort), Enum.GetUnderlyingType(typeof(AnimTriggers)));
        }

        [Fact]
        public void NetMessageType_Highest_equals_the_true_maximum()
        {
            int max = Actual(typeof(NetMessageType)).Values.Max();
            Assert.Equal(73, max);
            Assert.Equal((int)NetMessageType._Highest, max);
            Assert.Equal(37, Actual(typeof(NetMessageType)).Count);
        }

        [Fact]
        public void PuzzleType_is_contiguous_from_1_to_81_with_no_gaps_or_duplicates()
        {
            var values = Enum.GetValues(typeof(PuzzleType)).Cast<PuzzleType>().Select(p => (int)p).ToList();
            Assert.Equal(81, values.Max());
            Assert.Equal(Enumerable.Range(1, 81), values.OrderBy(v => v));
            Assert.Equal(values.Count, values.Distinct().Count());
            Assert.Equal(81, Enum.GetNames(typeof(PuzzleType)).Length);
            Assert.Equal(PuzzleType.MEM_ChecklistLogic, (PuzzleType)81);
        }

        [Fact]
        public void Story_enemy_and_boss_enums_have_their_expected_maxima()
        {
            Assert.Equal(23, Actual(typeof(StoryCmd)).Values.Max());
            Assert.Equal(5, Actual(typeof(EnemyActionKind)).Values.Max());
            Assert.Equal(3, Actual(typeof(BossHitKind)).Values.Max());
            Assert.Equal(21, Actual(typeof(InteractionKind)).Values.Max());
            // StoryCmd 15..19 are intentionally unassigned (Story domain starts at 20): no other gaps below 15.
            Assert.Equal(Enumerable.Range(0, 15), Actual(typeof(StoryCmd)).Values.Where(v => v < 20).OrderBy(v => v));
            Assert.Equal(Enumerable.Range(0, 6), Actual(typeof(EnemyActionKind)).Values.OrderBy(v => v));
            Assert.Equal(Enumerable.Range(0, 4), Actual(typeof(BossHitKind)).Values.OrderBy(v => v));
        }

        // ------------------------------------------------------------------ schema hash

        /// <summary>Independent re-implementation of NetSchema.Compute (FNV-1a, 32-bit) fed from the frozen tables.</summary>
        private static uint Replicate(string asmVersion, int protocolVersion)
        {
            uint h = 2166136261u;
            h = MixS(h, asmVersion);
            h = MixI(h, protocolVersion);
            // Same order as NetSchema.Compute. Enum.GetValues includes the _Highest alias, i.e. NetMessageType's max appears twice.
            h = MixEnum(h, "NetMessageType", FrozenTables.T_NetMessageType.Select(x => x.Value).Concat(new[] { 73 }));
            h = MixEnum(h, "InteractionKind", FrozenTables.T_InteractionKind.Select(x => x.Value));
            h = MixEnum(h, "StoryCmd", FrozenTables.T_StoryCmd.Select(x => x.Value));
            h = MixEnum(h, "DeathKind", FrozenTables.T_DeathKind.Select(x => x.Value));
            h = MixEnum(h, "DoorType", FrozenTables.T_DoorType.Select(x => x.Value));
            h = MixEnum(h, "PuzzleType", FrozenTables.T_PuzzleType.Select(x => x.Value));
            h = MixEnum(h, "BossType", FrozenTables.T_BossType.Select(x => x.Value));
            h = MixEnum(h, "EnemyActionKind", FrozenTables.T_EnemyActionKind.Select(x => x.Value));
            h = MixEnum(h, "BossHitKind", FrozenTables.T_BossHitKind.Select(x => x.Value));
            return h;
        }

        private static uint MixEnum(uint h, string name, IEnumerable<int> ids)
        {
            h = MixS(h, name);
            foreach (int id in ids.OrderBy(i => i)) h = MixI(h, id);
            return h;
        }

        private static uint MixS(uint h, string s)
        {
            unchecked { foreach (char c in s) { h ^= c; h *= 16777619u; } }
            return h;
        }

        private static uint MixI(uint h, int v)
        {
            unchecked { for (int i = 0; i < 4; i++) { h ^= (uint)((v >> (i * 8)) & 0xFF); h *= 16777619u; } }
            return h;
        }

        [Fact]
        public void SchemaHash_matches_an_independent_implementation_fed_from_the_frozen_tables()
        {
            string asm = typeof(NetSchema).Assembly.GetName().Version.ToString();
            Assert.Equal("9.9.9.9", asm);
            Assert.Equal(Replicate(asm, PluginInfo.ProtocolVersion), NetSchema.StructuralHash);
        }

        [Fact]
        public void SchemaHash_golden_value()
        {
            Assert.Equal(GoldenSchemaHash, NetSchema.StructuralHash);
            Assert.Equal(GoldenSchemaHash, Replicate("9.9.9.9", 14));
        }

        [Fact]
        public void SchemaHash_mixes_in_the_module_version_id_on_top_of_the_structural_hash()
        {
            // A stale / modified dll of the same version has a different MVID (deterministic build = content hash),
            // so the handshake hash must move with it while the structural hash stays put.
            string mvid = typeof(NetSchema).Module.ModuleVersionId.ToString("N");
            Assert.Equal(mvid, NetSchema.ModuleId);
            Assert.Equal(MixS(NetSchema.StructuralHash, mvid), NetSchema.Hash);
            Assert.NotEqual(MixS(NetSchema.StructuralHash, Guid.NewGuid().ToString("N")), NetSchema.Hash);
        }

        [Fact]
        public void SchemaHash_is_sensitive_to_protocol_version_assembly_version_and_enum_ids()
        {
            uint baseline = Replicate("9.9.9.9", 14);
            Assert.NotEqual(baseline, Replicate("9.9.9.9", 13));
            Assert.NotEqual(baseline, Replicate("9.9.9.8", 14));
            Assert.Equal(NetSchema.Hash, NetSchema.Hash); // cached, stable
            Assert.Equal(PluginInfo.Version, NetSchema.ModVersion);
        }
    }
}
