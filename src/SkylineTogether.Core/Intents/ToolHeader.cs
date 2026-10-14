namespace SkylineTogether.Core.Intents
{
    /// <summary>
    /// The tool that produced a change set (docs/history/ARCHITECTURE-original.md §5.2 stage B, §5.3): which tool, its prefab and mode, and the cost the
    /// local apply charged, computed as <c>ToolApplySystem</c> does: Σ <c>Temp.m_Cost</c> of every temp without
    /// <c>TempFlags.Cancel</c> (<c>ToolApplySystem.cs:42-74</c>). The sum is negative when refunds of recently built
    /// entities exceed the cost (the vanilla apply then adds money).
    /// </summary>
    public sealed class ToolHeader
    {
        /// <summary>The game layer's numeric tool id (diagnostics and presence; never used for authority decisions).</summary>
        public ushort ToolKind;

        /// <summary>Full type name of the active tool system (identifier, at most 256 UTF-8 bytes; null allowed).</summary>
        public string ToolTypeName;

        /// <summary>The tool's prefab (<see cref="PrefabRank.None"/> when the tool has none).</summary>
        public PrefabRank Prefab = PrefabRank.None;

        /// <summary>The tool's mode (game-layer value, e.g. the net tool's mode).</summary>
        public byte Mode;

        /// <summary>Σ <c>Temp.m_Cost</c> without <c>Cancel</c>; negative for a net refund.</summary>
        public long CostSum;

        public override string ToString() => (ToolTypeName ?? "tool " + ToolKind) + " " + Prefab + " cost " + CostSum;
    }
}
