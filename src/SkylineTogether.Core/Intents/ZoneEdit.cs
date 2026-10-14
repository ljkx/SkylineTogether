namespace SkylineTogether.Core.Intents
{
    /// <summary>
    /// A zone overlay of one zone block (docs/history/ARCHITECTURE-original.md §4.10.7, §5.4.2): the zone prefab rank of every cell (never the
    /// machine-local <c>ZoneType.m_Index</c>, docs/history/ARCHITECTURE-original.md §3.5). <see cref="Block"/> is the block's derived key
    /// (<see cref="DerivedKeyKind.Block"/>); <see cref="BaseRevision"/> is the per-block-key revision (docs/history/ARCHITECTURE-original.md §3.8).
    /// The host writes <c>Cell.m_Zone</c> on the matching block plus <c>Updated</c>, as <c>ApplyZonesSystem</c> does
    /// (<c>ApplyZonesSystem.cs:104-140</c>).
    /// </summary>
    public sealed class ZoneEdit
    {
        public DerivedKey Block;

        public ushort BaseRevision;

        /// <summary>Zone prefab rank per cell (at most <see cref="ProposalFormat.MaxZoneCellsPerEdit"/> cells).</summary>
        public ushort[] ZoneRanks;

        public override string ToString() => "zone " + Block + " rev " + BaseRevision + " (" + (ZoneRanks == null ? 0 : ZoneRanks.Length) + " cells)";
    }
}
