using SyncRADation.Players;
using Xunit;

namespace SyncRADation.Tests
{
    public class SnapClockTests
    {
        [Fact]
        public void Jittered_arrivals_get_near_even_stamps()
        {
            var c = new SnapClock(0.04f);
            // 40 ms cadence arriving on 16 ms frame boundaries: 32 / 48 ms alternation plus a late one.
            float[] arrivals = { 0f, 0.048f, 0.080f, 0.128f, 0.160f, 0.208f, 0.256f, 0.272f, 0.320f, 0.368f };
            float prev = c.Stamp(arrivals[0]);
            float minGap = float.MaxValue, maxGap = 0f;
            for (int i = 1; i < arrivals.Length; i++)
            {
                float s = c.Stamp(arrivals[i]);
                float gap = s - prev;
                if (gap < minGap) minGap = gap;
                if (gap > maxGap) maxGap = gap;
                prev = s;
            }
            // Raw arrival gaps span 16..48 ms; stamped gaps stay close to the 40 ms cadence.
            Assert.InRange(minGap, 0.033f, 0.047f);
            Assert.InRange(maxGap, 0.033f, 0.047f);
        }

        [Fact]
        public void Stamps_are_strictly_increasing_for_bursts()
        {
            var c = new SnapClock(0.04f);
            float prev = c.Stamp(1f);
            for (int i = 0; i < 5; i++)
            {
                float s = c.Stamp(1f); // several packets in one frame
                Assert.True(s > prev);
                prev = s;
            }
        }

        [Fact]
        public void Long_gap_resyncs_to_arrival()
        {
            var c = new SnapClock(0.04f);
            c.Stamp(0f);
            c.Stamp(0.04f);
            Assert.Equal(2.5f, c.Stamp(2.5f)); // sender paused (inventory / load)
        }

        [Fact]
        public void Tracks_arrival_without_drift()
        {
            var c = new SnapClock(1f / 30f); // nominal 30 Hz, real 25 Hz
            float s = 0f;
            for (int i = 0; i < 200; i++)
                s = c.Stamp(i * 0.04f);
            Assert.InRange(199 * 0.04f - s, -0.01f, 0.01f);
            Assert.InRange(c.Interval, 0.038f, 0.042f);
        }

        /// <summary>
        /// Sender at a fixed cadence (plus small network jitter); the receiver handles packets once per frame, so at
        /// a frame rate below the send rate several packets arrive with the same timestamp. The learned interval
        /// must stay the send cadence and the stream must not keep resyncing.
        /// </summary>
        [Theory]
        [InlineData(30f, 15f)]
        [InlineData(30f, 20f)]
        [InlineData(30f, 60f)]
        [InlineData(25f, 15f)]
        [InlineData(25f, 20f)]
        [InlineData(25f, 60f)]
        public void Slow_receiver_learns_the_send_cadence(float sendHz, float receiverFps)
        {
            float send = 1f / sendHz;
            float frame = 1f / receiverFps;
            var c = new SnapClock(1f / 30f);
            var rng = new System.Random(1234);
            const float warmup = 2f;
            int resyncsAtWarmup = -1;
            float worst = 0f;
            for (int i = 0; i < (int)(12f * sendHz); i++)
            {
                float sentAt = i * send;
                float lands = sentAt + 0.004f + (float)rng.NextDouble() * 0.006f;
                float arrival = (float)System.Math.Ceiling(lands / frame) * frame; // handled on the next receiver frame
                float stamp = c.Stamp(arrival);
                if (sentAt < warmup) continue;
                if (resyncsAtWarmup < 0) resyncsAtWarmup = c.Resyncs;
                float err = System.Math.Abs(arrival - stamp);
                if (err > worst) worst = err;
            }
            Assert.InRange(c.Interval, send * 0.92f, send * 1.08f);
            Assert.Equal(resyncsAtWarmup, c.Resyncs);
            Assert.True(worst < SnapClock.ResyncWindow, "stamp drifted " + worst + " s from arrival");
        }
    }
}
