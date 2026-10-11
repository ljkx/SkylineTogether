namespace SkylineTogether.Core
{
    /// <summary>
    /// Monotonic time source (IMPLEMENTATION-PLAN §3 rule 2: Core logic never reads <c>DateTime.Now</c>). Time never
    /// goes backwards. <see cref="Ticks"/> counts 100-nanosecond units (<c>TimeSpan.TicksPerSecond</c> per second) from
    /// an arbitrary origin, and <see cref="Seconds"/> is the same instant in seconds (<c>Ticks / 1e7</c>).
    /// Implementations: <c>Testing.ManualClock</c> (tests) and <c>Testing.StopwatchClock</c> (real time).
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public interface IClock
    {
        /// <summary>Monotonic time in 100 ns units.</summary>
        long Ticks { get; }

        /// <summary>Monotonic time in seconds.</summary>
        double Seconds { get; }
    }
}
