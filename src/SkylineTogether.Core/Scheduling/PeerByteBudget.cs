using System;

namespace SkylineTogether.Core.Scheduling
{
    /// <summary>
    /// Byte budget of one low-priority stream to one peer (ARCH §4.8.2-4.8.4, playable subset): a <see cref="TokenBucket"/>
    /// plus a backlog guard. Nothing is offered while the peer's outbox and transport already hold more than
    /// <see cref="MaxBacklogBytes"/>, so attention values, cell maps and the rolling refresh never pile up behind a slow
    /// link (P0 traffic is not budgeted here and always goes first). <see cref="Boost"/> scales the rate temporarily
    /// (an infoview topic that asks for this stream). Not thread-safe.
    /// </summary>
    public sealed class PeerByteBudget
    {
        private readonly TokenBucket _bucket;
        private readonly int _baseRate;
        private readonly int _baseBurst;

        public PeerByteBudget(int bytesPerSecond, int burstBytes, long maxBacklogBytes)
        {
            if (maxBacklogBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBacklogBytes));
            _bucket = new TokenBucket(bytesPerSecond, burstBytes);
            _baseRate = bytesPerSecond;
            _baseBurst = burstBytes;
            MaxBacklogBytes = maxBacklogBytes;
        }

        public long MaxBacklogBytes { get; }

        /// <summary>Rate multiplier (1 = base rate; clamped to [0.1, 8]).</summary>
        public float Boost
        {
            get => _boost;
            set
            {
                float v = float.IsNaN(value) ? 1f : Math.Max(0.1f, Math.Min(8f, value));
                _boost = v;
                _bucket.RateBytesPerSecond = (int)Math.Min(int.MaxValue, _baseRate * (double)v);
                _bucket.BurstBytes = (int)Math.Min(int.MaxValue, _baseBurst * (double)Math.Max(1f, v));
            }
        }

        private float _boost = 1f;

        /// <summary>Refills for <paramref name="deltaSeconds"/> and returns the bytes that may be sent now (0 while the backlog is too large).</summary>
        public int Allowance(double deltaSeconds, long backlogBytes)
        {
            int available = _bucket.Take(deltaSeconds);
            if (backlogBytes > MaxBacklogBytes) return 0;
            return available;
        }

        /// <summary>Spends what was sent (may exceed the allowance by one record; the debt delays the next send).</summary>
        public void Spend(int bytes) => _bucket.Consume(bytes);

        public override string ToString() => _bucket.ToString();
    }
}
