using System;
using System.Threading;

namespace SkylineTogether.Core.Testing
{
    /// <summary>
    /// A clock that moves only when told to (tests and replays). Monotonic like every <see cref="IClock"/>: moving it
    /// backwards throws. Thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class ManualClock : IClock
    {
        private long _ticks;

        public ManualClock() : this(0)
        {
        }

        public ManualClock(double startSeconds)
        {
            _ticks = ToTicks(startSeconds, nameof(startSeconds));
        }

        public long Ticks => Interlocked.Read(ref _ticks);

        public double Seconds => Ticks / (double)TimeSpan.TicksPerSecond;

        /// <summary>Moves the clock forward by <paramref name="seconds"/> (zero or more).</summary>
        public void Advance(double seconds)
        {
            long delta = ToTicks(seconds, nameof(seconds));
            Interlocked.Add(ref _ticks, delta);
        }

        /// <summary>Sets the clock to <paramref name="seconds"/>, which must not be earlier than now.</summary>
        public void Set(double seconds)
        {
            long target = ToTicks(seconds, nameof(seconds));
            while (true)
            {
                long current = Interlocked.Read(ref _ticks);
                if (target < current) throw new ArgumentOutOfRangeException(nameof(seconds), "a monotonic clock cannot go backwards");
                if (Interlocked.CompareExchange(ref _ticks, target, current) == current) return;
            }
        }

        private static long ToTicks(double seconds, string name)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0 || seconds > 1e9)
            {
                throw new ArgumentOutOfRangeException(name, seconds, "expected a finite, non-negative number of seconds");
            }
            return (long)Math.Round(seconds * TimeSpan.TicksPerSecond);
        }
    }
}
