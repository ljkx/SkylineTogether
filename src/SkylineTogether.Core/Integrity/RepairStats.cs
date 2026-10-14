namespace SkylineTogether.Core.Integrity
{
    /// <summary>
    /// Counters of the client repair coordinator since its creation (shown in the network panel and written into the
    /// diagnostics dump, docs/history/ARCHITECTURE-original.md §13.2, §13.6). <see cref="RepairCoordinator.Reset"/> keeps them.
    /// </summary>
    public struct RepairStats
    {
        /// <summary>Beacons whose sum or count differed from the ledger (each mismatching beacon counts once).</summary>
        public int BucketMismatches;

        /// <summary>Confirmed per-slot differences (differing, missing and extra slots) found by bucket details of confirmed buckets.</summary>
        public int ConfirmedMismatches;

        /// <summary>Slots queued for an entity re-send from bucket details (level 2).</summary>
        public int EntityRepairs;

        /// <summary>Domain rebaselines requested (level 3 hints).</summary>
        public int Rebaselines;

        /// <summary>Pairs and slots put on the tolerance list.</summary>
        public int Tolerated;

        /// <summary>Leaks confirmed for repair (persisting for two frames, or immediate leaks after re-application).</summary>
        public int Leaks;

        /// <summary>Snapshot resyncs requested (level 4 hints).</summary>
        public int Resyncs;

        /// <summary>Queued items (slots, buckets, hints) merged into a request that was already pending for their class.</summary>
        public int RequestsCoalesced;

        /// <summary>Requests held back by the client rate limit or by a host throttle back-off.</summary>
        public int RequestsThrottled;
    }
}
