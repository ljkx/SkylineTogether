namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// A decoded envelope (<see cref="EnvelopeCodec.Decode"/>). <see cref="Body"/>[<see cref="BodyOffset"/> ..
    /// <see cref="BodyOffset"/> + <see cref="BodyCount"/> − 1] is the message body, already decompressed.
    /// <see cref="Flags"/> are the wire flags (informational, except <see cref="EnvelopeFlags.Fragment"/>: then the body
    /// is a fragment for <see cref="Reassembler.Accept"/>).
    /// <para>
    /// The body array is borrowed: it is the input buffer for uncompressed messages and fragments, and the scratch
    /// writer's buffer for decompressed ones. Copy what you keep beyond the next decode or the transport callback.
    /// </para>
    /// </summary>
    public struct Envelope
    {
        public MessageKind Kind;
        public EnvelopeFlags Flags;
        public byte[] Body;
        public int BodyOffset;
        public int BodyCount;
    }
}
