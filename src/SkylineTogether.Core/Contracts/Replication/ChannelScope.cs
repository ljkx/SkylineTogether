namespace SkylineTogether.Core
{
    /// <summary>Where a channel record goes (ARCH §4.10).</summary>
    public enum ChannelScope : byte
    {
        /// <summary>The shared P0 section of the epoch (every peer, epoch log).</summary>
        SharedP0,
        /// <summary>One peer's P1 value section.</summary>
        PerPeerP1,
        /// <summary>One peer's P3 value section.</summary>
        PerPeerP3
    }
}
