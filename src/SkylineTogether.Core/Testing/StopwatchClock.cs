using System;
using System.Diagnostics;

namespace SkylineTogether.Core.Testing
{
    /// <summary>
    /// Real monotonic time from <see cref="Stopwatch"/>, starting at 0 when the clock is created (game-bootstrap's
    /// <c>ModRuntime.Clock</c>, network-thread timing, measurements). Thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class StopwatchClock : IClock
    {
        private static readonly long Frequency = Stopwatch.Frequency;
        private readonly long _origin = Stopwatch.GetTimestamp();

        public long Ticks
        {
            get
            {
                long elapsed = Stopwatch.GetTimestamp() - _origin;
                // split the conversion so elapsed * 1e7 never overflows (it would after about a day at 10 MHz)
                return elapsed / Frequency * TimeSpan.TicksPerSecond + elapsed % Frequency * TimeSpan.TicksPerSecond / Frequency;
            }
        }

        public double Seconds => (Stopwatch.GetTimestamp() - _origin) / (double)Frequency;
    }
}
