using System;

namespace SkylineTogether.Core.Transport
{
    /// <summary>
    /// Bytes per second over the last second, in ten 100 ms buckets (the <c>OutBytesPerSecond</c> and
    /// <c>InBytesPerSecond</c> fields of <see cref="ConnectionStats"/> for the Core transports). Times are
    /// <see cref="IClock.Ticks"/>. Not thread-safe: the owner synchronises access.
    /// </summary>
    internal sealed class RateMeter
    {
        private const long BucketTicks = TimeSpan.TicksPerSecond / 10;
        private const int Buckets = 10;

        private readonly long[] _bytes = new long[Buckets];
        private readonly long[] _ids = new long[Buckets];

        public RateMeter()
        {
            for (int i = 0; i < Buckets; i++) _ids[i] = long.MinValue;
        }

        public void Add(long ticks, long bytes)
        {
            long id = ticks / BucketTicks;
            int slot = (int)(id % Buckets);
            if (slot < 0) slot += Buckets;
            if (_ids[slot] != id)
            {
                _ids[slot] = id;
                _bytes[slot] = 0;
            }
            _bytes[slot] += bytes;
        }

        /// <summary>Average rate over the second that ends at <paramref name="nowTicks"/>.</summary>
        public float Rate(long nowTicks)
        {
            long current = nowTicks / BucketTicks;
            long sum = 0;
            for (int i = 0; i < Buckets; i++)
            {
                long age = current - _ids[i];
                if (_ids[i] != long.MinValue && age >= 0 && age < Buckets) sum += _bytes[i];
            }
            return sum;
        }
    }
}
