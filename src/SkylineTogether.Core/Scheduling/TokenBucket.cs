using System;
using System.Globalization;

namespace SkylineTogether.Core.Scheduling
{
    /// <summary>
    /// A byte token bucket (ARCH §4.8.4: <c>budget = tokenBucket.Take(frameDt)</c>). <see cref="Take"/> refills
    /// <see cref="RateBytesPerSecond"/> × elapsed seconds, up to <see cref="BurstBytes"/>, and returns what may be spent
    /// now without removing it; <c>Consume</c> spends. Spending may drive the balance negative: P0 is accounted,
    /// never refused, and the debt then delays the lower priorities until the link has carried it. Tokens are kept as a
    /// double, so no fraction of a byte is lost at high frame rates (48 000 B/s at 144 fps is 333.3 bytes per frame).
    /// Not thread-safe: one owner (the peer's scheduler state).
    /// </summary>
    public sealed class TokenBucket
    {
        /// <summary>Debt beyond this is not tracked (keeps every conversion to int and every sum finite).</summary>
        private const double MaxDebt = 1e12;

        private double _tokens;
        private int _rate;
        private int _burst;

        /// <param name="bytesPerSecond">Refill rate (bytes per second, at least 0).</param>
        /// <param name="burstBytes">Bucket depth (bytes, at least 0); the bucket starts full.</param>
        public TokenBucket(int bytesPerSecond, int burstBytes)
        {
            if (bytesPerSecond < 0) throw new ArgumentOutOfRangeException(nameof(bytesPerSecond), bytesPerSecond, "the rate must not be negative");
            if (burstBytes < 0) throw new ArgumentOutOfRangeException(nameof(burstBytes), burstBytes, "the burst must not be negative");
            _rate = bytesPerSecond;
            _burst = burstBytes;
            _tokens = burstBytes;
        }

        /// <summary>Refill rate in bytes per second; negative values are stored as 0.</summary>
        public int RateBytesPerSecond
        {
            get => _rate;
            set => _rate = value < 0 ? 0 : value;
        }

        /// <summary>Bucket depth in bytes; negative values are stored as 0. A lower depth caps the balance at the next
        /// <see cref="Take"/>.</summary>
        public int BurstBytes
        {
            get => _burst;
            set => _burst = value < 0 ? 0 : value;
        }

        /// <summary>The current balance in whole bytes, negative while P0 debt is outstanding.</summary>
        public int Available
        {
            get
            {
                double floor = Math.Floor(_tokens);
                if (floor >= int.MaxValue) return int.MaxValue;
                if (floor <= int.MinValue) return int.MinValue;
                return (int)floor;
            }
        }

        /// <summary>The exact balance (fractional bytes; diagnostics and tests).</summary>
        public double Balance => _tokens;

        /// <summary>
        /// Refills for <paramref name="deltaSeconds"/> (non-finite or negative values add nothing) and returns the bytes
        /// that may be spent now: the balance floored at 0. Nothing is removed; spend with <c>Consume</c>.
        /// </summary>
        public int Take(double deltaSeconds)
        {
            if (deltaSeconds > 0 && !double.IsInfinity(deltaSeconds))
            {
                _tokens += _rate * deltaSeconds;
            }
            if (_tokens > _burst) _tokens = _burst;
            return Available > 0 ? Available : 0;
        }

        /// <summary>Spends <paramref name="bytes"/> (values of 0 or less are ignored); the balance may become negative.</summary>
        public void Consume(int bytes)
        {
            if (bytes <= 0) return;
            _tokens -= bytes;
            if (_tokens < -MaxDebt) _tokens = -MaxDebt;
        }

        /// <summary>Spends a fractional amount (the scheduler charges bytes scaled by the measured wire ratio).</summary>
        public void Consume(double bytes)
        {
            if (!(bytes > 0) || double.IsInfinity(bytes)) return;
            _tokens -= bytes;
            if (_tokens < -MaxDebt) _tokens = -MaxDebt;
        }

        /// <summary>
        /// Limits the debt to <paramref name="maxDebtBytes"/>: a debt larger than the bytes that are really still queued
        /// in front of us (outbox plus transport) is fictitious, because the link has already carried the rest.
        /// </summary>
        public void LimitDebt(long maxDebtBytes)
        {
            if (maxDebtBytes < 0) maxDebtBytes = 0;
            double floor = -Math.Min((double)maxDebtBytes, MaxDebt);
            if (_tokens < floor) _tokens = floor;
        }

        public override string ToString()
        {
            return "bucket " + Available.ToString(CultureInfo.InvariantCulture) + " B of " + _burst.ToString(CultureInfo.InvariantCulture)
                   + " B at " + _rate.ToString(CultureInfo.InvariantCulture) + " B/s";
        }
    }
}
