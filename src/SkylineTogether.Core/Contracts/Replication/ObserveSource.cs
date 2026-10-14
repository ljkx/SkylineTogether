namespace SkylineTogether.Core
{
    /// <summary>Which host capture path observed a value (docs/history/ARCHITECTURE-original.md §4.4); it decides coalescing, revisions and priority.</summary>
    public enum ObserveSource : byte
    {
        /// <summary>Part of a spawn.</summary>
        Spawn,
        /// <summary>Event-driven capture from the tag sweep (bumps the revision when the authorable projection changed).</summary>
        Edc,
        /// <summary>Scan-driven capture (never bumps revisions).</summary>
        Sdc,
        /// <summary>Hot-set comparison.</summary>
        HotSet,
        /// <summary>The motion sampler (discrete mover state).</summary>
        Motion,
        /// <summary>Re-serialised at the flush of the S/G pending set.</summary>
        Coalesced,
        /// <summary>Re-serialised for a repair.</summary>
        Repair,
        /// <summary>A committed proposal.</summary>
        Commit,
        /// <summary>Host session start: baseline only, no dirty bits, no records (docs/history/ARCHITECTURE-original.md §4.4.8).</summary>
        Prime,
        /// <summary>A reverse buffer re-captured with its forward reference: forced into this epoch's P0 TX (docs/history/ARCHITECTURE-original.md §4.2.6).</summary>
        CoCapture,
        /// <summary>Segment path state captured on the pathfinder's <c>PathUpdated</c> event: P0, never coalesced (docs/history/ARCHITECTURE-original.md §4.4.2 step 3a).</summary>
        PathResult
    }
}
