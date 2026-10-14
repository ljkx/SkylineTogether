namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// Why the host refused a join (<see cref="RefuseMessage"/>, ARCHITECTURE.md#handshake-and-admission, #security-model); the values travel on the wire
    /// and never change. Readers accept unknown values (a newer peer), which the UI shows generically.
    /// </summary>
    public enum RefuseReason : byte
    {
        /// <summary>A compatibility mismatch; the message carries the report (ARCHITECTURE.md#handshake-and-admission).</summary>
        Mismatch,
        Full,
        Banned,
        BadPassword,
        NotInvited,
        HostShuttingDown,
        /// <summary>The peers speak different protocol versions.</summary>
        ProtocolVersion,
        /// <summary>Host session start has not completed yet (ARCHITECTURE.md#host-capture).</summary>
        HostPreparing
    }
}
