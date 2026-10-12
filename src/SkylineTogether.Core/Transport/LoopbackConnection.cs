using System.Globalization;

namespace SkylineTogether.Core.Transport
{
    /// <summary>
    /// One end of an in-process connection of <see cref="LoopbackTransport"/>. Every field is guarded by the lock of
    /// the owner's <see cref="LoopbackNetwork"/>; the public members delegate to the owner, which takes it.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    internal sealed class LoopbackConnection : IConnection
    {
        internal readonly LoopbackTransport Owner;

        /// <summary>The other end; null for a connection attempt that failed.</summary>
        internal LoopbackConnection Remote;

        /// <summary><see cref="Close"/> was called on this end (or its transport was disposed).</summary>
        internal bool LocallyClosed;

        /// <summary>The other end closed; sends fail from now on.</summary>
        internal bool RemotelyClosed;

        /// <summary>Closed without linger: messages this end sent that the other end has not polled yet are dropped.</summary>
        internal bool DiscardUndelivered;

        /// <summary>Bytes this end sent that the other end has not polled yet (the transport's "send buffer").</summary>
        internal long PendingReliable;
        internal long PendingUnreliable;

        internal long BytesSent;
        internal long BytesReceived;
        internal long MessagesSent;
        internal long MessagesReceived;

        internal LoopbackConnection(LoopbackTransport owner, int id, PeerIdentity peer)
        {
            Owner = owner;
            Id = id;
            Peer = peer;
        }

        public int Id { get; }

        public PeerIdentity Peer { get; }

        public SendResult Send(Lane lane, Delivery delivery, byte[] buffer, int offset, int count)
        {
            return Owner.SendFrom(this, lane, delivery, buffer, offset, count);
        }

        public ConnectionStats GetStats()
        {
            return Owner.StatsOf(this);
        }

        public void Close(int reason, string text, bool linger)
        {
            Owner.CloseConnection(this, reason, text, linger);
        }

        public override string ToString()
        {
            return "loopback#" + Id.ToString(CultureInfo.InvariantCulture) + " to " + Peer;
        }
    }
}
