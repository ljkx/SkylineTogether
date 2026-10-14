namespace SkylineTogether.Core
{
    /// <summary>
    /// Kinds of derived keys (docs/history/ARCHITECTURE-original.md §3.7). The numeric values are the subkinds of the extended <c>NetRef</c> tag on the
    /// wire (docs/history/ARCHITECTURE-original.md §4.3.3) and must never change.
    /// </summary>
    public enum DerivedKeyKind : byte
    {
        /// <summary>A lane of a net or building owner (owner slot, lane prefab, master/slave flags, path nodes).</summary>
        Lane = 1,

        /// <summary>A prefab sub-object (owner slot, prefab, aligned index or quantised owner-space position).</summary>
        SubObject = 2,

        /// <summary>A secondary object (owner slot, prefab, quantised position).</summary>
        SecondaryObject = 3,

        /// <summary>A zone block (owner edge slot, side, quantised position, size).</summary>
        Block = 4,

        /// <summary>A route owned by an object, e.g. a gate-bypass path (owner slot, route prefab).</summary>
        OwnedRoute = 5,

        /// <summary>An electricity or water flow edge (start owner, end owner, role).</summary>
        FlowEdge = 6,

        /// <summary>A waypoint or segment of an owned route (owned-route key, element kind, element index).</summary>
        OwnedRouteElement = 7
    }
}
