namespace SkylineTogether.Core.Scheduling
{
    /// <summary>
    /// Tunables of budgets and attention (ARCH §4.8, Appendix D). The first block is the contract of
    /// IMPLEMENTATION-PLAN §7.7; the fields after it are additive tunables of this implementation, each with the default
    /// that the design implies. Consumers (<see cref="LinkEstimator"/>, <c>AttentionSets</c>,
    /// <c>PeerScheduler</c>, <see cref="DegradationController"/>) copy and sanitise the values when they are
    /// constructed, so changing a field afterwards has no effect on an existing instance, and invalid values (NaN,
    /// negative shares, an unsorted quanta list) fall back to the defaults instead of failing at run time.
    /// </summary>
    public sealed class SchedulerConfig
    {
        // ---------- contract (IMPLEMENTATION-PLAN §7.7) ----------

        /// <summary>Shares of what remains after P0 (ARCH §4.8.4): attention values, motion, rolling refresh. Work-conserving.</summary>
        public double P1Share = 0.40, P2Share = 0.45, P3Share = 0.15, MinP1ShareAfterP0 = 0.30;

        /// <summary>The per-peer budget never drops below this (or below the user's own cap, if that is lower).</summary>
        public int FloorBytesPerSecond = 48_000;

        /// <summary>AIMD on Steam's queue time (ARCH §4.8.2): above <see cref="QueueHighMs"/> the budget is multiplied by
        /// <see cref="DecreaseFactor"/>; below <see cref="QueueLowMs"/> while backlogged it grows by
        /// <see cref="IncreasePerSecond"/> per second. The ceiling is min(user cap, <see cref="SendRateFraction"/> × send rate).</summary>
        public double QueueHighMs = 150, QueueLowMs = 30, DecreaseFactor = 0.8, IncreasePerSecond = 0.05, SendRateFraction = 0.85;

        /// <summary>A dirty page older than each quantum is promoted one level (ARCH §4.8.4); ascending seconds.</summary>
        public double[] PromotionQuantaSeconds = { 2, 4, 8, 16, 32 };

        /// <summary>A peer is P0-overloaded when its P0 backlog (outbox plus Steam's pending reliable bytes, in seconds of
        /// its link) stays above <see cref="P0BacklogSeconds"/> for <see cref="P0OverloadSeconds"/> (ARCH §4.8.5).</summary>
        public double P0OverloadSeconds = 30, P0BacklogSeconds = 2;

        /// <summary>Motion interest disc: radius = clamp(<see cref="MotionRadiusPerZoom"/> × zoom, min, max), left at
        /// <see cref="LeaveFactor"/> × radius (ARCH §4.8.3 item 1, R15 §7.1).</summary>
        public float MinMotionRadius = 300, MaxMotionRadius = 2500, MotionRadiusPerZoom = 1.0f, LeaveFactor = 1.25f;

        // ---------- budget tunables (additive) ----------

        /// <summary>The P3 share at ladder step 3 of ARCH §4.8.5 ("the P3 share shrinks to 5 %").</summary>
        public double ReducedP3Share = 0.05;

        /// <summary>Budget ceiling when neither the user cap nor Steam's send rate is known (the direct/LAN default cap, ARCH §4.8.2).</summary>
        public int UnknownLinkBytesPerSecond = 2_000_000;

        /// <summary>Token-bucket depth in seconds of the peer's rate (at least <see cref="MinBurstBytes"/>).</summary>
        public double BurstSeconds = 0.25;

        public int MinBurstBytes = 16 * 1024;

        /// <summary>Minimum spacing of two AIMD decreases (the larger of this and two round trips): one decrease per
        /// congestion episode, not one per poll of the same queue-time sample.</summary>
        public double DecreaseHoldSeconds = 0.5;

        /// <summary>Longest gap the token bucket refills for in one step (a stalled frame must not create a huge burst).</summary>
        public double MaxFrameSeconds = 0.5;

        /// <summary>Fraction of the P1 grant the per-peer P1 channel backlog may use before attention values are scheduled
        /// (it starts with the inspection samples, the first P1 class); the rest of the backlog follows after the values.</summary>
        public double ChannelFirstFractionP1 = 0.5;

        /// <summary>Fraction of the P3 grant reserved for the P3 channel backlog (cell maps, water tiles, poses) whenever it
        /// has content, so rolling values can never starve it; unused reserve flows back to values.</summary>
        public double ChannelReserveFractionP3 = 0.2;

        /// <summary>Weights of the rolling refresh per replication class ("V before A before A-low", ARCH §4.8.4); used only
        /// when <c>PeerScheduler.SlotClass</c> is set. "Other" covers movers' discrete state and unknown classes.</summary>
        public double RollingWeightV = 0.5, RollingWeightA = 0.3, RollingWeightALow = 0.1, RollingWeightOther = 0.1;

        /// <summary>Upper bound of slots the scheduler inspects per peer and frame (CPU bound on the worker).</summary>
        public int MaxSlotVisitsPerFrame = 16384;

        /// <summary>How often the per-peer index of stale dirty buckets is rebuilt.</summary>
        public double StaleRescanSeconds = 0.25;

        /// <summary>Correct the budget for compression: the measured ratio of wire bytes (Steam's out rate) to scheduled
        /// bytes, clamped to [<see cref="MinWireRatio"/>, 1], scales what every scheduled byte costs. Never above 1, so the
        /// estimate can only make the scheduler send less than the bytes it counts would suggest, never more.</summary>
        public bool EstimateWireRatio = true;

        public double MinWireRatio = 0.25;

        // ---------- attention tunables (additive) ----------

        /// <summary>Add the camera's frustum footprint to the motion interest region (ARCH §4.8.3 item 1). The view report
        /// carries no field of view, so the footprint uses these assumed half angles.</summary>
        public bool UseFrustumFootprint = true;

        public float FrustumHalfAngleDegrees = 40f, FrustumVerticalHalfAngleDegrees = 22.5f;

        /// <summary>The in-view static set is refreshed at this period or after a large camera move (ARCH §4.8.3 item 2).</summary>
        public double InViewRefreshSeconds = 1.0;

        /// <summary>A pivot or camera move beyond this fraction of the motion radius, or a zoom change beyond
        /// 1 ± this fraction, counts as a large camera move.</summary>
        public float InViewRefreshMoveFraction = 0.25f;

        /// <summary>Upper bound of slots in one in-view, closure or topic set (oversized sets are truncated and logged).</summary>
        public int MaxAttentionSetSize = 1 << 20;

        // ---------- degradation ladder tunables (additive, ARCH §4.8.5) ----------

        /// <summary>Motion demand above this multiple of its budget counts as over budget.</summary>
        public double OverBudgetRatio = 1.05;

        /// <summary>Motion demand below this multiple of its budget counts as headroom (one step up roughly doubles the far field).</summary>
        public double RecoverBudgetRatio = 0.55;

        /// <summary>Over budget for this long (and this long since the last change) degrades one step.</summary>
        public double DegradeAfterSeconds = 1.0;

        /// <summary>Headroom for this long (and this long since the last change) recovers one step.</summary>
        public double RecoverAfterSeconds = 5.0;

        /// <summary>Period of radius adjustments while the radius step is active, and the factors per adjustment.</summary>
        public double RadiusStepSeconds = 0.5;

        public float RadiusShrinkFactor = 0.85f, RadiusGrowFactor = 1.1f;
    }
}
