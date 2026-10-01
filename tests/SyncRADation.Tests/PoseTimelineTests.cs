using System;
using SyncRADation.Players;
using UnityEngine;
using Xunit;

namespace SyncRADation.Tests
{
    /// <summary>The remote-player snapshot timeline (root motion + bone pose) and its quaternion blend.</summary>
    public class PoseTimelineTests
    {
        private static SnapshotRing<int> Ring(params float[] times)
        {
            var r = new SnapshotRing<int>(8);
            for (int i = 0; i < times.Length; i++)
                r.Push(times[i]) = i;
            return r;
        }

        [Fact]
        public void Empty_ring_samples_empty()
        {
            Assert.Equal(RingSample.Empty, new SnapshotRing<int>(4).Sample(1f, out _, out _, out _));
        }

        [Fact]
        public void Before_the_oldest_or_with_one_snapshot_holds_the_oldest()
        {
            Assert.Equal(RingSample.BeforeOldest, Ring(1f).Sample(5f, out int lo, out int hi, out float t));
            Assert.Equal((0, 0, 0f), (lo, hi, t));
            Assert.Equal(RingSample.BeforeOldest, Ring(1f, 2f).Sample(0.5f, out lo, out hi, out _));
            Assert.Equal((0, 0), (lo, hi));
            Assert.Equal(RingSample.BeforeOldest, Ring(1f, 2f).Sample(1f, out _, out _, out _));
        }

        [Fact]
        public void At_or_after_the_newest_holds_the_newest()
        {
            var r = Ring(1f, 2f, 3f);
            Assert.Equal(RingSample.AfterNewest, r.Sample(3f, out int lo, out int hi, out float t));
            Assert.Equal((2, 2, 0f), (lo, hi, t));
            Assert.Equal(RingSample.AfterNewest, r.Sample(9f, out lo, out _, out _));
            Assert.Equal(2, r.At(lo));
        }

        [Theory]
        [InlineData(1.5f, 0, 1, 0.5f)]
        [InlineData(2f, 1, 2, 0f)]
        [InlineData(2.25f, 1, 2, 0.25f)]
        [InlineData(3.9f, 2, 3, 0.9f)]
        public void Between_brackets_the_two_neighbours(float render, int expLo, int expHi, float expT)
        {
            var r = Ring(1f, 2f, 3f, 4f);
            Assert.Equal(RingSample.Between, r.Sample(render, out int lo, out int hi, out float t));
            Assert.Equal((expLo, expHi), (lo, hi));
            Assert.Equal(expT, t, 4);
            Assert.True(r.TimeAt(lo) <= render && render < r.TimeAt(hi));
        }

        [Fact]
        public void Full_ring_recycles_the_oldest_slot_with_its_payload()
        {
            var r = new SnapshotRing<int[]>(3);
            var bufs = new int[3][];
            for (int i = 0; i < 3; i++)
            {
                ref int[] slot = ref r.Push(i);
                slot = new int[] { i };
                bufs[i] = slot;
            }
            ref int[] reused = ref r.Push(3f);
            Assert.Same(bufs[0], reused); // the caller can refill the old buffer instead of allocating
            Assert.Equal(3, r.Count);
            Assert.Equal(new[] { 1f, 2f, 3f }, new[] { r.TimeAt(0), r.TimeAt(1), r.TimeAt(2) });
            Assert.Same(bufs[1], r.At(0));
        }

        [Fact]
        public void Wrapped_ring_still_samples_in_time_order()
        {
            var r = new SnapshotRing<int>(4);
            for (int i = 0; i < 11; i++)
                r.Push(i) = i;
            Assert.Equal(RingSample.Between, r.Sample(8.5f, out int lo, out int hi, out float t));
            Assert.Equal((8, 9, 0.5f), (r.At(lo), r.At(hi), t));
            Assert.Equal(RingSample.BeforeOldest, r.Sample(7f, out lo, out _, out _));
            Assert.Equal(7, r.At(lo));
        }

        [Fact]
        public void Clear_empties_the_ring()
        {
            var r = Ring(1f, 2f);
            r.Clear();
            Assert.Equal(0, r.Count);
            Assert.Equal(RingSample.Empty, r.Sample(1.5f, out _, out _, out _));
            r.Push(5f) = 42;
            Assert.Equal(42, r.At(0));
        }

        private static Quaternion Yaw(float degrees)
        {
            double h = degrees * Math.PI / 360.0;
            return new Quaternion(0f, (float)Math.Sin(h), 0f, (float)Math.Cos(h));
        }

        [Fact]
        public void Nlerp_hits_the_endpoints_and_stays_normalized()
        {
            Quaternion a = Yaw(10f), b = Yaw(70f);
            Quaternion q0 = PoseMath.Nlerp(a, b, 0f), q1 = PoseMath.Nlerp(a, b, 1f), qm = PoseMath.Nlerp(a, b, 0.5f);
            Assert.Equal(a.y, q0.y, 5);
            Assert.Equal(b.y, q1.y, 5);
            Assert.Equal(1f, qm.x * qm.x + qm.y * qm.y + qm.z * qm.z + qm.w * qm.w, 5);
            // Symmetric pair: the midpoint is the 40-degree yaw.
            Assert.Equal(Yaw(40f).y, qm.y, 4);
        }

        [Fact]
        public void Nlerp_takes_the_short_way_across_the_double_cover()
        {
            Quaternion a = Yaw(10f);
            Quaternion bNeg = Yaw(30f);
            bNeg = new Quaternion(-bNeg.x, -bNeg.y, -bNeg.z, -bNeg.w); // same rotation, opposite hemisphere
            Quaternion m = PoseMath.Nlerp(a, bNeg, 0.5f);
            if (m.w < 0f) m = new Quaternion(-m.x, -m.y, -m.z, -m.w);
            Assert.Equal(Yaw(20f).y, m.y, 4);
            Assert.Equal(Yaw(20f).w, m.w, 4);
        }

        [Fact]
        public void Nlerp_of_opposite_quaternions_falls_back_to_the_target()
        {
            var a = new Quaternion(0f, 0f, 0f, 1f);
            var b = new Quaternion(0f, 0f, 0f, -1f);
            // dot < 0 flips b, so the blend is a itself (same rotation), never a zero-length quaternion.
            Quaternion m = PoseMath.Nlerp(a, b, 0.5f);
            Assert.Equal(1f, Math.Abs(m.w), 5);
        }
    }
}
