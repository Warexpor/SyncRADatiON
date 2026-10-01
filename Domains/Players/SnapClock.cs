// Jitter-smoothed receive timestamps for a ~constant-rate snapshot stream (pose root, bones)
namespace SyncRADation.Players
{
    /// <summary>
    /// Snapshots carry no sender clock, so stamping them with the raw arrival time turns network and
    /// frame-boundary jitter (a 40 ms cadence arriving as 20 / 60 / 40 ms) straight into speed pulses.
    /// Stamp = previous stamp + the learned interval, pulled gently toward the arrival time; a gap or
    /// burst beyond <see cref="ResyncWindow"/> resyncs to the arrival time (pause, loading, packet loss).
    /// Pure C# so tests can drive it.
    /// </summary>
    public sealed class SnapClock
    {
        public const float ResyncWindow = 0.15f;
        const float Pull = 0.1f;
        const float IntervalAlpha = 0.05f;
        const float MinInterval = 0.01f;
        const float MaxInterval = 0.2f;

        float _last = -1f;
        float _lastArrival = -1f;
        float _interval;

        public SnapClock(float nominalInterval)
        {
            _interval = nominalInterval;
        }

        /// <summary>Learned packet interval (seconds).</summary>
        public float Interval => _interval;

        public void Reset()
        {
            _last = -1f;
            _lastArrival = -1f;
        }

        public float Stamp(float arrival)
        {
            if (_last < 0f)
            {
                _last = arrival;
                _lastArrival = arrival;
                return arrival;
            }
            float gap = arrival - _lastArrival;
            _lastArrival = arrival;
            if (gap >= MinInterval && gap <= MaxInterval)
                _interval += (gap - _interval) * IntervalAlpha;

            float expected = _last + _interval;
            float stamped = expected + (arrival - expected) * Pull;
            float err = arrival - stamped;
            if (err > ResyncWindow || err < -ResyncWindow)
                stamped = arrival;
            // Never stamp at or before the previous snapshot: interpolation needs increasing times.
            if (stamped <= _last + MinInterval * 0.5f)
                stamped = _last + MinInterval * 0.5f;
            _last = stamped;
            return stamped;
        }
    }
}
