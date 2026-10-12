using SkylineTogether.Core.Protocol;

namespace SkylineTogether.Core.Motion
{
    /// <summary>
    /// Message layouts of the motion channel (ARCH §4.9.3, Appendix C). Both are complete envelopes
    /// (<c>u8 kind | u8 flags = None | body</c>, ARCH §4.6.1, decodable by <see cref="EnvelopeCodec"/>); the router
    /// hands the receiver the body only.
    /// <code>
    /// Motion (lane U, unreliable no-delay, at most 1200 bytes including the envelope):
    ///   u32 tick | u16 seq | Q-B record...          every record is for a mover whose UpdateFrame == tick &amp; 15
    /// MotionControl (lane R, reliable ordered):
    ///   u32 seq | u16 opCount | op...
    ///   op = u8 kind, then
    ///     Assign     (1): the Q-A record (interest slot, generation, slot, UpdateFrame, four baseline frames with ticks)
    ///     Release    (2): u16 interestSlot | u8 (generation | pin &lt;&lt; 4)
    ///     ReleaseAll (3): u8 (pin &lt;&lt; 4)
    /// </code>
    /// The 10 Hz <c>Ack</c> carries the last applied MotionControl <c>seq</c>; it confirms releases (interest slots are
    /// reused only afterwards). An assignment is valid from the tick after its newest baseline frame.
    /// </summary>
    internal static class MotionWire
    {
        /// <summary><c>u8 kind | u8 flags</c> of an uncompressed envelope.</summary>
        public const int EnvelopeHeaderBytes = 2;

        /// <summary><c>u32 tick | u16 seq</c>.</summary>
        public const int DatagramHeaderBytes = 6;

        /// <summary>Envelope plus datagram header.</summary>
        public const int DatagramOverheadBytes = EnvelopeHeaderBytes + DatagramHeaderBytes;

        /// <summary>Largest motion datagram including the envelope (ARCH §9.3).</summary>
        public const int MaxDatagramBytes = Limits.MaxDatagramBytes;

        /// <summary><c>u32 seq | u16 opCount</c>.</summary>
        public const int ControlHeaderBytes = 6;

        /// <summary>A MotionControl message is closed once its body reaches this size (far below the fragmentation
        /// threshold, so a burst of interest entries never blocks lane R with one huge message).</summary>
        public const int ControlSplitBodyBytes = 32 * 1024;

        /// <summary>Ops per MotionControl message (the <c>u16 opCount</c>).</summary>
        public const int MaxControlOps = ushort.MaxValue;

        public const byte OpAssign = 1;
        public const byte OpRelease = 2;
        public const byte OpReleaseAll = 3;

        /// <summary>Release flag (bit 4 of the release byte): pin the mover at its last pose (ARCH §4.9.5).</summary>
        public const byte PinFlag = 0x10;

        /// <summary>Interest slot 0xFFFF is never assigned (the table holds at most 65535 slots, 0..65534).</summary>
        public const ushort ReservedInterestSlot = 0xFFFF;

        /// <summary>Generations have 4 bits.</summary>
        public const int GenerationMask = 0x0F;
    }
}
