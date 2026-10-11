namespace SkylineTogether.Core
{
    /// <summary>A peer's link as the scheduler sees it in one frame (ARCH §4.8.2).</summary>
    public struct PeerLinkState
    {
        public ConnectionStats Stats;
        /// <summary>The per-peer cap of the session settings.</summary>
        public int UserCapBytesPerSecond;
        /// <summary>This peer's share of the host's total upload cap.</summary>
        public int HostShareBytesPerSecond;
        /// <summary>Bytes waiting in the peer's outbox (P0 backlog).</summary>
        public long OutboxQueuedBytes;
        /// <summary>The outbox's reliable window has room; without it no P1/P3 section is built this frame.</summary>
        public bool ReliableWindowOpen;
    }
}
