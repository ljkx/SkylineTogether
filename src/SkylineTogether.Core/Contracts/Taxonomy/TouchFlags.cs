using System;

namespace SkylineTogether.Core
{
    /// <summary>
    /// Lifecycle tags a client mirrors on an entity (TOUCH records and tag rules, ARCH §4.4.2 step 3, §4.7.7).
    /// The values travel on the wire (TOUCH tag byte).
    /// </summary>
    [Flags]
    public enum TouchFlags : byte
    {
        None = 0,
        /// <summary><c>Updated</c>: derived systems regenerate the entity (plus the client-computed net closure).</summary>
        Updated = 1,
        /// <summary><c>BatchesUpdated</c>: visual-only change.</summary>
        BatchesUpdated = 2,
        /// <summary><c>PathfindUpdated</c>: lane-relevant edge change.</summary>
        PathfindUpdated = 4,
        /// <summary><c>EffectsUpdated</c>: effect-relevant change (lights, sirens, boarding).</summary>
        EffectsUpdated = 8
    }
}
