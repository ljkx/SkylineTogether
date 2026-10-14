namespace SkylineTogether.Core.Integrity
{
    /// <summary>
    /// Thresholds of the client repair escalation (ARCHITECTURE.md#escalation-and-resync-offer, #performance-budgets) used by
    /// <see cref="RepairEscalation"/>; read at use time, so a change takes effect immediately.
    /// </summary>
    public sealed class RepairThresholds
    {
        /// <summary>Level 3 when a confirmed bucket detail shows more than this many differing, missing or extra slots (64).</summary>
        public int EntityRepairLimit = 64;

        /// <summary>Level 3 when more than this fraction of a class's buckets is confirmed mismatching (10 %).</summary>
        public double ClassBucketFraction = 0.10;
    }
}
