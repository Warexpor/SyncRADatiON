using System.Collections.Generic;
using SyncRADation.Networking;
using Xunit;

namespace SyncRADation.Tests
{
    /// <summary>Bulk story commit apply: writes planned against one slot kind (StorySlotPlan.cs).</summary>
    public class StorySlotPlanTests
    {
        static StoryFlagEntry I(string k, int v) => new StoryFlagEntry { Kind = 1, Key = k, IntVal = v };
        static StoryFlagEntry F(string k, float v) => new StoryFlagEntry { Kind = 2, Key = k, FloatVal = v };

        static List<StorySlotPlan.Write> Plan(string[] keys, int[] vals, params StoryFlagEntry[] incoming)
            => StorySlotPlan.Plan(keys, i => I(keys[i], vals[i]), incoming);

        [Fact]
        public void Unchanged_keys_write_nothing_changed_keys_set_in_place_missing_keys_append()
        {
            var w = Plan(new[] { "a", "b", "c" }, new[] { 1, 2, 3 }, I("a", 1), I("b", 5), I("d", 7));
            Assert.Equal(2, w.Count);
            Assert.Equal(1, w[0].Index);
            Assert.Equal(5, w[0].Entry.IntVal);
            Assert.Equal(-1, w[1].Index);
            Assert.Equal("d", w[1].Entry.Key);
        }

        [Fact]
        public void The_first_matching_key_is_the_one_compared_and_written_like_native_Get_and_Set()
        {
            var w = Plan(new[] { "a", "a" }, new[] { 1, 9 }, I("a", 9));
            Assert.Single(w);
            Assert.Equal(0, w[0].Index);
        }

        [Fact]
        public void A_repeated_key_in_one_batch_ends_on_its_last_value()
        {
            var w = Plan(new[] { "a" }, new[] { 1 }, I("a", 2), I("a", 1), I("n", 3), I("n", 4));
            Assert.Equal(2, w.Count);
            Assert.Equal(0, w[0].Index);
            Assert.Equal(1, w[0].Entry.IntVal);
            Assert.Equal(-1, w[1].Index);
            Assert.Equal(4, w[1].Entry.IntVal);
        }

        [Fact]
        public void Empty_keys_are_ignored_and_an_empty_slot_appends_everything()
        {
            var w = Plan(new string[0], new int[0], I("", 1), I("x", 2), I(null, 3));
            Assert.Single(w);
            Assert.Equal(-1, w[0].Index);
        }

        [Fact]
        public void Values_compare_per_kind()
        {
            Assert.True(StorySlotPlan.SameValue(F("f", 1f), F("f", 1f + 1e-8f)));
            Assert.False(StorySlotPlan.SameValue(F("f", 1f), F("f", 1.01f)));
            Assert.False(StorySlotPlan.SameValue(I("i", 1), F("i", 1f)));
            var s1 = new StoryFlagEntry { Kind = 3, Key = "s", StringVal = null };
            var s2 = new StoryFlagEntry { Kind = 3, Key = "s", StringVal = "" };
            Assert.True(StorySlotPlan.SameValue(s1, s2));
            var v1 = new StoryFlagEntry { Kind = 4, Key = "v", FloatVal = 1f, VecY = 2f, VecZ = 3f };
            var v2 = v1;
            v2.VecZ = 4f;
            Assert.False(StorySlotPlan.SameValue(v1, v2));
            Assert.True(StorySlotPlan.SameValue(v1, v1));
        }
    }
}
