namespace SkylineTogether.Core
{
    /// <summary>
    /// Header of a STRUCT record (ARCH §4.6.3): a signature change of a bound entity to <see cref="NewTypeSet"/>, with
    /// the payloads of the added types (<c>varint slot, varint typeSetId, varint addedPayloadCount, payloads</c>).
    /// </summary>
    public struct StructHeader
    {
        public NetSlot Slot;
        public TypeSetId NewTypeSet;
        /// <summary>Number of payloads of added types that follow.</summary>
        public int AddedComponentCount;
    }
}
