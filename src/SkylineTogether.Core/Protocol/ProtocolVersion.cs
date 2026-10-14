namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// Version of the wire format (ARCHITECTURE.md#code-rules): every change of an envelope, epoch, record or
    /// message layout increments it, and peers with different versions refuse each other in the handshake
    /// (<see cref="RefuseReason.ProtocolVersion"/>), before the password and before any build or mod detail. The policy
    /// (one increment per change that reaches main) is in docs/dev/releasing.md. The golden byte vectors of the Core
    /// tests (<c>Protocol/*GoldenTests.cs</c>) pin the layouts.
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
        /// <summary>
        /// The version this build speaks. 1 was the first layout; 2 covers everything added since under one increment (among
        /// others the id ceiling record, the cell-map messages and the mod channel message kind).
        /// </summary>
        public const int Current = 2;
    }
}
