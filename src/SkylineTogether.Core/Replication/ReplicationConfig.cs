namespace SkylineTogether.Core.Replication
{
    /// <summary>
    /// Tunables of the host and client replication engines (ARCH §4.4-4.7, Appendix D). The engines read the fields when
    /// they need them (most of them every frame), so a composition root may adjust a value at run time from the main
    /// thread, for example the upload caps after a session-settings change.
    /// </summary>
    public sealed class ReplicationConfig
    {
        /// <summary>Minimum interval between two value-only S sends of one entity: ≤ 10 Hz (ARCH §4.4.5).</summary>
        public double SValueCoalesceSeconds = 0.1;

        /// <summary>Minimum interval between two G sends of one singleton: ≤ 4 Hz (ARCH §4.4.5).</summary>
        public double GValueCoalesceSeconds = 0.25;

        /// <summary>Byte cap of the host's epoch log (P0 content only; ARCH §4.5.4).</summary>
        public long EpochLogBytes = 128L << 20;

        /// <summary>Time cap of the host's epoch log.</summary>
        public double EpochLogSeconds = 600;

        /// <summary>Folder for spilled epoch-log entries; null or empty keeps the whole log in memory.</summary>
        public string EpochLogSpillDirectory;

        /// <summary>How long a dropped peer's dirty set and send log are kept for a short-drop resume (ARCH §7.8).</summary>
        public double ShortDropGraceSeconds = 300;

        /// <summary>Epochs a parked value waits for its missing reference before a repair request (ARCH §4.7.5).</summary>
        public int PendingEpochTtl = 600;

        /// <summary>Seconds a value parked on an A-low target waits, without escalation (ARCH §4.7.5).</summary>
        public double ALowPendingSeconds = 120;

        /// <summary>
        /// Echo suppression for the originator of a proposal (ARCH §5.5.3, milestone M4/M9). Not available in this build:
        /// when set, the host engine logs once and keeps the shared encoding, which is always correct.
        /// </summary>
        public bool EchoSuppression;

        /// <summary>P3 share of a joiner that is catching up (ARCH §7.1 step 12); used by the built-in fallback scheduler.</summary>
        public double JoinerP3Share = 0.5;

        /// <summary>Structural repair re-sends (entity re-sends and "not present" answers) per epoch at most (ARCH §8.5).</summary>
        public int MaxRepairSlotsPerMessage = 512;

        /// <summary>Largest epoch message the host emits; larger transactions travel as epoch parts (ARCH §4.6.2).</summary>
        public int MaxPartBytes = Protocol.Limits.MaxUncompressedBytes;

        /// <summary>Main-thread milliseconds per frame the game layer spends on host session start (ARCH §4.4.8).</summary>
        public double SessionStartBudgetMs = 8;

        // ---- additions of the implementation (not in the plan's table; additive) ----

        /// <summary>The session's per-peer upload cap, handed to the scheduler as <c>PeerLinkState.UserCapBytesPerSecond</c>
        /// (session setting, ARCH §4.8.2; default 300 KB/s over relay).</summary>
        public int PerPeerCapBytesPerSecond = 300_000;

        /// <summary>The host's total upload cap, split equally between peers for <c>PeerLinkState.HostShareBytesPerSecond</c>
        /// (the scheduler redistributes unused shares; default 1.5 MB/s, ARCH §4.8.2).</summary>
        public int HostUploadCapBytesPerSecond = 1_500_000;

        /// <summary>Log epochs replayed to one joining or resuming peer per frame at most (bounds the worker's frame).</summary>
        public int MaxReplayBytesPerFrame = 4 << 20;

        /// <summary>Beacons appended to one peer message at most (the beacon source applies its own rate).</summary>
        public int MaxBeaconsPerMessage = 64;

        /// <summary>Client: epochs buffered in memory before further epochs spill to a temp file (ARCH §7.1 step 8).</summary>
        public long ClientBacklogMemoryBytes = 32L << 20;

        /// <summary>Client: seconds a received inspection value counts as pending before it is assumed applied (ARCH §4.7.9).</summary>
        public double InspectionPendingSeconds = 10;

        /// <summary>Host: a garbage share of the value shadow above which <c>EndCapture</c> compacts incrementally.</summary>
        public double ValueShadowCompactThreshold = 0.25;

        /// <summary>Host: main-thread milliseconds per frame for incremental value-shadow compaction.</summary>
        public double ValueShadowCompactMs = 0.25;

        /// <summary>Fallback scheduler (used when no <c>IPeerScheduler</c> is supplied): byte floor per peer per second.</summary>
        public int FallbackFloorBytesPerSecond = 48_000;

        /// <summary>Returns a member-wise copy.</summary>
        public ReplicationConfig Clone() => (ReplicationConfig)MemberwiseClone();
    }
}
