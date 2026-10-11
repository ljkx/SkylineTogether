namespace SkylineTogether.Core
{
    /// <summary>Delivery priorities of the per-peer scheduler (ARCH §4.8.1).</summary>
    public enum Priority : byte
    {
        /// <summary>Shared P0: transactions, existence, events, icons; never throttled.</summary>
        P0 = 0,
        /// <summary>Attention values: inspection samples, in-view state, infoview topics, repair responses.</summary>
        P1 = 1,
        /// <summary>Motion (unreliable lane).</summary>
        P2 = 2,
        /// <summary>Rolling refresh of everything else that is dirty.</summary>
        P3 = 3,
        /// <summary>Bulk lane: snapshots, rebaselines, tiles.</summary>
        Bulk = 4
    }
}
