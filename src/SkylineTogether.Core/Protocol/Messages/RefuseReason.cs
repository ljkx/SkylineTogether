namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// Why the host refused a join (<see cref="RefuseMessage"/>, ARCH §7.1 step 3, §14.2); the values travel on the wire
    /// and never change. Readers accept unknown values (a newer peer), which the UI shows generically.
    /// </summary>
    public enum RefuseReason : byte
    {
        /// <summary>A compatibility mismatch; the message carries the report (ARCH §7.9).</summary>
        Mismatch,
        Full,
        Banned,
        BadPassword,
        NotInvited,
        HostShuttingDown,
        /// <summary>The peers speak different protocol versions.</summary>
        ProtocolVersion,
        /// <summary>Host session start has not completed yet (ARCH §4.4.8).</summary>
        HostPreparing
    }
}
