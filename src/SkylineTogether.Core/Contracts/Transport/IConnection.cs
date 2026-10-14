namespace SkylineTogether.Core
{
    /// <summary>
    /// One connection of a transport (ARCH §9.1). Reliable traffic never calls <see cref="Send"/> directly: it goes
    /// through core-transport's <c>PeerOutbox</c>, which keeps <see cref="ConnectionStats.PendingReliableBytes"/> inside
    /// a window and re-queues on <see cref="SendResult.Busy"/>, so a reliable message is never lost (ARCH §9.3).
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public interface IConnection
    {
        int Id { get; }

        PeerIdentity Peer { get; }

        /// <summary>Copies the bytes into the transport. Busy = the send buffer is full (Steam LimitExceeded).</summary>
        SendResult Send(Lane lane, Delivery delivery, byte[] buffer, int offset, int count);

        ConnectionStats GetStats();

        /// <summary>Closes the connection; <paramref name="linger"/> flushes queued reliable data first.</summary>
        void Close(int reason, string text, bool linger);
    }
}

namespace SkylineTogether.Core
{
    /// <summary>
    /// Marks a connection whose <see cref="IConnection.Send"/> and <see cref="IConnection.GetStats"/> may be
    /// called from a background thread while the main thread uses it (Steam Networking Sockets is thread-safe). Only
    /// such connections get their snapshot chunks pumped by the host's <c>BackgroundPump</c>.
    /// </summary>
    public interface IThreadSafeConnection : IConnection
    {
    }
}
