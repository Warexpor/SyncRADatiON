using System.Collections.Generic;
using SyncRADation.Networking;
using Xunit;

namespace SyncRADation.Tests
{
    public class StoryWireTests
    {
        static StoryFlagEntry B(string k, bool v) => new StoryFlagEntry { Kind = 0, Key = k, BoolVal = v };
        static StoryFlagEntry I(string k, int v) => new StoryFlagEntry { Kind = 1, Key = k, IntVal = v };

        [Fact]
        public void TakeDirty_returns_only_changed_keys_and_clears_the_set()
        {
            var flags = new Dictionary<string, StoryFlagEntry>
            {
                ["a"] = B("a", true), ["b"] = I("b", 3), ["c"] = B("c", false),
            };
            var dirty = new HashSet<string> { "b", "gone" };
            var sent = StoryWire.TakeDirty(flags, dirty);
            Assert.Single(sent);
            Assert.Equal("b", sent[0].Key);
            Assert.Equal(3, sent[0].IntVal);
            Assert.Empty(dirty);
            Assert.Empty(StoryWire.TakeDirty(flags, dirty));
        }

        [Fact]
        public void MergeFlags_newer_wins_per_key_and_keeps_older_only_keys()
        {
            var older = new[] { B("a", true), I("b", 1), B("c", true) };
            var newer = new[] { I("b", 2), B("d", true) };
            var m = StoryWire.MergeFlags(older, newer);
            Assert.Equal(4, m.Length);
            Assert.Equal("a", m[0].Key);
            Assert.Equal(2, m[1].IntVal); // "b" replaced in place by the newer value
            Assert.Equal("c", m[2].Key);
            Assert.Equal("d", m[3].Key);
        }

        [Fact]
        public void MergeFlags_handles_null_and_empty_sides()
        {
            var one = new[] { B("a", true) };
            Assert.Same(one, StoryWire.MergeFlags(null, one));
            Assert.Same(one, StoryWire.MergeFlags(one, null));
            Assert.Same(one, StoryWire.MergeFlags(new StoryFlagEntry[0], one));
            Assert.Null(StoryWire.MergeFlags(null, null));
        }

        [Fact]
        public void CountsEndHere_host_tag_suppresses_everyone_player_tag_only_that_player()
        {
            Assert.True(StoryWire.CountsEndHere("", 2));
            Assert.True(StoryWire.CountsEndHere(null, 2));
            Assert.False(StoryWire.CountsEndHere(StoryWire.HostCounted, 2));
            Assert.True(StoryWire.CountsEndHere(StoryWire.PlayerCounted(2), 2));
            Assert.False(StoryWire.CountsEndHere(StoryWire.PlayerCounted(2), 3));
            Assert.True(StoryWire.CountsEndHere("replay", 3)); // unknown tag = legacy, count
            Assert.True(StoryWire.CountsEndHere("pX", 3));
        }

        [Fact]
        public void Dialogue_tag_round_trips_and_rejects_garbage()
        {
            Assert.Equal("17:0", StoryWire.DialogueTag(17, 0));
            Assert.True(StoryWire.TryParseDialogueTag("17:4", out var id, out var step));
            Assert.Equal(17, id);
            Assert.Equal(4, step);
            Assert.False(StoryWire.TryParseDialogueTag("", out _, out _));
            Assert.False(StoryWire.TryParseDialogueTag("nocolon", out _, out _));
            Assert.False(StoryWire.TryParseDialogueTag(":3", out _, out _));
            Assert.False(StoryWire.TryParseDialogueTag("a:b", out _, out _));
        }
    }
}
