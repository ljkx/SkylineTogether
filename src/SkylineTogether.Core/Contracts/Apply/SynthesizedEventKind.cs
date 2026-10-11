namespace SkylineTogether.Core
{
    /// <summary>Derivation events a client synthesises after applying host data (ARCH §4.7.7, §4.7.8).</summary>
    public enum SynthesizedEventKind : byte
    {
        /// <summary><c>Event+RentersUpdated</c> for a property, only after its <c>Renter</c> buffer was applied in the same program.</summary>
        RentersUpdated,
        /// <summary><c>Event+ColorUpdated</c> for a route.</summary>
        ColorUpdated,
        /// <summary><c>Event+SubObjectsUpdated</c> for an owner.</summary>
        SubObjectsUpdated,
        /// <summary><c>Event+PathUpdated</c> for a segment whose path state was applied (ARCH §4.11.3): once per segment,
        /// after the last of its path-state values.</summary>
        PathUpdated
    }
}
