namespace SkylineTogether.Compat
{
    /// <summary>
    /// Zoning Toolkit (Paradox Mods 75750): <c>ZoningInfo</c> on road edges replicates in the edge's record; the tool
    /// writes it directly with update tags (direct-write tool). Every peer re-derives the zone blocks from the edge's
    /// <c>ZoningInfo</c> (the mod's block system uses the edge's value for edges that were not applied locally), so block
    /// keys and zone cells match. The zoning mode for new roads is a per-player tool choice that travels inside the
    /// proposed edge.
    /// </summary>
    public sealed class ZoningToolkitAdapter : ModAdapter
    {
        public override string ModName => "Zoning Toolkit";

        public override string AssemblyName => "ZoningToolkit";

        public override string Summary => "ZoningInfo replicates with edges; blocks re-derived identically";
    }

    /// <summary>
    /// Line Tool (algernon, Paradox Mods 75816): places objects through the vanilla creation-definition pipeline; the
    /// applied objects (with their random seeds already resolved) are proposed like any tool apply. No state of its own.
    /// </summary>
    public sealed class LineToolAdapter : ModAdapter
    {
        public override string ModName => "Line Tool";

        public override string AssemblyName => "LineTool";

        public override System.Collections.Generic.IEnumerable<string> PipelineTools => new[] { "LineTool.LineToolSystem" };

        public override string Summary => "vanilla pipeline; results replicate";
    }

    /// <summary>
    /// Find It (Paradox Mods 77240): a prefab browser and picker (client-local UI); placement uses the vanilla tools. Its
    /// generated prop variants are runtime prefabs: they resolve by PrefabID on peers that have Find It, and a peer
    /// without it reports them as missing assets instead of creating broken objects.
    /// </summary>
    public sealed class FindItAdapter : ModAdapter
    {
        public override string ModName => "Find It";

        public override string AssemblyName => "FindIt";

        public override System.Collections.Generic.IEnumerable<string> PipelineTools => new[] { "FindIt.Systems.PickerToolSystem" };

        public override string Summary => "client-local UI; generated prefabs resolved by PrefabID, missing ones reported";
    }
}
