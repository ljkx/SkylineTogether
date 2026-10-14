namespace SkylineTogether.Core.Scheduling
{
    /// <summary>
    /// Steps of the degradation ladder under a small budget, in the order of docs/history/ARCHITECTURE-original.md §4.8.5. Each step includes the ones
    /// before it. P0 is never degraded, and "P1 never drops below 30 % of what remains after P0" is an invariant of the
    /// scheduler at every step, not a step.
    /// </summary>
    public enum DegradationStep : byte
    {
        None = 0,

        /// <summary>1a: far-field motion at half rate (the client extrapolates, then pins).</summary>
        FarHalf = 1,

        /// <summary>1b: far-field motion at a quarter rate.</summary>
        FarQuarter = 2,

        /// <summary>2: the motion radius shrinks towards the minimum radius (300 m).</summary>
        ShrinkRadius = 3,

        /// <summary>3: the P3 share shrinks to 5 % (staleness grows; nothing is lost).</summary>
        ReduceP3 = 4,

        /// <summary>4: water correction tiles pause (game-fields reads the step per peer).</summary>
        PauseWaterTiles = 5,

        /// <summary>5: cell-map deltas are coalesced to one per map per minute (game-state-channels reads the step).</summary>
        CoalesceCellMaps = 6
    }
}
