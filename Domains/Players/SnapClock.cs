// Jitter-smoothed receive timestamps for a ~constant-rate snapshot stream (pose root, bones)
namespace SyncRADation.Players
{
    /// <summary>
    /// Snapshots carry no sender clock, so stamping them with the raw arrival time turns network and
    /// frame-boundary jitter (a 40 ms cadence arriving as 20 / 60 / 40 ms) straight into speed pulses.
    /// Stamp = previous stamp + the learned interval, pulled gently toward the arrival time; a gap or
    /// burst beyond <see cref="ResyncWindow"/> resyncs to the arrival time (pause, loading, packet loss).
    /// The interval is learned as elapsed time / packet count over a window, so a receiver slower than the
    /// send rate (two packets handled in one 15 fps frame: gaps 0 / 67 / 0 / 67 ms) still learns the real
    /// cadence instead of only the long gaps. Pure C# so tests can drive it.
    /// </summary>
    public sealed class SnapClock
    {
        public const float ResyncWindow = 0.15f;
        const float Pull = 0.1f;
        const float MinInterval = 0.01f;
        const float MaxInterval = 0.2f;
        /// <summary>Elapsed arrival time one interval sample spans (several packets, several receiver frames).</summary>
        const float WindowSeconds = 0.5f;
        const int WindowMinPackets = 4;
        const float WindowAlpha = 0.3f;

        float _last = -1f;
        float _lastArrival = -1f;
        float _interval;
        float _winStart = -1f;
        int _winCount;

        public SnapClock(float nominalInterval)
        {
            _interval = nominalInterval;
        }

        /// <summary>Learned packet interval (seconds).</summary>
        public float Interval => _interval;

        /// <summary>Stamps that snapped to the raw arrival time because the smoothed stamp drifted too far.</summary>
        public int Resyncs { get; private set; }

        public void Reset()
        {
            _last = -1f;
            _lastArrival = -1f;
            _winStart = -1f;
            _winCount = 0;
        }

        public float Stamp(float arrival)
        {
            if (_last < 0f)
            {
                _last = arrival;
                _lastArrival = arrival;
                _winStart = arrival;
                _winCount = 0;
                return arrival;
            }
            float gap = arrival - _lastArrival;
            _lastArrival = arrival;
            LearnInterval(arrival, gap);

            float expected = _last + _interval;
            float stamped = expected + (arrival - expected) * Pull;
            float err = arrival - stamped;
            if (err > ResyncWindow || err < -ResyncWindow)
            {
                stamped = arrival;
                Resyncs++;
            }
            // Never stamp at or before the previous snapshot: interpolation needs increasing times.
            if (stamped <= _last + MinInterval * 0.5f)
                stamped = _last + MinInterval * 0.5f;
            _last = stamped;
            return stamped;
        }

        void LearnInterval(float arrival, float gap)
        {
            // A pause / load gap is not cadence: restart the window after it.
            if (gap > MaxInterval || gap < 0f)
            {
                _winStart = arrival;
                _winCount = 0;
                return;
            }
            _winCount++;
            float span = arrival - _winStart;
            if (span < WindowSeconds || _winCount < WindowMinPackets) return;
            float sample = span / _winCount;
            if (sample < MinInterval) sample = MinInterval;
            else if (sample > MaxInterval) sample = MaxInterval;
            _interval += (sample - _interval) * WindowAlpha;
            _winStart = arrival;
            _winCount = 0;
        }
    }
}
