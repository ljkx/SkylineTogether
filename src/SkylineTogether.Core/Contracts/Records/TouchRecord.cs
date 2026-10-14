namespace SkylineTogether.Core
{
    /// <summary>A TOUCH record (docs/history/ARCHITECTURE-original.md §4.6.3): the host's tags on a slot, mirrored so clients re-derive the host's closure.</summary>
    public struct TouchRecord
    {
        public NetSlot Slot;
        public TouchFlags Flags;
    }
}
