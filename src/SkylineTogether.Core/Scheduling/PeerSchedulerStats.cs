namespace SkylineTogether.Core.Scheduling
{
    /// <summary>
    /// One peer's scheduling figures for the HUD and network panel (ARCH §13.2: "staleness per class, budgets, bytes per
    /// stream"). The first block is the contract of IMPLEMENTATION-PLAN §7.7; the rest is additive detail. Rates are
    /// smoothed over about two seconds; staleness is the dirty age of values when they were scheduled, over the last 10
    /// to 20 seconds.
    /// </summary>
    public struct PeerSchedulerStats
    {
        /// <summary>The token-bucket rate: min(link budget, host share).</summary>
        public int BudgetBytesPerSecond;

        public int P0BytesPerSecond, P1BytesPerSecond, P2BytesPerSecond, P3BytesPerSecond;

        /// <summary>Staleness of every value scheduled for the peer (P1 and P3).</summary>
        public double StalenessP50Seconds, StalenessP95Seconds;

        public MotionQuality Motion;

        public float MotionRadiusScale;

        // ---------- additive ----------

        /// <summary>The link estimator's budget (ARCH §4.8.2), before the host share.</summary>
        public int LinkBudgetBytesPerSecond;

        /// <summary>The peer's share of the host's total upload cap.</summary>
        public int HostShareBytesPerSecond;

        /// <summary>Staleness of attention values (P1) and of the rolling refresh (P3).</summary>
        public double P1StalenessP95Seconds, P3StalenessP95Seconds;

        /// <summary>The degradation ladder's step for the peer (ARCH §4.8.5).</summary>
        public DegradationStep Ladder;

        /// <summary>P0 backlog in seconds of the peer's link (outbox plus the transport's pending reliable bytes).</summary>
        public double P0BacklogSeconds;

        /// <summary>The backlog has stayed above the threshold long enough (automatic speed cap input).</summary>
        public bool P0Overloaded;

        /// <summary>The outbox's reliable window was open in the last frame (otherwise no P1/P3 section was built).</summary>
        public bool ReliableWindowOpen;

        /// <summary>More was waiting than the budget allowed in the last frame.</summary>
        public bool Backlogged;

        /// <summary>The measured ratio of wire bytes to scheduled bytes (compression), 1 when unknown.</summary>
        public double WireRatio;

        /// <summary>Stale dirty buckets (dirty for at least the first promotion quantum) in the last index rebuild.</summary>
        public int StaleBuckets;
    }
}
