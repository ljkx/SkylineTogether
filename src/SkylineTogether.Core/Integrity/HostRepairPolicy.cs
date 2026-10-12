namespace SkylineTogether.Core.Integrity
{
    /// <summary>
    /// Per-peer caps of <see cref="HostRepairResponder"/> (ARCH §8.5, §14.3, Appendix D). The first five fields are the
    /// plan's contract; the rest tune timeouts that the architecture leaves open. Values below their minimum are
    /// treated as the minimum by the responder.
    /// </summary>
    public sealed class HostRepairPolicy
    {
        /// <summary>Repair requests accepted per peer and second; excess requests are dropped (throttled), never a kick.</summary>
        public int MaxRequestsPerSecond = 10;

        /// <summary>Slots served from one request; further slots of the request are ignored (the client repeats them).</summary>
        public int MaxSlotsPerRequest = 512;

        /// <summary>Domain rebaselines running at the same time for one peer.</summary>
        public int MaxConcurrentRebaselines = 1;

        /// <summary>Domain rebaselines per peer within 10 minutes, across all classes; a further request becomes a snapshot offer.</summary>
        public int MaxRebaselinesPer10Min = 3;

        /// <summary>Snapshot resyncs per peer within 30 minutes; needing one more disconnects the peer ("repair loop").</summary>
        public int MaxSnapshotResyncsPer30Min = 3;

        /// <summary>A running rebaseline that is never reported complete counts as finished after this many seconds.</summary>
        public double RebaselineTimeoutSeconds = 300;

        /// <summary>A snapshot offer stays outstanding (further snapshot decisions are throttled) for this many seconds.</summary>
        public double SnapshotOfferTimeoutSeconds = 60;

        /// <summary>Distinct buckets queued for details from one bucket-level request.</summary>
        public int MaxDetailsPerRequest = 16;

        /// <summary>Bucket details queued per peer at most; older requests are dropped first.</summary>
        public int MaxQueuedDetailsPerPeer = 256;

        /// <summary>A queued detail of a per-peer class waits at most this long for its bucket to have no dirty bits, then it is dropped.</summary>
        public double DetailMaxWaitSeconds = 30;
    }
}
