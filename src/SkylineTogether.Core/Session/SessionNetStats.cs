namespace SkylineTogether.Core.Session
{
    /// <summary>Link numbers for the network HUD line and the diagnostics dump (host: summed over its peers).</summary>
    public struct SessionNetStats
    {
        /// <summary>Round-trip time in ms (host: the worst live peer), -1 when unknown.</summary>
        public int PingMs;

        public float InBytesPerSecond;

        public float OutBytesPerSecond;

        /// <summary>Bytes waiting in the session's own outboxes (not yet handed to the transport).</summary>
        public long QueuedBytes;

        /// <summary>Connections counted (host: admitted peers; client: 0 or 1).</summary>
        public int Connections;
    }

    /// <summary>w2/ids: one admitted peer's send side, for the bandwidth scheduler (<see cref="SessionHost.TryGetLink"/>).</summary>
    public struct PeerSendState
    {
        /// <summary>The peer finished loading and applies what it receives.</summary>
        public bool Live;

        /// <summary>Reliable bytes waiting in the session's outbox for this peer.</summary>
        public long QueuedReliableBytes;

        /// <summary>Reliable bytes the transport accepted but has not delivered yet.</summary>
        public long PendingReliableBytes;

        /// <summary>The transport's send-rate estimate (0 when unknown).</summary>
        public int SendRateBytesPerSecond;

        public float OutBytesPerSecond;
    }

    /// <summary>The phase a joiner reports in <see cref="Protocol.ControlKind.LoadProgress"/>.</summary>
    public enum JoinPhase : byte
    {
        Downloading = 0,
        Loading = 1
    }
}
