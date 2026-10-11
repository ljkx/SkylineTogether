namespace SkylineTogether.Core.Fields
{
    /// <summary>Outcome of <see cref="CellMapVersionRing.ApplyDelta"/> on a client.</summary>
    public enum CellMapApplyResult : byte
    {
        /// <summary>The record was applied; the map is now at the record's version (acknowledge it).</summary>
        Applied,

        /// <summary>The record is not newer than the local map (a duplicate or an older delta); it was skipped.</summary>
        Stale,

        /// <summary>The delta's base version is no longer (or never was) held locally; it was skipped and the client
        /// needs a keyframe (report the version it does hold).</summary>
        MissingBase,

        /// <summary>The record names a map index this ring does not have (a different map registry); it was skipped.</summary>
        UnknownMap,

        /// <summary>The record's map size differs from the size set with <see cref="CellMapVersionRing.SetExpectedLength"/>
        /// (the peers' maps differ, for example through a mod); it was skipped without allocating anything.</summary>
        SizeMismatch
    }
}
