// Pure WorldId-set checksum + diff helpers (no Unity / MelonLoader references: compiled into the unit tests).
// WorldRegistry hashes the sorted WorldIds of every registered category once per Rebuild; peers exchange
// (count, checksum) per category in SceneHello / SceneFollow and diff the id lists on a mismatch.
using System;
using System.Collections.Generic;
using System.Text;

namespace SyncRADation.Sync
{
    public static class WorldChecksum
    {
        // Category indexes: wire order, frozen with the protocol (a new category is appended and bumps the protocol).
        public const int Enemies = 0;
        public const int DoubleDoors = 1;
        public const int ConnectedDoors = 2;
        public const int SlidingDoors = 3;
        public const int CategoryCount = 4;

        private static readonly string[] Names = { "enemies", "doubleDoors", "connectedDoors", "slidingDoors" };

        public static string NameOf(int category) =>
            category >= 0 && category < Names.Length ? Names[category] : "cat" + category;

        private const ulong Offset = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        /// <summary>
        /// FNV-1a 64 over the ids sorted ascending (8 bytes little-endian each), then the count (4 bytes):
        /// order-independent, an empty set differs from any non-empty one. Ids are unique registry keys.
        /// </summary>
        public static ulong Compute(ICollection<ulong> ids)
        {
            if (ids == null || ids.Count == 0) return Mix(Offset, 0);
            var sorted = new ulong[ids.Count];
            ids.CopyTo(sorted, 0);
            Array.Sort(sorted);
            return ComputeSorted(sorted);
        }

        /// <summary>Same as <see cref="Compute"/> for an already ascending list (no copy, no sort).</summary>
        public static ulong ComputeSorted(IList<ulong> sorted)
        {
            ulong h = Offset;
            int n = sorted != null ? sorted.Count : 0;
            for (int i = 0; i < n; i++)
            {
                ulong v = sorted[i];
                for (int b = 0; b < 8; b++)
                {
                    h ^= (byte)(v >> (b * 8));
                    h *= Prime;
                }
            }
            return Mix(h, n);
        }

        private static ulong Mix(ulong h, int v)
        {
            unchecked
            {
                for (int b = 0; b < 4; b++)
                {
                    h ^= (byte)(v >> (b * 8));
                    h *= Prime;
                }
            }
            return h;
        }

        /// <summary>One value for the whole registry (per-category sums folded in order). For dedupe keys / logs.</summary>
        public static ulong Combine(ulong[] sums)
        {
            ulong h = Offset;
            int n = sums != null ? sums.Length : 0;
            for (int i = 0; i < n; i++)
            {
                ulong v = sums[i];
                for (int b = 0; b < 8; b++)
                {
                    h ^= (byte)(v >> (b * 8));
                    h *= Prime;
                }
            }
            return h;
        }

        /// <summary>
        /// Bit i set = category i differs (count or checksum). 0 when either side has no data (null/empty) or they agree.
        /// Only the categories both sides report are compared.
        /// </summary>
        public static uint MismatchMask(int[] aCounts, ulong[] aSums, int[] bCounts, ulong[] bSums)
        {
            if (aCounts == null || aSums == null || bCounts == null || bSums == null) return 0;
            int n = Math.Min(Math.Min(aCounts.Length, aSums.Length), Math.Min(bCounts.Length, bSums.Length));
            if (n > 32) n = 32;
            uint mask = 0;
            for (int i = 0; i < n; i++)
            {
                if (aCounts[i] != bCounts[i] || aSums[i] != bSums[i])
                    mask |= 1u << i;
            }
            return mask;
        }

        /// <summary>"enemies 30/31! doubleDoors 12/12 ..." (a / b per category, "!" = mismatching).</summary>
        public static string Format(int[] aCounts, int[] bCounts, uint mask)
        {
            int n = Math.Min(aCounts != null ? aCounts.Length : 0, bCounts != null ? bCounts.Length : 0);
            var sb = new StringBuilder(96);
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(NameOf(i)).Append(' ').Append(aCounts[i]).Append('/').Append(bCounts[i]);
                if (i < 32 && (mask & (1u << i)) != 0) sb.Append('!');
            }
            return sb.ToString();
        }

        /// <summary>Lower bound of differing ids from counts alone (at least 1 per mismatching category).</summary>
        public static int EstimateDiffIds(int[] aCounts, int[] bCounts, uint mask)
        {
            int n = Math.Min(aCounts != null ? aCounts.Length : 0, bCounts != null ? bCounts.Length : 0);
            long total = 0;
            for (int i = 0; i < n && i < 32; i++)
            {
                if ((mask & (1u << i)) == 0) continue;
                total += Math.Max(1L, Math.Abs((long)aCounts[i] - bCounts[i]));
            }
            return total > int.MaxValue ? int.MaxValue : (int)total;
        }

        /// <summary>
        /// Ids in <paramref name="remote"/> that <paramref name="local"/> lacks (missing) and ids in local that remote lacks
        /// (extra), each capped at <paramref name="cap"/> entries; the totals count everything.
        /// </summary>
        public static void Diff(ICollection<ulong> local, ICollection<ulong> remote, int cap,
            List<ulong> missing, List<ulong> extra, out int missingTotal, out int extraTotal)
        {
            var localSet = new HashSet<ulong>(local ?? (ICollection<ulong>)Array.Empty<ulong>());
            var remoteSet = new HashSet<ulong>(remote ?? (ICollection<ulong>)Array.Empty<ulong>());
            var miss = new List<ulong>();
            var ext = new List<ulong>();
            foreach (ulong id in remoteSet)
                if (!localSet.Contains(id)) miss.Add(id);
            foreach (ulong id in localSet)
                if (!remoteSet.Contains(id)) ext.Add(id);
            miss.Sort(); // deterministic "first N" regardless of hash-set order
            ext.Sort();
            missingTotal = miss.Count;
            extraTotal = ext.Count;
            if (missing != null)
                for (int i = 0; i < miss.Count && missing.Count < cap; i++) missing.Add(miss[i]);
            if (extra != null)
                for (int i = 0; i < ext.Count && extra.Count < cap; i++) extra.Add(ext[i]);
        }

        /// <summary>Chunks needed for n ids (at least 1: an empty list still tells the peer the host has none).</summary>
        public static int ChunkCount(int n, int chunkSize)
        {
            if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
            return n <= 0 ? 1 : (n + chunkSize - 1) / chunkSize;
        }
    }
}
