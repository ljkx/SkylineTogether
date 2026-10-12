namespace SkylineTogether.Core.Integrity
{
    /// <summary>
    /// Thresholds of the client repair ladder (ARCH §8.5, Appendix D) used by <see cref="RepairCoordinator"/>; read at use
    /// time, so a change takes effect immediately. The <c>Min...</c> floors are not in the architecture's table: they
    /// keep a fractional rule from escalating a tiny class or a small city on a single bucket or pair, where entity
    /// repair (level 2) already covers every difference and a rebaseline would only consume the host's rebaseline budget
    /// (a second rebaseline of a class within 10 minutes is a snapshot resync).
    /// </summary>
    public sealed class RepairThresholds
    {
        /// <summary>Level 3 when a confirmed bucket detail shows more than this many differing, missing or extra slots (64).</summary>
        public int EntityRepairLimit = 64;

        /// <summary>Level 3 when more than this fraction of a class's buckets is confirmed mismatching (10 %).</summary>
        public double ClassBucketFraction = 0.10;

        /// <summary>The bucket-fraction rule needs at least this many confirmed buckets.</summary>
        public int MinConfirmedBucketsForFraction = 3;

        /// <summary>Level 3 when a class has more than this many confirmed bucket repairs within one minute (3).</summary>
        public int BucketRepairsPerMinuteLimit = 3;

        /// <summary>Level 4 when more than this fraction of the S buckets shows a confirmed structural mismatch (5 %).</summary>
        public double StructuralFraction = 0.05;

        /// <summary>The structural rule needs at least this many structurally mismatching S buckets.</summary>
        public int MinStructuralBucketsForResync = 4;

        /// <summary>A pair or slot that still mismatches after this many repairs goes on the tolerance list (2).</summary>
        public int FailedRepairsBeforeTolerance = 2;

        /// <summary>How long a tolerated pair or slot stays masked (10 minutes).</summary>
        public double ToleranceSeconds = 600;

        /// <summary>Level 3 when the pairs tolerated since the class's last rebaseline exceed this fraction of the class (1 %).</summary>
        public double ToleranceClassFraction = 0.01;

        /// <summary>The tolerance rule needs at least this many tolerated pairs.</summary>
        public int MinToleratedForRebaseline = 4;

        /// <summary>A requested bucket detail that has not arrived after this many seconds may be requested again.</summary>
        public double DetailTimeoutSeconds = 10;

        /// <summary>A confirmed bucket that sees no further beacon stops counting toward the fraction rules after this many seconds.</summary>
        public double ConfirmedBucketLifetimeSeconds = 300;

        /// <summary>A rebaseline or resync hint stays raised (not repeated) for this many seconds unless reported complete.</summary>
        public double HintCooldownSeconds = 120;

        /// <summary>A leak candidate that is not reported again within this many seconds is forgotten.</summary>
        public double LeakCandidateSeconds = 2;

        /// <summary>Requests of one class are coalesced: at most one per this many seconds (100 ms).</summary>
        public double CoalesceSeconds = 0.1;

        /// <summary>Slots queued per class at most (bounded memory under a repair storm); further slots wait for the next detail.</summary>
        public int MaxPendingSlotsPerClass = 8192;

        /// <summary>Bucket details kept while waiting for their epoch to be applied; the oldest is dropped beyond it.</summary>
        public int MaxDeferredDetails = 64;
    }
}
