namespace SkylineTogether.Core
{
    /// <summary>Header of a VALUE record (ARCH §4.6.3): <c>varint slot, varint typeRank, u8 hdr, varint length, bytes</c>.</summary>
    public struct ValueHeader
    {
        public NetSlot Slot;
        public TypeRank Type;
        public ValueHeaderFlags Flags;
    }
}
