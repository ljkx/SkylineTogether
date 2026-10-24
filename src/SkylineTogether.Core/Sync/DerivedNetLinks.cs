using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// The net-side links where one direction travels on the wire and the other is derived on every peer
    /// (ARCHITECTURE.md#derived-net-links). The game keeps both directions in step itself and follows them in Burst jobs
    /// without a check; the applier writes only the replicated direction.
    /// </summary>
    public enum DerivedNetLink : byte
    {
        /// <summary><c>Game.Net.Aggregated.m_Aggregate</c> of an edge (presence only on the wire; the game derives it from the lists on load).</summary>
        AggregatePointer,
        /// <summary><c>Game.Net.AggregateElement.m_Edge</c>, the edge list of a street aggregate (replicated).</summary>
        AggregateElement,
        /// <summary><c>Game.Buildings.Building.m_RoadEdge</c> of a building (replicated).</summary>
        RoadEdge,
        /// <summary><c>Game.Buildings.ConnectedBuilding.m_Building</c>, the buildings an edge lists (presence only on the wire).</summary>
        ConnectedBuilding,
        /// <summary><c>Game.Net.ConnectedEdge.m_Edge</c>, the edges a node lists (presence only on the wire; derived from the edges' nodes).</summary>
        ConnectedEdge,
        /// <summary><c>Game.Net.ConnectedNode.m_Node</c>, the middle connections of an edge (replicated).</summary>
        MiddleNode,
        /// <summary><c>Game.Net.Edge.m_Start/m_End</c> of an edge (replicated); an edge whose node is gone is deleted.</summary>
        EdgeNode,
    }

    /// <summary>
    /// Rules for <see cref="DerivedNetLink"/>: what the game itself guarantees and a peer whose two directions came from
    /// different sources (host records, its own derivation, the join snapshot) must restore before a game system reads them.
    /// <list type="bullet">
    /// <item><c>AggregateSystem</c> (Modification2B) indexes the list of the aggregate every modified edge names and of the
    /// aggregates its neighbours name (<c>m_AggregateElements[...]</c>, <c>dynamicBuffer[0]</c>), then the prefab of every
    /// listed edge; <c>AggregateMeshSystem</c> (PreCulling) indexes <c>Edge</c> and the nodes' <c>ConnectedEdge</c> of every
    /// listed edge of an updated aggregate.</item>
    /// <item><c>RoadConnectionSystem</c> (Modification4B) indexes the building of every entry of a modified edge's list and
    /// the geometry and list of every building's <c>m_RoadEdge</c> near a modified edge.</item>
    /// <item><c>Net.ReferencesSystem</c> (Modification2B) indexes the edge list of every modified node and of every node a
    /// modified edge names (<c>m_Edges[edge.m_Start]</c>, <c>m_Nodes[edge]</c> for every entry of a deleted node); the
    /// geometry, lane, composition and search systems, <c>AggregateMeshSystem</c> and many simulation systems iterate a
    /// node's edge list and index each entry's components.</item>
    /// </list>
    /// A reference to a destroyed entity there is a native crash (no managed stack). Pure.
    /// </summary>
    public static class DerivedNetLinkRules
    {
        /// <summary>Number of <see cref="DerivedNetLink"/> values.</summary>
        public const int Count = 7;

        /// <summary>The field's name as the log writes it.</summary>
        public static string Name(DerivedNetLink link)
        {
            switch (link)
            {
                case DerivedNetLink.AggregatePointer: return "Net.Aggregated.m_Aggregate";
                case DerivedNetLink.AggregateElement: return "Net.AggregateElement.m_Edge";
                case DerivedNetLink.RoadEdge: return "Buildings.Building.m_RoadEdge";
                case DerivedNetLink.ConnectedBuilding: return "Buildings.ConnectedBuilding.m_Building";
                case DerivedNetLink.ConnectedEdge: return "Net.ConnectedEdge.m_Edge";
                case DerivedNetLink.MiddleNode: return "Net.ConnectedNode.m_Node";
                case DerivedNetLink.EdgeNode: return "Net.Edge.m_Start/m_End";
                default: return "?";
            }
        }

        /// <summary>
        /// A pointer (<see cref="DerivedNetLink.AggregatePointer"/>, <see cref="DerivedNetLink.RoadEdge"/>) is cleared when it
        /// names something that is not a usable target: destroyed, of the wrong kind, being deleted this frame (it is gone
        /// after the frame), or an aggregate whose list is empty (<c>ShouldCombine</c> reads element 0). Null is normal for
        /// both fields (an edge without a street, a building without a road).
        /// </summary>
        public static bool MustClear(bool isNull, bool usable, bool dying) => !isNull && (!usable || dying);

        /// <summary>
        /// Removes the list elements that <paramref name="keep"/> rejects, keeping the order of the rest (the aggregate list
        /// is the street's order; the game rebuilds it from the first kept edge). Returns the number removed.
        /// </summary>
        public static int Prune<T>(IList<T> elements, Func<T, bool> keep)
        {
            if (elements == null) throw new ArgumentNullException(nameof(elements));
            if (keep == null) throw new ArgumentNullException(nameof(keep));
            int write = 0;
            for (int read = 0; read < elements.Count; read++)
            {
                T e = elements[read];
                if (!keep(e)) continue;
                if (write != read) elements[write] = e;
                write++;
            }
            int removed = elements.Count - write;
            for (int i = elements.Count - 1; i >= write; i--) elements.RemoveAt(i);
            return removed;
        }
    }

    /// <summary>One change the membership pass makes (see <see cref="AggregateMembership"/>).</summary>
    public readonly struct MembershipOp<TEntity>
    {
        public MembershipOp(TEntity edge, TEntity from, TEntity to)
        {
            Edge = edge;
            From = from;
            To = to;
        }

        /// <summary>The edge whose pointer changes.</summary>
        public TEntity Edge { get; }

        /// <summary>The aggregate it named before (its list loses the edge when it is a usable aggregate other than <see cref="To"/>).</summary>
        public TEntity From { get; }

        /// <summary>The aggregate whose list names the edge.</summary>
        public TEntity To { get; }
    }

    /// <summary>
    /// The street membership of edges after a peer wrote an aggregate's edge list from a record (host batch, proposal):
    /// every listed edge names that aggregate and leaves the list of the aggregate it named before, exactly as the game sets
    /// <c>Aggregated</c> from the lists when a city loads (<c>Game.Serialization.AggregatedSystem</c>). Without it the edges of
    /// a street the host extended kept the client's own aggregate while the host's aggregate listed them too; deleting such
    /// an edge cleaned one list only, and the other list kept a destroyed edge. Pure.
    /// </summary>
    public static class AggregateMembership
    {
        /// <summary>
        /// Adds to <paramref name="ops"/> one operation per listed edge that names another aggregate (or none).
        /// <paramref name="usableEdge"/> says whether a listed element is an edge the pass may write (it exists, has the
        /// pointer component, is neither deleted nor a tool preview). Duplicates in the list are planned once. Returns the
        /// number of operations added.
        /// </summary>
        public static int Plan<TEntity>(TEntity aggregate, IReadOnlyList<TEntity> elements, Func<TEntity, TEntity> pointerOf, Func<TEntity, bool> usableEdge, List<MembershipOp<TEntity>> ops)
            where TEntity : IEquatable<TEntity>
        {
            if (elements == null || ops == null || pointerOf == null || usableEdge == null) return 0;
            int added = 0;
            HashSet<TEntity> seen = null;
            for (int i = 0; i < elements.Count; i++)
            {
                TEntity edge = elements[i];
                if (!usableEdge(edge)) continue;
                if (elements.Count > 1)
                {
                    seen ??= new HashSet<TEntity>();
                    if (!seen.Add(edge)) continue;
                }
                TEntity current = pointerOf(edge);
                if (current.Equals(aggregate)) continue;
                ops.Add(new MembershipOp<TEntity>(edge, current, aggregate));
                added++;
            }
            return added;
        }
    }

    /// <summary>
    /// The deletions of one frame that the derived links must not outlive (<see cref="DerivedNetLink"/>): at the end of the
    /// frame's Modification phases, after the game cleaned the lists and pointers it knows of, whatever still names one of
    /// these entities is cleared, since they are destroyed when the frame ends. Edges, nodes, aggregates and buildings are
    /// noted separately; tool previews (<c>Temp</c>) are never noted (the tool regenerates them). Main thread only.
    /// </summary>
    public sealed class DerivedLinkDeletions<TEntity> where TEntity : IEquatable<TEntity>
    {
        public readonly HashSet<TEntity> Edges = new HashSet<TEntity>();
        public readonly HashSet<TEntity> Aggregates = new HashSet<TEntity>();
        public readonly HashSet<TEntity> Buildings = new HashSet<TEntity>();
        public readonly HashSet<TEntity> Nodes = new HashSet<TEntity>();

        /// <summary>Aggregates whose lists became empty while pruning (a pointer to one is cleared like one to a deleted aggregate).</summary>
        public readonly HashSet<TEntity> Emptied = new HashSet<TEntity>();

        public bool HasWork => Edges.Count + Aggregates.Count + Buildings.Count + Nodes.Count > 0;

        /// <summary>A building's road edge or an aggregate's element names a deleted edge.</summary>
        public bool IsDeletedEdge(TEntity edge) => Edges.Contains(edge);

        /// <summary>An edge's pointer names an aggregate that is deleted or was emptied.</summary>
        public bool IsGoneAggregate(TEntity aggregate) => Aggregates.Contains(aggregate) || Emptied.Contains(aggregate);

        /// <summary>An edge's list names a deleted building.</summary>
        public bool IsDeletedBuilding(TEntity building) => Buildings.Contains(building);

        /// <summary>An edge's start, end or middle connection names a deleted node.</summary>
        public bool IsDeletedNode(TEntity node) => Nodes.Contains(node);

        public void Clear()
        {
            Edges.Clear();
            Aggregates.Clear();
            Buildings.Clear();
            Nodes.Clear();
            Emptied.Clear();
        }
    }

    /// <summary>
    /// The nodes an edge stops naming when a record rewrites its <c>Edge</c> (start, end) or its middle connections
    /// (<c>ConnectedNode</c>). A node's edge list (<c>ConnectedEdge</c>) is derived by <c>Net.ReferencesSystem</c>, which
    /// takes an updated edge out of a node's list only when that node is updated in the same frame, and adds it to the nodes
    /// it names now; a deleted edge leaves the lists of the nodes it names at that moment. A node the edge left without being
    /// updated keeps it in its list, and once the edge is deleted that entry names a destroyed entity: the native crash of
    /// a joined player building next to such a node. The game updates the nodes an edge leaves itself
    /// (<c>Game.Tools.ApplyNetSystem</c>); the applier does the same with the nodes this reports. Pure.
    /// </summary>
    public static class EdgeNodeChange
    {
        /// <summary>
        /// Adds to <paramref name="into"/> every node of the old ends and middle connections that the new ones no longer
        /// name, once each; <paramref name="none"/> (the null entity) is never added. Returns the number added.
        /// </summary>
        public static int NodesLeft<TEntity>(TEntity oldStart, TEntity oldEnd, IReadOnlyList<TEntity> oldMiddles, TEntity newStart, TEntity newEnd,
                                             IReadOnlyList<TEntity> newMiddles, TEntity none, List<TEntity> into)
            where TEntity : IEquatable<TEntity>
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            int added = 0;
            if (Left(oldStart, newStart, newEnd, newMiddles, none, into)) added++;
            if (Left(oldEnd, newStart, newEnd, newMiddles, none, into)) added++;
            if (oldMiddles == null) return added;
            for (int i = 0; i < oldMiddles.Count; i++)
            {
                if (Left(oldMiddles[i], newStart, newEnd, newMiddles, none, into)) added++;
            }
            return added;
        }

        /// <summary>
        /// Whether the edge names the same nodes before and after (ends in place, middle connections as a set): when it
        /// does, the node lists stay right without any update.
        /// </summary>
        public static bool SameNodes<TEntity>(TEntity oldStart, TEntity oldEnd, IReadOnlyList<TEntity> oldMiddles, TEntity newStart, TEntity newEnd, IReadOnlyList<TEntity> newMiddles)
            where TEntity : IEquatable<TEntity>
        {
            if (!oldStart.Equals(newStart) || !oldEnd.Equals(newEnd)) return false;
            int oldCount = oldMiddles?.Count ?? 0, newCount = newMiddles?.Count ?? 0;
            if (oldCount != newCount) return false;
            for (int i = 0; i < oldCount; i++)
            {
                if (!Contains(newMiddles, oldMiddles[i]) || !Contains(oldMiddles, newMiddles[i])) return false;
            }
            return true;
        }

        private static bool Left<TEntity>(TEntity node, TEntity newStart, TEntity newEnd, IReadOnlyList<TEntity> newMiddles, TEntity none, List<TEntity> into)
            where TEntity : IEquatable<TEntity>
        {
            if (node.Equals(none) || node.Equals(newStart) || node.Equals(newEnd) || Contains(newMiddles, node)) return false;
            for (int i = 0; i < into.Count; i++)
            {
                if (into[i].Equals(node)) return false;
            }
            into.Add(node);
            return true;
        }

        private static bool Contains<TEntity>(IReadOnlyList<TEntity> list, TEntity e) where TEntity : IEquatable<TEntity>
        {
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Equals(e)) return true;
            }
            return false;
        }
    }
}
