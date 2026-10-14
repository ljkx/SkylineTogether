namespace SkylineTogether.Core.Motion
{
    /// <summary>
    /// The header of a Q-B motion record (ARCHITECTURE.md#movers): <c>u16 interestSlot</c>, then
    /// <c>u8 hdr = keyframe (bit 0) | refAge (bits 1-3) | assignGeneration (bits 4-7)</c>.
    /// <see cref="RefAge"/> is the distance to the reference frame in 16-tick steps (1..7) for deltas and 0 for keyframes;
    /// <see cref="AssignGeneration"/> has 4 bits.
    /// </summary>
    public struct QbHeader
    {
        public ushort InterestSlot;
        public bool Keyframe;
        public byte RefAge;
        public byte AssignGeneration;
    }
}
