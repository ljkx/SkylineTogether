using System.Collections.Generic;

namespace SkylineTogether.Compat
{
    /// <summary>
    /// Move It (Quboid, Paradox Mods 74324): a direct-write tool. Moves, rotations, multi-selection actions, control
    /// point edits and undo/redo all write <c>Transform</c>, node positions, curves, area nodes and elevations of
    /// persistent entities directly and tag them <c>Updated</c>; there is no Temp pipeline and no serializable Move It
    /// component. The capture treats the active Move It tool as a direct-write tool (proposals flagged
    /// <c>DirectTool</c>), and the client's edit hold (<c>SyncController</c>) keeps the host's echo of an older drag
    /// position from snapping a dragged object back while the player is still moving it (no ping-pong). Control points
    /// (<c>PrefabRef(Entity.Null)</c> + <c>MIT_ControlPoint</c>) and overlay entities are tool-local.
    /// </summary>
    public sealed class MoveItAdapter : ModAdapter
    {
        public override string ModName => "Move It";

        public override string AssemblyName => "MoveIt";

        public override IEnumerable<string> LocalTypes => new[]
        {
            "MoveIt.Components.MIT_ControlPoint",
            "MoveIt.Overlays.MIO_Common",
        };

        public override string Summary => "direct-write tool: captured as change sets both ways, with an edit hold against echoes; control points tool-local";
    }
}
