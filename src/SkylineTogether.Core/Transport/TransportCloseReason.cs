namespace SkylineTogether.Core.Transport
{
    /// <summary>
    /// Reason codes the Core transports report in <see cref="ITransportEventSink.OnDisconnected"/> when the transport,
    /// not the application, ends a connection. They sit in the range Steam uses for miscellaneous transport endings
    /// (5000-5999), so they never collide with the application's own reasons (Steam's application range is 1000-2999)
    /// that <see cref="IConnection.Close"/> passes through unchanged.
    /// </summary>
    public static class TransportCloseReason
    {
        /// <summary>Nobody listens at the endpoint (or it is not reachable).</summary>
        public const int NoListener = 5101;

        /// <summary>The listener already has <see cref="ListenOptions.MaxConnections"/> connections.</summary>
        public const int ListenerFull = 5102;

        /// <summary>The local or remote transport was disposed.</summary>
        public const int TransportDisposed = 5103;

        /// <summary>No packet arrived within the connection timeout (UDP transport).</summary>
        public const int Timeout = 5104;

        /// <summary>The handshake failed: wrong session password or failed authentication (UDP transport).</summary>
        public const int AuthenticationFailed = 5105;

        /// <summary>The peer speaks another transport protocol version (UDP transport).</summary>
        public const int ProtocolMismatch = 5106;

        /// <summary>The address is temporarily blocked after repeated authentication failures (UDP transport, ARCH §14.2).</summary>
        public const int AddressBlocked = 5107;

        /// <summary>A malformed or unauthenticated packet stream, or a receive buffer overflow (UDP transport).</summary>
        public const int ProtocolError = 5108;

        /// <summary>The host name could not be resolved (UDP transport).</summary>
        public const int ResolveFailed = 5109;

        /// <summary>A socket error ended the connection (UDP transport).</summary>
        public const int SocketError = 5110;
    }
}
