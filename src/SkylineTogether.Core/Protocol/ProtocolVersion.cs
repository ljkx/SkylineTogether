namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// Version of the wire format (IMPLEMENTATION-PLAN §3 rule 12): every change of an envelope, epoch, record or
    /// message layout increments it, and peers with different versions refuse each other in the handshake
    /// (<see cref="RefuseReason.ProtocolVersion"/>). Version 1 is pinned by the golden byte vectors of the Core tests
    /// (<c>Protocol/*GoldenTests.cs</c>).
    /// <para>
    /// Two layouts are kept stable across versions so that peers of different versions can still refuse each other
    /// readably: the leading <c>varint protocolVersion</c> of <see cref="HelloMessage"/> (see
    /// <see cref="HelloMessage.PeekProtocolVersion"/>) and the whole <see cref="RefuseMessage"/>, both inside the
    /// two-byte envelope header of <see cref="EnvelopeCodec"/>.
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class ProtocolVersion
    {
        /// <summary>The version this build speaks.</summary>
        public const int Current = 1;
    }
}
