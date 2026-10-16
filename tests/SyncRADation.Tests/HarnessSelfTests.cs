using System;
using System.Linq;
using SyncRADation.Networking;
using Xunit;

namespace SyncRADation.Tests
{
    /// <summary>Guards against a fuzzer that silently stopped fuzzing (all-default data, comparer that accepts anything).</summary>
    public class HarnessSelfTests
    {
        [Fact]
        public void Discovery_finds_every_message_plus_the_nested_entry_structs()
        {
            var names = WireFuzz.AllWireTypes().Select(t => t.Name).ToHashSet();
            Assert.True(WireFuzz.AllMessageTypes().Length >= 35, "expected all 35 NetMessageType messages");
            foreach (string n in new[] { "PuzzleStateEntry", "EnemySnapshotNet", "BossSnapshotNet", "StoryFlagEntry", "StorageBoxItem", "WorldPickupEntry", "HandshakeMessage", "PartyLifeMessage" })
                Assert.Contains(n, names);
        }

        [Fact]
        public void Random_fill_varies_and_populates_fields()
        {
            var a = WireFuzz.Write(WireFuzz.Fill(typeof(PlayerStateMessage), new Random(1), FillMode.Random)).CopyData();
            var b = WireFuzz.Write(WireFuzz.Fill(typeof(PlayerStateMessage), new Random(2), FillMode.Random)).CopyData();
            Assert.NotEqual(a, b);
            var z = WireFuzz.Write(WireFuzz.Fill(typeof(PlayerStateMessage), new Random(1), FillMode.Empty)).CopyData();
            Assert.NotEqual(a, z);
        }

        [Fact]
        public void Max_fill_really_hits_the_caps()
        {
            var e = (EnemyStateMessage)WireFuzz.Fill(typeof(EnemyStateMessage), new Random(1), FillMode.Max);
            Assert.Equal(NetWire.MaxEnemies, e.Enemies.Length);
            var h = (SceneHelloMessage)WireFuzz.Fill(typeof(SceneHelloMessage), new Random(1), FillMode.Max);
            Assert.Equal(NetWire.MaxString, h.SceneName.Length);
        }

        [Fact]
        public void Comparer_detects_a_single_differing_field_and_a_differing_array_element()
        {
            var a = (PuzzleStateEntry)WireFuzz.Fill(typeof(PuzzleStateEntry), new Random(3), FillMode.Random);
            var b = a;
            Assert.Empty(WireFuzz.Diff(a, b));
            b.Mask ^= 1;
            Assert.Single(WireFuzz.Diff(a, b));

            var m1 = new PartyKeyRingMessage { ItemEnums = new ushort[] { 1, 2, 3 } };
            var m2 = new PartyKeyRingMessage { ItemEnums = new ushort[] { 1, 9, 3 } };
            Assert.Single(WireFuzz.Diff(m1, m2));
            Assert.NotEmpty(WireFuzz.Diff(m1, new PartyKeyRingMessage { ItemEnums = new ushort[] { 1, 2 } }));
            Assert.Empty(WireFuzz.Diff(new PartyKeyRingMessage(), new PartyKeyRingMessage { ItemEnums = new ushort[0] }));
        }

        [Fact]
        public void Unregistered_array_field_fails_loudly()
        {
            Assert.Throws<InvalidOperationException>(() => WireFuzz.Fill(typeof(WithUnregisteredArray), new Random(1), FillMode.Random));
        }

        private struct WithUnregisteredArray { public int[] Mystery; }
    }
}
