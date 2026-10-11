namespace SkylineTogether.Core
{
    /// <summary>Operation of an ICON record (ARCH §4.6.3, §4.10.4); the values travel on the wire.</summary>
    public enum IconOp : byte
    {
        /// <summary>Add (idempotent per owner, prefab and target).</summary>
        Add,
        Remove,
        /// <summary>Remove every icon of the owner (icon-set repair).</summary>
        RemoveAll,
        /// <summary>A transaction-layer icon event (level-up sparkles, meeting icons).</summary>
        Event
    }
}
