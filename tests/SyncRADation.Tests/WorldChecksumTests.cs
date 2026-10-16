using System;
using System.Collections.Generic;
using System.Linq;
using SyncRADation.Sync;
using Xunit;

namespace SyncRADation.Tests
{
    public class WorldChecksumTests
    {
        // Independent reference: FNV-1a 64 over the ascending ids (8 bytes little-endian each), then the count (4 bytes little-endian).
        private static ulong Reference(IEnumerable<ulong> ids)
        {
            const ulong offset = 14695981039346656037UL, prime = 1099511628211UL;
            ulong h = offset;
            var sorted = ids.OrderBy(x => x).ToList();
            foreach (ulong id in sorted)
                foreach (byte b in BitConverter.GetBytes(id)) { h ^= b; h = unchecked(h * prime); }
            foreach (byte b in BitConverter.GetBytes(sorted.Count)) { h ^= b; h = unchecked(h * prime); }
            return h;
        }

        [Fact]
        public void Compute_matches_the_reference_fnv1a64()
        {
            var ids = new List<ulong> { 0xAF63BD4C8601B7DFUL, 1, 0x05037FC03E41D4B8UL, ulong.MaxValue, 42 };
            Assert.Equal(Reference(ids), WorldChecksum.Compute(ids));
            Assert.Equal(Reference(new ulong[0]), WorldChecksum.Compute(new List<ulong>()));
            Assert.Equal(Reference(new ulong[0]), WorldChecksum.Compute(null));
        }

        [Fact]
        public void Compute_is_independent_of_insertion_order_and_does_not_reorder_its_input()
        {
            var ids = new List<ulong> { 9, 3, 7, 1, 5 };
            var shuffled = new List<ulong> { 5, 1, 7, 3, 9 };
            Assert.Equal(WorldChecksum.Compute(ids), WorldChecksum.Compute(shuffled));
            Assert.Equal(new ulong[] { 9, 3, 7, 1, 5 }, ids.ToArray());
            var hashSet = new HashSet<ulong>(ids);
            Assert.Equal(WorldChecksum.Compute(ids), WorldChecksum.Compute(hashSet));
        }

        [Fact]
        public void ComputeSorted_equals_Compute_for_a_sorted_list()
        {
            var ids = new List<ulong> { 11, 4, 99, 2 };
            var sorted = ids.OrderBy(x => x).ToList();
            Assert.Equal(WorldChecksum.Compute(ids), WorldChecksum.ComputeSorted(sorted));
        }

        [Fact]
        public void One_changed_added_or_removed_id_changes_the_checksum()
        {
            var baseIds = new List<ulong> { 100, 200, 300 };
            ulong baseline = WorldChecksum.Compute(baseIds);
            Assert.NotEqual(baseline, WorldChecksum.Compute(new List<ulong> { 100, 200, 301 }));   // renamed object
            Assert.NotEqual(baseline, WorldChecksum.Compute(new List<ulong> { 100, 200 }));        // missing object
            Assert.NotEqual(baseline, WorldChecksum.Compute(new List<ulong> { 100, 200, 300, 0 })); // extra object
            Assert.NotEqual(WorldChecksum.Compute(new List<ulong>()), WorldChecksum.Compute(new List<ulong> { 0 }));
        }

        [Fact]
        public void Combine_depends_on_every_category_sum_in_order()
        {
            var a = new ulong[] { 1, 2, 3, 4 };
            Assert.Equal(WorldChecksum.Combine(a), WorldChecksum.Combine(new ulong[] { 1, 2, 3, 4 }));
            Assert.NotEqual(WorldChecksum.Combine(a), WorldChecksum.Combine(new ulong[] { 1, 2, 4, 3 }));
            Assert.NotEqual(WorldChecksum.Combine(a), WorldChecksum.Combine(new ulong[] { 1, 2, 3, 5 }));
        }

        [Fact]
        public void MismatchMask_flags_the_differing_categories_only()
        {
            int[] ac = { 12, 30, 5, 0 };
            ulong[] asum = { 1, 2, 3, 4 };
            Assert.Equal(0u, WorldChecksum.MismatchMask(ac, asum, new[] { 12, 30, 5, 0 }, new ulong[] { 1, 2, 3, 4 }));
            // count differs in category 1
            Assert.Equal(0b0010u, WorldChecksum.MismatchMask(ac, asum, new[] { 12, 31, 5, 0 }, new ulong[] { 1, 2, 3, 4 }));
            // same count, different ids (renamed) in categories 0 and 3
            Assert.Equal(0b1001u, WorldChecksum.MismatchMask(ac, asum, ac, new ulong[] { 9, 2, 3, 8 }));
        }

        [Fact]
        public void MismatchMask_is_zero_without_data_and_compares_only_the_shared_categories()
        {
            int[] c = { 1, 2 };
            ulong[] s = { 1, 2 };
            Assert.Equal(0u, WorldChecksum.MismatchMask(null, null, c, s));
            Assert.Equal(0u, WorldChecksum.MismatchMask(c, s, new int[0], new ulong[0]));
            Assert.Equal(0u, WorldChecksum.MismatchMask(c, s, new[] { 1, 2, 99 }, new ulong[] { 1, 2, 99 }));
            Assert.Equal(0b10u, WorldChecksum.MismatchMask(c, s, new[] { 1, 3, 99 }, new ulong[] { 1, 2, 99 }));
        }

        [Fact]
        public void Format_and_estimate_describe_the_mismatch()
        {
            int[] host = { 12, 30, 5, 0 };
            int[] peer = { 12, 31, 5, 0 };
            uint mask = WorldChecksum.MismatchMask(host, new ulong[] { 1, 2, 3, 4 }, peer, new ulong[] { 1, 9, 3, 4 });
            Assert.Equal("enemies 12/12 doubleDoors 30/31! connectedDoors 5/5 slidingDoors 0/0", WorldChecksum.Format(host, peer, mask));
            Assert.Equal(1, WorldChecksum.EstimateDiffIds(host, peer, mask));
            // same count but renamed ids: still at least one id differs
            Assert.Equal(1, WorldChecksum.EstimateDiffIds(host, host, 0b0001u));
            Assert.Equal(0, WorldChecksum.EstimateDiffIds(host, peer, 0));
            Assert.Equal(8, WorldChecksum.EstimateDiffIds(new[] { 10, 4 }, new[] { 3, 5 }, 0b11u));
        }

        [Fact]
        public void Diff_lists_missing_and_extra_ids_sorted_and_capped_with_full_totals()
        {
            var local = new List<ulong> { 1, 2, 3, 100, 101 };
            var remote = new List<ulong> { 2, 3, 4, 5, 6, 7 };
            var missing = new List<ulong>();
            var extra = new List<ulong>();
            WorldChecksum.Diff(local, remote, 3, missing, extra, out int mTotal, out int eTotal);
            Assert.Equal(new ulong[] { 4, 5, 6 }, missing.ToArray());   // host has, we lack (first 3 of 4)
            Assert.Equal(4, mTotal);
            Assert.Equal(new ulong[] { 1, 100, 101 }, extra.ToArray()); // we have, host lacks
            Assert.Equal(3, eTotal);
        }

        [Fact]
        public void Diff_of_identical_sets_is_empty_and_null_sets_are_treated_as_empty()
        {
            var ids = new List<ulong> { 5, 6 };
            var missing = new List<ulong>();
            var extra = new List<ulong>();
            WorldChecksum.Diff(ids, new List<ulong>(ids), 20, missing, extra, out int m, out int e);
            Assert.Empty(missing);
            Assert.Empty(extra);
            Assert.Equal(0, m + e);
            WorldChecksum.Diff(null, ids, 20, missing, extra, out m, out e);
            Assert.Equal(2, m);
            Assert.Equal(0, e);
        }

        [Fact]
        public void ChunkCount_covers_the_list_and_never_returns_zero()
        {
            Assert.Equal(1, WorldChecksum.ChunkCount(0, 256));
            Assert.Equal(1, WorldChecksum.ChunkCount(256, 256));
            Assert.Equal(2, WorldChecksum.ChunkCount(257, 256));
            Assert.Equal(8, WorldChecksum.ChunkCount(2048, 256));
            Assert.Throws<ArgumentOutOfRangeException>(() => WorldChecksum.ChunkCount(10, 0));
        }

        [Fact]
        public void Category_names_follow_the_wire_indexes()
        {
            Assert.Equal("enemies", WorldChecksum.NameOf(WorldChecksum.Enemies));
            Assert.Equal("doubleDoors", WorldChecksum.NameOf(WorldChecksum.DoubleDoors));
            Assert.Equal("connectedDoors", WorldChecksum.NameOf(WorldChecksum.ConnectedDoors));
            Assert.Equal("slidingDoors", WorldChecksum.NameOf(WorldChecksum.SlidingDoors));
            Assert.Equal(4, WorldChecksum.CategoryCount);
            Assert.Equal("cat9", WorldChecksum.NameOf(9));
        }
    }
}
