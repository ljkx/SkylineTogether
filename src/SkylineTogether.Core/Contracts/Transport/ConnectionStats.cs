namespace SkylineTogether.Core
{
    /// <summary>
    /// Real-time link statistics of a connection (Steam <c>GetConnectionRealTimeStatus</c>, R06 §4.1); the link
    /// estimator feeds the per-peer token bucket with them (ARCH §4.8.2).
    /// </summary>
    public struct ConnectionStats
    {
        public int PingMs;
        /// <summary>Connection quality 0..1 (Steam's local quality estimate).</summary>
        public float Quality;
        /// <summary>The transport's estimated sustainable send rate.</summary>
        public int SendRateBytesPerSecond;
        /// <summary>Reliable bytes queued inside the transport (Steam <c>m_cbPendingReliable</c>).</summary>
        public int PendingReliableBytes;
        public int PendingUnreliableBytes;
        /// <summary>Time the next message would wait in the transport's queue.</summary>
        public long QueueTimeMicroseconds;
        public float OutBytesPerSecond;
        public float InBytesPerSecond;
        /// <summary>
        /// loadspeed: delivery rate 0..1 of what this side sends, as the remote end observed it (Steam
        /// <c>m_flConnectionQualityRemote</c>); 0 or below = unknown. The send rate controller backs off on loss.
        /// </summary>
        public float RemoteQuality;
    }
}
