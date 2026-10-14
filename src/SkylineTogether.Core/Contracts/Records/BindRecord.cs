namespace SkylineTogether.Core
{
    /// <summary>
    /// A BIND record (docs/history/ARCHITECTURE-original.md §4.6.3, §5.5.1): the host committed the predicted entity <see cref="Local"/> of peer
    /// <see cref="Origin"/> as <see cref="Slot"/>. It precedes the SPAWN of that slot; only the origin peer acts on it
    /// (every client counts its local ids from 1), all others skip it.
    /// </summary>
    public struct BindRecord
    {
        public PeerId Origin;
        public LocalId Local;
        public NetSlot Slot;
        public byte Generation;
    }
}
