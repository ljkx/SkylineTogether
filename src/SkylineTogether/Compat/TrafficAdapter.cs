using System.Collections.Generic;

namespace SkylineTogether.Compat
{
    /// <summary>
    /// Traffic (krzychu124, Paradox Mods 80095): replaces vanilla lane generation (<c>TrafficLaneSystem</c>, vanilla
    /// <c>LaneSystem</c> off) and keeps lane connections and priority signs as save data:
    /// <list type="bullet">
    /// <item>nodes: <c>ModifiedLaneConnections</c> (buffer: lane index, carriageway/group, lane position, edge, and a
    /// reference to a <b>data entity</b>) and the <c>ModifiedConnections</c> tag;</item>
    /// <item>data entities: <c>DataOwner</c> (the node), <c>GeneratedConnection</c> (buffer of source/target edges and
    /// lane index maps) and <c>PrefabRef(Traffic.FakePrefab)</c>; they classify as mod entities and are bound and sent
    /// together with the node that references them (dependency binding in <c>EntityCodec</c>); their in-place edits are
    /// found by the <see cref="ModStateWatcher"/>;</item>
    /// <item>edges: <c>LanePriority</c> (buffer of lane indices and sign) and the <c>ModifiedPriorities</c> tag.</item>
    /// </list>
    /// No value references a lane entity (only edges, nodes and composition lane indices), so every peer re-derives the
    /// same lanes from the replicated data when the applier tags the node and its closure <c>Updated</c>; lane derived keys
    /// (movers, parked cars) then resolve against those lanes as against vanilla ones. The tools run their own Temp
    /// pipeline (<c>ApplyLaneConnectionsSystem</c>/<c>ApplyPrioritiesSystem</c> in ApplyTool), so a client's edit is
    /// predicted locally and proposed like any tool apply. Its tool-only entities below never replicate. Traffic's
    /// settings are UI/overlay only. Must be on every peer in the same version.
    /// </summary>
    public sealed class TrafficAdapter : ModAdapter
    {
        public override string ModName => "Traffic";

        public override string AssemblyName => "Traffic";

        public override IEnumerable<string> LocalTypes => new[]
        {
            "Traffic.Components.DataTemp",
            "Traffic.Components.EditIntersection",
            "Traffic.Components.LaneConnections.Connector",
            "Traffic.Components.LaneConnections.ConnectionDefinition",
            "Traffic.Components.PrioritySigns.LaneHandle",
            "Traffic.Components.PrioritySigns.PriorityDefinition",
            "Traffic.CommonData.EditLaneConnections",
            "Traffic.CommonData.EditPriorities",
            "Traffic.CommonData.ActionOverlayData",
        };

        public override IEnumerable<string> PipelineTools => new[] { "Traffic.Tools.LaneConnectorToolSystem", "Traffic.Tools.PriorityToolSystem" };

        public override string Summary => "lane connections and priorities replicate as node/edge data with their data entities; lanes re-derived identically on every peer";
    }
}
