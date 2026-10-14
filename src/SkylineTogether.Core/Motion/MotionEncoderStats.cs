namespace SkylineTogether.Core.Motion
{
    /// <summary>
    /// Counters of one peer's motion stream on the host (<see cref="MotionHostEncoder.GetStats"/>), for the network
    /// panel, the diagnostics dump and the measurement report (ARCHITECTURE.md#diagnostics-dump, #performance-budgets). Totals since the peer was added.
    /// </summary>
    public struct MotionEncoderStats
    {
        /// <summary>Movers in the peer's interest (assigned or waiting for their assignment message).</summary>
        public int InInterest;
        /// <summary>Movers whose assignment was not sent yet (budget or a full interest-slot table).</summary>
        public int AwaitingAssign;
        /// <summary>Released interest slots the client has not acknowledged yet.</summary>
        public int PendingReleases;
        /// <summary>The last <c>MotionControl</c> sequence sent and the last one the client acknowledged.</summary>
        public uint LastControlSeq, LastAckedControlSeq;

        public long Entries, Exits, Resets;
        public long FramesAdded, FramesSent, Keyframes, Deltas;
        /// <summary>Frames not sent: over the budget, skipped by the far-field rate, superseded or for unknown movers.</summary>
        public long FramesDroppedBudget, FramesSkippedRate, FramesStale, FramesNotInInterest;
        /// <summary>Frames or entries refused because of invalid data (wrong update frame, non-finite values).</summary>
        public long FramesRejected, EntriesRejected;
        public long Datagrams, DatagramBytes, DatagramsRefused;
        public long ControlMessages, ControlBytes, AssignsSent, AssignsDeferred, ReleasesSent;
        public long AcksIgnored, FlushFailures;
    }
}
