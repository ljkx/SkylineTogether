using System;

namespace SkylineTogether.Core.Scheduling
{
    /// <summary>
    /// The dirty buckets (1024 slots, <see cref="NetSlot.Bucket"/>) of one peer whose pages have been dirty for at least
    /// the first promotion quantum, oldest first (ARCH §4.8.4 staleness promotion: "a dirty page older than 2, 4, 8, 16 and
    /// 32 s is promoted one level"). Promotion levels are monotonic in age, so serving the highest level first is the same
    /// as serving the oldest page first. A bucket's age is the dirty age of its first dirty slot's page
    /// (<see cref="IPeerDirtyView.DirtyAgeSeconds"/>).
    /// <para>
    /// The index is rebuilt every <see cref="SchedulerConfig.StaleRescanSeconds"/>, incrementally (one probe per dirty
    /// bucket, bounded per frame), while the previous list stays in use; emission keeps a cursor (list position and slot)
    /// so a bucket larger than one frame's budget continues where it stopped. Single owner.
    /// </para>
    /// </summary>
    internal sealed class StaleBucketIndex
    {
        private int[] _buckets = new int[64];
        private double[] _since = new double[64];
        private int _count;

        private int[] _nextBuckets = new int[64];
        private double[] _nextSince = new double[64];
        private int _nextCount;
        private int _scanSlot;
        private bool _rebuilding;
        private double _rebuildStarted = double.NegativeInfinity;

        /// <summary>Position in the published list where emission continues.</summary>
        public int Position;

        /// <summary>Slot within the bucket at <see cref="Position"/> where emission continues (0 = the bucket's start).</summary>
        public int SlotCursor;

        /// <summary>Stale buckets in the published list.</summary>
        public int Count => _count;

        public int BucketAt(int index) => _buckets[index];

        /// <summary>The time the bucket at <paramref name="index"/> became dirty (seconds, the scheduler's clock).</summary>
        public double DirtySinceAt(int index) => _since[index];

        /// <summary>
        /// Advances the rebuild. <paramref name="visits"/> is the frame's remaining inspection budget; every probe uses one.
        /// </summary>
        public void Step(IPeerDirtyView dirty, double now, double minAgeSeconds, double rescanSeconds, ref int visits)
        {
            if (!_rebuilding)
            {
                if (now - _rebuildStarted < rescanSeconds && now >= _rebuildStarted) return;
                _rebuilding = true;
                _rebuildStarted = now;
                _nextCount = 0;
                _scanSlot = 1;
            }
            while (visits > 0)
            {
                int s = dirty.NextDirtySlot(_scanSlot);
                visits--;
                if (s < _scanSlot || s <= 0 || s > NetSlot.MaxValue)
                {
                    Publish();
                    return;
                }
                int bucket = s >> NetSlot.BucketShift;
                double age = dirty.DirtyAgeSeconds(new NetSlot((uint)s));
                if (age >= minAgeSeconds && !double.IsInfinity(age)) Append(bucket, now - age);
                long next = (long)(bucket + 1) << NetSlot.BucketShift;
                if (next > NetSlot.MaxValue)
                {
                    Publish();
                    return;
                }
                _scanSlot = (int)next;
            }
        }

        /// <summary>Forces the next <see cref="Step"/> to start a rebuild (after a peer reset).</summary>
        public void Invalidate()
        {
            _rebuilding = false;
            _rebuildStarted = double.NegativeInfinity;
        }

        public void Clear()
        {
            _count = 0;
            _nextCount = 0;
            Position = 0;
            SlotCursor = 0;
            Invalidate();
        }

        private void Append(int bucket, double since)
        {
            if (_nextCount == _nextBuckets.Length)
            {
                Array.Resize(ref _nextBuckets, _nextCount * 2);
                Array.Resize(ref _nextSince, _nextCount * 2);
            }
            _nextBuckets[_nextCount] = bucket;
            _nextSince[_nextCount] = since;
            _nextCount++;
        }

        private void Publish()
        {
            // oldest first; equal times by bucket (Array.Sort is not stable, the key pair makes the order deterministic)
            Array.Sort(_nextSince, _nextBuckets, 0, _nextCount);
            for (int i = 1; i < _nextCount; i++)
            {
                if (_nextSince[i] == _nextSince[i - 1] && _nextBuckets[i] < _nextBuckets[i - 1])
                {
                    int j = i;
                    while (j > 0 && _nextSince[j] == _nextSince[j - 1] && _nextBuckets[j] < _nextBuckets[j - 1])
                    {
                        int b = _nextBuckets[j]; _nextBuckets[j] = _nextBuckets[j - 1]; _nextBuckets[j - 1] = b;
                        j--;
                    }
                }
            }
            int[] buckets = _buckets; double[] since = _since;
            _buckets = _nextBuckets; _since = _nextSince; _count = _nextCount;
            _nextBuckets = buckets; _nextSince = since; _nextCount = 0;
            _rebuilding = false;
            Position = 0;
            SlotCursor = 0;
        }
    }
}
