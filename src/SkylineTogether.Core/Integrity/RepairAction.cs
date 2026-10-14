namespace SkylineTogether.Core.Integrity
{
    /// <summary>
    /// What <see cref="RepairCoordinator"/> did with an observation (docs/history/ARCHITECTURE-original.md §8.4-8.5). The client only asks; every
    /// request carries a level hint and the host decides (<see cref="HostRepairResponder"/>).
    /// </summary>
    public enum RepairAction : byte
    {
        /// <summary>Nothing to do: the observation matched, was stale, or the bucket is already being handled.</summary>
        None,

        /// <summary>A first mismatch (or a mismatch less than the confirmation interval after the first): wait for a second beacon.</summary>
        AwaitConfirmation,

        /// <summary>Level 1: two mismatching beacons at least the confirmation interval apart; a bucket detail was requested.</summary>
        RequestDetail,

        /// <summary>Level 2: confirmed differing, missing or extra slots were queued for an entity re-send.</summary>
        RequestEntities,

        /// <summary>Level 3: a domain rebaseline of the class was requested (a hint; the host decides).</summary>
        RequestRebaseline,

        /// <summary>Level 4: a snapshot resync was requested (a hint; the host decides).</summary>
        RequestSnapshot,

        /// <summary>The differences are masked: the pairs or slots are on the tolerance list (two failed repairs, 10 minutes).</summary>
        Tolerate
    }
}
