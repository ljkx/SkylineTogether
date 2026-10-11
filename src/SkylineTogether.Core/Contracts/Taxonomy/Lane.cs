namespace SkylineTogether.Core
{
    /// <summary>Transport lanes (ARCH §9.2); the values are the Steam lane indices.</summary>
    public enum Lane : byte
    {
        /// <summary>R: control, proposals, epochs, beacons, repair (reliable ordered).</summary>
        Reliable = 0,
        /// <summary>U: motion, clock, view reports, presence (unreliable, no delay).</summary>
        Unreliable = 1,
        /// <summary>B: snapshots, rebaselines, resume points, tiles (reliable, leftover bandwidth).</summary>
        Bulk = 2
    }
}
