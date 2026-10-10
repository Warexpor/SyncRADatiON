// DesyncCompare: the live desync check's rules must agree with scripts/pilot/digest-diff.py.
using System.Collections.Generic;
using SyncRADation.Networking;
using Xunit;

namespace SyncRADation.Tests
{
    public class DesyncCompareTests
    {
        static Dictionary<string, string> Diff(string[] host, string[] client, out int compared, out int skipped)
        {
            var d = new Dictionary<string, string>();
            DesyncCompare.Diff(new List<string>(host), new List<string>(client), d, out compared, out skipped);
            return d;
        }

        [Fact]
        public void Equal_worlds_have_no_differences_and_self_lines_are_ignored()
        {
            var h = new[] { "scene name LOV", "door2 AA open=False locked=True", "self hp 100 dead=False" };
            var c = new[] { "scene name LOV", "door2 AA open=False locked=True", "self hp 40 dead=False" };
            var d = Diff(h, c, out int compared, out int skipped);
            Assert.Empty(d);
            Assert.Equal(2, compared);
            Assert.Equal(0, skipped);
        }

        [Fact]
        public void A_real_difference_names_both_values()
        {
            var d = Diff(new[] { "door2 AA open=True locked=False" }, new[] { "door2 AA open=False locked=False" }, out _, out _);
            Assert.Equal("host=open=True locked=False client=open=False locked=False", d["door2 AA"]);
        }

        [Fact]
        public void A_line_only_one_peer_has_is_a_difference()
        {
            var d = Diff(new[] { "story flag_a 1" }, new string[0], out _, out _);
            Assert.Equal("host=1 client=(none)", d["story flag_a"]);
            d = Diff(new string[0], new[] { "story flag_b 1" }, out _, out _);
            Assert.Equal("host=(none) client=1", d["story flag_b"]);
        }

        [Theory]
        [InlineData("claimed", "gone")]
        [InlineData("triggered", "hidden")]
        public void Pickups_compare_as_here_or_taken(string host, string client)
        {
            Assert.Empty(Diff(new[] { "pickup 01 " + host }, new[] { "pickup 01 " + client }, out _, out _));
            Assert.Single(Diff(new[] { "pickup 01 here" }, new[] { "pickup 01 " + client }, out _, out _));
        }

        [Fact]
        public void A_pickup_one_peer_never_scanned_counts_as_taken()
        {
            Assert.Empty(Diff(new[] { "pickup 01 claimed" }, new string[0], out _, out _));
            Assert.Single(Diff(new[] { "pickup 01 here" }, new string[0], out _, out _));
        }

        [Fact]
        public void Enemy_names_are_not_compared()
        {
            Assert.Empty(Diff(new[] { "enemy 02 alive hp=25 EULR" }, new[] { "enemy 02 alive hp=25 Enemy_1_EULR" }, out _, out _));
            Assert.Single(Diff(new[] { "enemy 02 alive hp=25 EULR" }, new[] { "enemy 02 dead EULR" }, out _, out _));
        }

        [Fact]
        public void Per_player_puzzle_types_and_asleep_lines_are_skipped()
        {
            var d = Diff(
                new[] { "puzzle EnemyManagerState:01 b=100 i=0,0,0,0 f=0.0,0.0", "puzzle Keypad3D:02 b=100 i=1,0,0,0 f=0.0,0.0" },
                new[] { "puzzle EnemyManagerState:01 b=000 i=0,0,0,0 f=0.0,0.0", "puzzle Keypad3D:02 b=000 i=0,0,0,0 f=0.0,0.0 asleep" },
                out int compared, out int skipped);
            Assert.Empty(d);
            Assert.Equal(0, compared);
            Assert.Equal(2, skipped);
        }

        [Fact]
        public void AraNest_activated_bit_is_each_players_own()
        {
            Assert.Empty(Diff(new[] { "puzzle AraNest:03 b=110 i=0,0,0,0 f=0.0,0.0" }, new[] { "puzzle AraNest:03 b=100 i=0,0,0,0 f=0.0,0.0" }, out _, out _));
            Assert.Single(Diff(new[] { "puzzle AraNest:03 b=111 i=0,0,0,0 f=0.0,0.0" }, new[] { "puzzle AraNest:03 b=100 i=0,0,0,0 f=0.0,0.0" }, out _, out _));
        }

        [Fact]
        public void A_field_asleep_on_one_peer_is_left_out_of_the_compare()
        {
            Assert.Empty(Diff(new[] { "door2 AA open=False locked=asleep" }, new[] { "door2 AA open=False locked=True" }, out _, out _));
            Assert.Single(Diff(new[] { "door2 AA open=True locked=asleep" }, new[] { "door2 AA open=False locked=True" }, out _, out _));
        }
    }
}
