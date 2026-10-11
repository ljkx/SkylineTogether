namespace SkylineTogether.Core.Fields
{
    /// <summary>
    /// Counters of a <see cref="CellMapVersionRing"/> since its creation, for the network panel and the diagnostics
    /// dump (ARCH §13.2, §13.6): how often peers needed keyframes instead of deltas and what the records cost.
    /// </summary>
    public struct CellMapRingStats
    {
        /// <summary>New versions stored by <see cref="CellMapVersionRing.Publish"/> (identical content excluded).</summary>
        public long Versions;

        /// <summary>XOR delta records written.</summary>
        public long Deltas;

        /// <summary>Keyframe records written (the peer's version had left the ring, was unknown or had another size).</summary>
        public long Keyframes;

        /// <summary>Total bytes of all written records.</summary>
        public long BytesWritten;

        /// <summary>Records applied on a client.</summary>
        public long Applied;

        /// <summary>Records skipped on a client because they were not newer than the local map.</summary>
        public long Stale;

        /// <summary>Delta records skipped on a client because their base version was not held.</summary>
        public long MissingBase;

        /// <summary>Records skipped on a client because they named an unknown map.</summary>
        public long UnknownMap;

        /// <summary>Records skipped on a client because their map size differed from the expected one.</summary>
        public long SizeMismatch;
    }
}
