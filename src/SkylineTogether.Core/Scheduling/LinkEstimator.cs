using System;
using System.Globalization;

namespace SkylineTogether.Core.Scheduling
{
    /// <summary>
    /// Per-peer budget from link statistics (ARCH §4.8.2, §9.5). The ceiling is
    /// <c>min(userCap, SendRateFraction × sendRate)</c>, where the send rate is Steam's own estimate
    /// (<c>m_nSendRateBytesPerSecond</c> of <c>GetConnectionRealTimeStatus</c>, R06 §4.1); the budget then follows AIMD
    /// on the transport's queue time (<c>m_usecQueueTime</c>): above <see cref="SchedulerConfig.QueueHighMs"/> it is
    /// multiplied by <see cref="SchedulerConfig.DecreaseFactor"/>, at most once per hold time (the larger of
    /// <see cref="SchedulerConfig.DecreaseHoldSeconds"/> and two round trips, so the same congestion episode, seen in
    /// every poll until the queue drains, is not punished sixty times a second); below
    /// <see cref="SchedulerConfig.QueueLowMs"/> while the peer is backlogged it grows by
    /// <see cref="SchedulerConfig.IncreasePerSecond"/> per second (multiplicatively). The budget always stays between the
    /// floor (<see cref="SchedulerConfig.FloorBytesPerSecond"/>, but never above the user's cap) and the ceiling.
    /// <para>
    /// Unknown inputs: a user cap of 0 or less means "no user cap"; a send rate of 0 or less (no statistics yet, test
    /// transports) means "unknown", and the ceiling is then the user cap, or
    /// <see cref="SchedulerConfig.UnknownLinkBytesPerSecond"/> when that is unknown too. Not thread-safe (one owner).
    /// </para>
    /// </summary>
    public sealed class LinkEstimator
    {
        private readonly SchedulerSettings _s;
        private double _budget;
        private bool _initialized;
        private double _holdRemaining;

        public LinkEstimator(SchedulerConfig config)
        {
            _s = SchedulerSettings.From(config);
        }

        internal LinkEstimator(SchedulerSettings settings)
        {
            _s = settings ?? SchedulerSettings.From(null);
        }

        /// <summary>The budget after the last <see cref="Update"/> (bytes per second); 0 before the first update.</summary>
        public int CurrentBudget => _initialized ? ToInt(_budget) : 0;

        /// <summary>The ceiling computed by the last update (diagnostics).</summary>
        public int Ceiling { get; private set; }

        /// <summary>The floor applied by the last update (diagnostics).</summary>
        public int Floor { get; private set; }

        /// <summary>The queue time seen by the last update, in milliseconds.</summary>
        public double QueueTimeMs { get; private set; }

        /// <summary>Multiplicative decreases applied so far (diagnostics).</summary>
        public int Decreases { get; private set; }

        /// <summary>
        /// Feeds one statistics sample. Call it every frame or at the polling cadence (100 ms); the AIMD steps are scaled
        /// by <paramref name="deltaSeconds"/> and rate-limited, so the cadence does not change the outcome.
        /// </summary>
        /// <param name="stats">The connection's latest real-time statistics.</param>
        /// <param name="userCapBytesPerSecond">The session's per-peer cap; 0 or less = none.</param>
        /// <param name="backlogged">The scheduler had more to send than the budget allowed (growth only happens then).</param>
        /// <param name="deltaSeconds">Seconds since the previous update (non-finite or negative = 0, capped at 1 s).</param>
        /// <returns>The new budget in bytes per second.</returns>
        public int Update(in ConnectionStats stats, int userCapBytesPerSecond, bool backlogged, double deltaSeconds)
        {
            double dt = deltaSeconds > 0 && !double.IsInfinity(deltaSeconds) ? Math.Min(deltaSeconds, 1.0) : 0.0;
            double userCap = userCapBytesPerSecond > 0 ? userCapBytesPerSecond : double.PositiveInfinity;

            double ceiling;
            if (stats.SendRateBytesPerSecond > 0)
            {
                ceiling = Math.Min(userCap, _s.SendRateFraction * stats.SendRateBytesPerSecond);
            }
            else
            {
                ceiling = double.IsInfinity(userCap) ? _s.UnknownLinkBytesPerSecond : userCap;
            }
            double floor = Math.Min(_s.FloorBytesPerSecond, userCap);
            if (ceiling < floor) ceiling = floor;

            if (!_initialized)
            {
                _budget = ceiling;
                _initialized = true;
            }

            double queueMs = stats.QueueTimeMicroseconds > 0 ? stats.QueueTimeMicroseconds / 1000.0 : 0.0;
            QueueTimeMs = queueMs;
            if (_holdRemaining > 0) _holdRemaining -= dt;

            if (queueMs > _s.QueueHighMs)
            {
                if (_holdRemaining <= 0)
                {
                    _budget *= _s.DecreaseFactor;
                    double rtt = stats.PingMs > 0 ? stats.PingMs / 1000.0 : 0.0;
                    _holdRemaining = Math.Max(_s.DecreaseHoldSeconds, 2.0 * rtt);
                    Decreases++;
                }
            }
            else if (queueMs < _s.QueueLowMs && backlogged && dt > 0)
            {
                _budget *= 1.0 + _s.IncreasePerSecond * dt;
            }

            if (_budget > ceiling) _budget = ceiling;
            if (_budget < floor) _budget = floor;
            Ceiling = ToInt(ceiling);
            Floor = ToInt(floor);
            return ToInt(_budget);
        }

        /// <summary>Forgets the history (a reconnected peer starts again at its ceiling).</summary>
        public void Reset()
        {
            _initialized = false;
            _budget = 0;
            _holdRemaining = 0;
            Decreases = 0;
        }

        public override string ToString()
        {
            return "link " + CurrentBudget.ToString(CultureInfo.InvariantCulture) + " B/s (ceiling " + Ceiling.ToString(CultureInfo.InvariantCulture)
                   + ", queue " + QueueTimeMs.ToString("0.#", CultureInfo.InvariantCulture) + " ms)";
        }

        private static int ToInt(double value)
        {
            if (double.IsNaN(value) || value <= 0) return 0;
            return value >= int.MaxValue ? int.MaxValue : (int)value;
        }
    }
}
