namespace SkylineTogether.Core
{
    /// <summary>
    /// A peer's send side as the engines see it, implemented by core-transport's <c>PeerOutbox</c> (ARCHITECTURE.md#outbox-lanes-and-rate-control).
    /// Reliable messages are queued and never dropped; unreliable ones go straight to the connection.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public interface IPeerOutput
    {
        PeerId Peer { get; }

        ConnectionStats Stats { get; }

        /// <summary>Reliable: queued, never dropped (returns <see cref="SendResult.Ok"/> or <see cref="SendResult.Closed"/>).
        /// Unreliable: sent at once, dropped when refused.</summary>
        SendResult Send(Lane lane, Delivery delivery, byte[] buffer, int offset, int count);

        /// <summary>The reliable window has room for <paramref name="bytes"/> (the scheduler asks before building value sections).</summary>
        bool CanAcceptReliable(int bytes);

        /// <summary>Bytes waiting in the outbox (they count as P0 backlog, ARCHITECTURE.md#live-session).</summary>
        long QueuedBytes { get; }

        /// <summary>Age of the oldest queued message.</summary>
        double OldestQueuedSeconds { get; }
    }
}
