namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// The header of an epoch message (ARCH §4.6.2):
    /// <c>u32 epoch | u32 hostTick | u8 flags | u8 sectionCount | [MultiPart: varint partIndex, varint partCount]</c>.
    /// A single-part epoch has <see cref="PartIndex"/> 0 and <see cref="PartCount"/> 1.
    /// </summary>
    public struct EpochHeader
    {
        public EpochId Epoch;
        /// <summary>The host <c>frameIndex</c> at capture.</summary>
        public uint HostTick;
        public EpochFlags Flags;
        /// <summary>Sections in this message (in this part, for a multi-part epoch).</summary>
        public int SectionCount;
        public int PartIndex;
        public int PartCount;
    }
}
