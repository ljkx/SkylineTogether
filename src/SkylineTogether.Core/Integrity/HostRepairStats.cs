namespace SkylineTogether.Core.Integrity
{
    /// <summary>Counters of <see cref="HostRepairResponder"/> over all peers since its creation (network panel, diagnostics dump).</summary>
    public struct HostRepairStats
    {
        /// <summary>Requests served (levels 0-2 and bucket-detail requests).</summary>
        public int Served;

        /// <summary>Requests dropped: above the per-peer rate, or a rebaseline / snapshot offer already running.</summary>
        public int Throttled;

        /// <summary>Requests ignored as invalid (undefined class or level).</summary>
        public int Rejected;

        /// <summary>Slots re-serialised from live ECS for a peer (<c>ResendStructural</c>).</summary>
        public int SlotsResent;

        /// <summary>Slots whose pairs were marked dirty for a peer at P1 (<c>MarkDirty</c>).</summary>
        public int SlotsMarkedDirty;

        /// <summary>Slots answered "not present" (the client adds <c>Deleted</c>).</summary>
        public int NotPresent;

        /// <summary>Bucket details queued, built from the hash shadow, and dropped (queue overflow, or a bucket that stayed dirty too long).</summary>
        public int DetailsQueued, DetailsBuilt, DetailsDropped;

        /// <summary>Domain rebaselines started.</summary>
        public int Rebaselines;

        /// <summary>Snapshot resyncs offered.</summary>
        public int SnapshotOffers;

        /// <summary>Peers that needed more snapshot resyncs than allowed within 30 minutes ("repair loop" disconnects).</summary>
        public int RepairLoops;
    }
}
