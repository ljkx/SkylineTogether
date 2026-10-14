namespace SkylineTogether.Core.Intents
{
    /// <summary>
    /// The category of a client proposal (docs/history/ARCHITECTURE-original.md §5.2, §5.3). The value travels in the proposal header (<c>u8</c>, see
    /// <see cref="ProposalFormat"/>); never reorder. Which lists a proposal of each kind may fill is a validation rule
    /// (<c>ProposalValidator</c>), not a format rule.
    /// </summary>
    public enum ProposalKind : byte
    {
        /// <summary>
        /// A vanilla tool apply, captured at stages B, C and C2: creates, updates, deletes, touches and zone overlays,
        /// plus <see cref="IntentKind.ReplaceObjectPrefab"/> intents for upgrades and rebuilds in place.
        /// </summary>
        ToolChangeSet,

        /// <summary>
        /// The edit of a direct-write tool (Move It, Tree Controller brush, Better Bulldozer, Zoning Toolkit, Water
        /// Features, Anarchy components, the vanilla service-district selection tool), captured at stage D; same format
        /// as <see cref="ToolChangeSet"/> (docs/history/ARCHITECTURE-original.md §5.3 <c>LocalEditChangeSet</c>).
        /// </summary>
        LocalEdit,

        /// <summary>The terrain brush stamps of one apply frame (stage B, docs/history/ARCHITECTURE-original.md §4.10.9).</summary>
        TerrainStroke,

        /// <summary>UI actions that the host executes through public vanilla APIs (stage A, docs/history/ARCHITECTURE-original.md §5.3).</summary>
        Semantic
    }
}
