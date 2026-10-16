// Fixed-capacity, time-stamped snapshot buffer for the remote-player timelines (root pose, bone pose). Pure C# (tests).
namespace SyncRADation.Players
{
    /// <summary>Where a render time falls on a <see cref="SnapshotRing{T}"/>.</summary>
    public enum RingSample
    {
        /// <summary>No snapshots.</summary>
        Empty,
        /// <summary>At or before the oldest snapshot (or only one): hold index 0.</summary>
        BeforeOldest,
        /// <summary>Between two snapshots: blend lo -> hi by t.</summary>
        Between,
        /// <summary>At or after the newest snapshot: hold (or extrapolate from) the newest.</summary>
        AfterNewest
    }

    /// <summary>
    /// Oldest-first ring of snapshots with strictly increasing stamps (SnapClock). When full, <see cref="Push"/> hands
    /// back the oldest slot with its previous contents intact, so payloads holding pooled buffers are recycled instead
    /// of allocated per packet.
    /// </summary>
    public sealed class SnapshotRing<T>
    {
        private readonly float[] _times;
        private readonly T[] _items;
        private int _head;
        private int _count;

        public SnapshotRing(int capacity)
        {
            if (capacity < 1) capacity = 1;
            _times = new float[capacity];
            _items = new T[capacity];
        }

        public int Count => _count;
        public int Capacity => _items.Length;

        public void Clear()
        {
            _head = 0;
            _count = 0;
        }

        /// <summary>Slot i, oldest first (0 .. Count-1).</summary>
        public ref T At(int i) => ref _items[Slot(i)];

        public float TimeAt(int i) => _times[Slot(i)];

        /// <summary>Appends a snapshot stamped <paramref name="time"/> and returns its slot for the caller to fill.</summary>
        public ref T Push(float time)
        {
            int slot;
            if (_count < _items.Length)
            {
                slot = (_head + _count) % _items.Length;
                _count++;
            }
            else
            {
                slot = _head;
                _head = (_head + 1) % _items.Length;
            }
            _times[slot] = time;
            return ref _items[slot];
        }

        /// <summary>
        /// Brackets <paramref name="renderTime"/>: lo / hi are oldest-first indices, t the blend weight from lo to hi
        /// (0 outside the stamped span, where lo == hi is the held end).
        /// </summary>
        public RingSample Sample(float renderTime, out int lo, out int hi, out float t)
        {
            lo = hi = 0;
            t = 0f;
            if (_count == 0) return RingSample.Empty;
            if (_count == 1 || renderTime <= TimeAt(0)) return RingSample.BeforeOldest;
            int last = _count - 1;
            if (renderTime >= TimeAt(last))
            {
                lo = hi = last;
                return RingSample.AfterNewest;
            }
            hi = last;
            while (hi > 1 && TimeAt(hi - 1) > renderTime)
                hi--;
            lo = hi - 1;
            float a = TimeAt(lo);
            float span = TimeAt(hi) - a;
            t = span > 0.0001f ? (renderTime - a) / span : 1f;
            if (t < 0f) t = 0f;
            else if (t > 1f) t = 1f;
            return RingSample.Between;
        }

        private int Slot(int i) => (_head + i) % _items.Length;
    }
}
