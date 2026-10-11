namespace SkylineTogether.Core
{
    /// <summary>A DESPAWN record (ARCH §4.6.3): <c>varint slot, u8 gen, u8 flags {DeleteChildrenAsHost}</c>.</summary>
    public struct DespawnRecord
    {
        public NetSlot Slot;
        public byte Generation;
        /// <summary>The host also deleted the first-class children; the client deletes them the same way.</summary>
        public bool DeleteChildrenAsHost;
    }
}
