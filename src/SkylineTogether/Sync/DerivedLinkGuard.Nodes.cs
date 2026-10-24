using System;
using System.Collections.Generic;
using Game.Common;
using Game.Net;
using SkylineTogether.Core.Sync;
using SkylineTogether.Diagnostics;
using Unity.Collections;
using Unity.Entities;

namespace SkylineTogether.Sync
{
    // The node and edge lists of the derived net links (ARCHITECTURE.md#derived-net-links): a node's edge list
    // (ConnectedEdge, derived), an edge's middle connections (ConnectedNode) and its ends (Edge), both replicated.
    internal sealed partial class DerivedLinkGuard
    {
        private EntityQuery _deletedNodes, _allNodes, _allEdges, _modifiedNodes, _modifiedEdges;
        private readonly IndexVersionSet _deletedEdgeSet = new IndexVersionSet();
        private readonly HashSet<Entity> _nodes = new HashSet<Entity>();
        private readonly HashSet<Entity> _rebuild = new HashSet<Entity>();
        private readonly HashSet<Entity> _broken = new HashSet<Entity>();
        private readonly List<Entity> _work = new List<Entity>();
        private long _rederived, _brokenDeleted, _nodeScans;

        /// <summary>
        /// An edge this guard deleted because a node it names is gone (a client releases its id and asks the host for it; the
        /// host's capture broadcasts the despawn); set by the controller.
        /// </summary>
        public Action<Entity> EdgeDeleted;

        private void EnsureNodes(EntityManager em)
        {
            ComponentType temp = ComponentType.ReadOnly<Game.Tools.Temp>();
            ComponentType deleted = ComponentType.ReadOnly<Deleted>();
            ComponentType[] changed = { ComponentType.ReadOnly<Updated>(), deleted };
            EntityQuery Q(ComponentType[] all, ComponentType[] any, ComponentType[] none) =>
                em.CreateEntityQuery(new EntityQueryDesc { All = all, Any = any ?? Array.Empty<ComponentType>(), None = none });
            ComponentType[] node = { ComponentType.ReadOnly<Node>(), ComponentType.ReadOnly<ConnectedEdge>() };
            _deletedNodes = Q(new[] { ComponentType.ReadOnly<Node>(), deleted }, null, new[] { temp });
            _allNodes = Q(node, null, new[] { temp, deleted });
            _allEdges = Q(new[] { ComponentType.ReadOnly<Edge>() }, null, new[] { temp, deleted });
            _modifiedNodes = Q(node, changed, new[] { temp });
            _modifiedEdges = Q(new[] { ComponentType.ReadOnly<Edge>() }, changed, new[] { temp });
        }

        private void ResetNodes()
        {
            _deletedEdgeSet.Clear();
            _nodes.Clear();
            _rebuild.Clear();
            _broken.Clear();
            _work.Clear();
            _rederived = _brokenDeleted = _nodeScans = 0;
        }

        private string DescribeNodes() =>
            _nodeScans + " node list scans, " + _rederived + " nodes and edges re-derived after a repair, " + _brokenDeleted + " edges deleted whose node was gone";

        // ================================================================== usable targets

        /// <summary>A node an edge may name: it exists and has its edge list (<c>Net.ReferencesSystem</c> indexes <c>m_Edges[node]</c>).</summary>
        private static bool IsUsableNode(EntityManager em, Entity n) => n != Entity.Null && em.Exists(n) && em.HasBuffer<ConnectedEdge>(n);

        /// <summary>An entry a node's edge list may hold: an edge (the game's jobs index its <c>Edge</c>, curve, composition).</summary>
        private static bool UsableEdge(EntityManager em, Entity e) => em.Exists(e) && em.HasComponent<Edge>(e);

        // ================================================================== repairs

        /// <summary>Removes the entries of a node's edge list that <paramref name="keep"/> rejects (order kept); returns the number removed.</summary>
        private int PruneNodeEdges(EntityManager em, Entity node, Func<Entity, bool> keep, string why)
        {
            DynamicBuffer<ConnectedEdge> ro = em.GetBuffer<ConnectedEdge>(node, true);
            Entity first = Entity.Null;
            bool any = false;
            for (int i = 0; i < ro.Length && !any; i++)
            {
                if (keep(ro[i].m_Edge)) continue;
                any = true;
                first = ro[i].m_Edge;
            }
            if (!any) return 0;
            DynamicBuffer<ConnectedEdge> rw = em.GetBuffer<ConnectedEdge>(node);
            int removed = 0;
            for (int i = rw.Length - 1; i >= 0; i--)
            {
                if (keep(rw[i].m_Edge)) continue;
                rw.RemoveAt(i);
                removed++;
            }
            _repairs[(int)DerivedNetLink.ConnectedEdge] += removed;
            Forensics.Cleanup("derived links", em, node, DerivedNetLinkRules.Name(DerivedNetLink.ConnectedEdge) + ": " + removed + " entr(ies) dropped, e.g. "
                                                          + Forensics.Describe(em, first) + " (" + why + ")");
            return removed;
        }

        /// <summary>Removes the middle connections of an edge that <paramref name="keep"/> rejects; returns the number removed.</summary>
        private int PruneMiddleNodes(EntityManager em, Entity edge, Func<Entity, bool> keep, string why)
        {
            if (!em.HasBuffer<ConnectedNode>(edge)) return 0;
            DynamicBuffer<ConnectedNode> ro = em.GetBuffer<ConnectedNode>(edge, true);
            Entity first = Entity.Null;
            bool any = false;
            for (int i = 0; i < ro.Length && !any; i++)
            {
                if (keep(ro[i].m_Node)) continue;
                any = true;
                first = ro[i].m_Node;
            }
            if (!any) return 0;
            DynamicBuffer<ConnectedNode> rw = em.GetBuffer<ConnectedNode>(edge);
            int removed = 0;
            for (int i = rw.Length - 1; i >= 0; i--)
            {
                if (keep(rw[i].m_Node)) continue;
                rw.RemoveAt(i);
                removed++;
            }
            _repairs[(int)DerivedNetLink.MiddleNode] += removed;
            Forensics.Cleanup("derived links", em, edge, DerivedNetLinkRules.Name(DerivedNetLink.MiddleNode) + ": " + removed + " middle connection(s) dropped, e.g. "
                                                          + Forensics.Describe(em, first) + " (" + why + ")");
            return removed;
        }

        /// <summary>Whether an edge names a start or end node that is gone or no node.</summary>
        private static bool HasBrokenEnd(EntityManager em, Entity edge)
        {
            Edge ends = em.GetComponentData<Edge>(edge);
            return !IsUsableNode(em, ends.m_Start) || !IsUsableNode(em, ends.m_End);
        }

        /// <summary>
        /// Deletes an edge whose start or end node is gone (nothing else can be cleared: the game has no edge without both
        /// nodes), the way the applier deletes a dangling edge: the gone node is replaced by the other one, so
        /// <c>Net.ReferencesSystem</c> takes the edge out of the list of the node it keeps (<see cref="EntityCodec.SanitizeForDelete"/>),
        /// and that node is re-derived with it.
        /// </summary>
        private void DeleteBroken(EntityManager em, Entity edge, string why)
        {
            Edge ends = em.GetComponentData<Edge>(edge);
            Entity gone = IsUsableNode(em, ends.m_Start) ? ends.m_End : ends.m_Start;
            Forensics.Cleanup("derived links", em, edge, DerivedNetLinkRules.Name(DerivedNetLink.EdgeNode) + " named " + Forensics.Describe(em, gone) + " (" + why + ") -> edge deleted");
            EntityCodec.SanitizeForDelete(em, edge);
            if (em.HasComponent<Edge>(edge))
            {
                Entity kept = em.GetComponentData<Edge>(edge).m_Start;
                if (IsUsableNode(em, kept)) Touch(em, kept);
            }
            em.AddComponent<Deleted>(edge);
            _repairs[(int)DerivedNetLink.EdgeNode]++;
            _brokenDeleted++;
            EdgeDeleted?.Invoke(edge);
        }

        /// <summary>Tags an entity <c>Updated</c> (never a preview or one being deleted); true when it was tagged.</summary>
        private static bool Touch(EntityManager em, Entity e)
        {
            if (!em.Exists(e) || IsTemp(em, e) || em.HasComponent<Deleted>(e) || em.HasComponent<Updated>(e)) return false;
            em.AddComponent<Updated>(e);
            return true;
        }

        /// <summary>
        /// Re-derives a node whose list or an edge whose middle connections were repaired, with the net closure the applier
        /// tags (a node, its edges and their nodes; an edge and its nodes): the game rebuilds geometry, composition and lanes
        /// of an updated node from its list, and a node that kept a destroyed edge was built with it.
        /// </summary>
        private void Rederive(EntityManager em, Entity e)
        {
            if (!em.Exists(e) || IsTemp(em, e) || em.HasComponent<Deleted>(e)) return;
            _work.Clear();
            if (em.HasComponent<Edge>(e))
            {
                Edge ends = em.GetComponentData<Edge>(e);
                _work.Add(e);
                _work.Add(ends.m_Start);
                _work.Add(ends.m_End);
            }
            else if (em.HasBuffer<ConnectedEdge>(e))
            {
                _work.Add(e);
                DynamicBuffer<ConnectedEdge> list = em.GetBuffer<ConnectedEdge>(e, true);
                for (int i = 0; i < list.Length; i++)
                {
                    Entity edge = list[i].m_Edge;
                    if (!UsableEdge(em, edge)) continue;
                    Edge ends = em.GetComponentData<Edge>(edge);
                    _work.Add(edge);
                    _work.Add(ends.m_Start);
                    _work.Add(ends.m_End);
                }
            }
            bool any = false;
            foreach (Entity x in _work)
            {
                if (x != Entity.Null && Touch(em, x)) any = true;
            }
            if (any) _rederived++;
            _work.Clear();
        }

        /// <summary>Notes a repaired node or edge for its re-derivation at the start of the next frame (unless it is updated anyway).</summary>
        private void NoteRebuild(EntityManager em, Entity e)
        {
            if (em.Exists(e) && !em.HasComponent<Updated>(e) && !em.HasComponent<Deleted>(e)) _rebuild.Add(e);
        }

        // ================================================================== end of the Modification phases

        /// <summary>
        /// ModificationEnd (part of <see cref="EndOfModification"/>): the game took every edge deleted this frame out of the
        /// lists of the nodes it names now; a node it left without both being updated in the same frame still lists it and
        /// would name a destroyed edge from the next frame on (every node list is scanned, a bitmap test per entry). An edge
        /// still ending at a node deleted this frame is deleted at the start of the next frame.
        /// </summary>
        private void DeletedNodesAndEdges(EntityManager em)
        {
            if (_deleted.Edges.Count > 0)
            {
                _deletedEdgeSet.Clear();
                int min = int.MaxValue, max = int.MinValue;
                foreach (Entity e in _deleted.Edges)
                {
                    _deletedEdgeSet.Add(e.Index, e.Version);
                    min = Math.Min(min, e.Index);
                    max = Math.Max(max, e.Index);
                }
                _nodeScans++;
                FindNodesListing(em, min, max, _hits);
                foreach (Entity n in _hits)
                {
                    if (PruneNodeEdges(em, n, e => !_deletedEdgeSet.Contains(e.Index, e.Version), "deleted this frame") > 0) NoteRebuild(em, n);
                }
                _deletedEdgeSet.Clear();
            }
            if (_deleted.Nodes.Count > 0)
            {
                foreach (Entity node in _deleted.Nodes)
                {
                    if (!em.Exists(node) || !em.HasBuffer<ConnectedEdge>(node)) continue;
                    DynamicBuffer<ConnectedEdge> list = em.GetBuffer<ConnectedEdge>(node, true);
                    for (int i = 0; i < list.Length; i++)
                    {
                        Entity edge = list[i].m_Edge;
                        if (!UsableEdge(em, edge) || IsTemp(em, edge) || em.HasComponent<Deleted>(edge)) continue;
                        Edge ends = em.GetComponentData<Edge>(edge);
                        if (ends.m_Start == node || ends.m_End == node) _broken.Add(edge);
                        else _nodes.Add(edge); // a middle connection the game did not drop
                    }
                }
                foreach (Entity edge in _nodes)
                {
                    if (PruneMiddleNodes(em, edge, n => !_deleted.IsDeletedNode(n), "deleted this frame") > 0) NoteRebuild(em, edge);
                }
                _nodes.Clear();
            }
        }

        /// <summary>The nodes (no previews, none being deleted) whose list has an entry in <see cref="_deletedEdgeSet"/>.</summary>
        private void FindNodesListing(EntityManager em, int min, int max, List<Entity> into)
        {
            into.Clear();
            if (_allNodes.IsEmptyIgnoreFilter) return;
            _allNodes.CompleteDependency();
            BufferTypeHandle<ConnectedEdge> handle = em.GetBufferTypeHandle<ConnectedEdge>(true);
            EntityTypeHandle entityType = em.GetEntityTypeHandle();
            NativeArray<ArchetypeChunk> chunks = _allNodes.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                foreach (ArchetypeChunk chunk in chunks)
                {
                    BufferAccessor<ConnectedEdge> lists = chunk.GetBufferAccessor(ref handle);
                    NativeArray<Entity> holders = chunk.GetNativeArray(entityType);
                    for (int i = 0; i < lists.Length; i++)
                    {
                        DynamicBuffer<ConnectedEdge> list = lists[i];
                        for (int k = 0; k < list.Length; k++)
                        {
                            Entity e = list[k].m_Edge;
                            if (e.Index < min || e.Index > max || !_deletedEdgeSet.Contains(e.Index, e.Version)) continue;
                            into.Add(holders[i]);
                            break;
                        }
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }
        }

        private static readonly Func<ConnectedEdge, Entity> NodeEdgeOf = x => x.m_Edge;
        private static readonly Func<ConnectedNode, Entity> MiddleOf = x => x.m_Node;

        /// <summary>The once-per-session pass (part of <see cref="FullPass"/>): every node list, middle connection and edge end.</summary>
        private void FullPassNodes(EntityManager em)
        {
            FindLists(em, _allNodes, NodeEdgeOf, e => !UsableEdge(em, e), _hits);
            foreach (Entity n in _hits)
            {
                if (PruneNodeEdges(em, n, e => UsableEdge(em, e), "destroyed or not an edge") > 0) NoteRebuild(em, n);
            }
            FindLists(em, _allEdges, MiddleOf, n => !IsUsableNode(em, n), _hits);
            foreach (Entity e in _hits)
            {
                if (PruneMiddleNodes(em, e, n => IsUsableNode(em, n), "destroyed or not a node") > 0) NoteRebuild(em, e);
            }
            Collect(_allEdges, _hits);
            foreach (Entity e in _hits)
            {
                if (HasBrokenEnd(em, e)) _broken.Add(e);
            }
            _hits.Clear();
        }

        // ================================================================== right before the game reads them

        /// <summary>
        /// Modification2B, right before <c>Net.ReferencesSystem</c>: the lists it indexes. The edge lists of the modified
        /// nodes and of the nodes the modified edges name (<c>m_Nodes[edge]</c> for every entry of a deleted node; the
        /// geometry and lane systems of this frame iterate them) name edges; the modified edges' middle connections and ends
        /// name nodes (<c>m_Edges[node]</c>). A deleted edge whose node is gone is made safe; a kept one is deleted.
        /// </summary>
        public void BeforeNetReferences(EntityManager em)
        {
            Ensure(em);
            bool nodes = !_modifiedNodes.IsEmptyIgnoreFilter, edges = !_modifiedEdges.IsEmptyIgnoreFilter;
            if (!nodes && !edges) return;
            _nodes.Clear();
            if (edges)
            {
                Collect(_modifiedEdges, _hits);
                foreach (Entity e in _hits)
                {
                    if (!em.Exists(e) || !em.HasComponent<Edge>(e)) continue;
                    PruneMiddleNodes(em, e, n => IsUsableNode(em, n), "destroyed or not a node");
                    Edge ends = em.GetComponentData<Edge>(e);
                    if (HasBrokenEnd(em, e))
                    {
                        if (!em.HasComponent<Deleted>(e)) DeleteBroken(em, e, "destroyed or not a node");
                        else if (EntityCodec.SanitizeForDelete(em, e))
                        {
                            _repairs[(int)DerivedNetLink.EdgeNode]++;
                            Forensics.Cleanup("derived links", em, e, DerivedNetLinkRules.Name(DerivedNetLink.EdgeNode) + " of a deleted edge named a node that is gone -> made safe");
                        }
                        ends = em.HasComponent<Edge>(e) ? em.GetComponentData<Edge>(e) : default;
                    }
                    if (IsUsableNode(em, ends.m_Start)) _nodes.Add(ends.m_Start);
                    if (IsUsableNode(em, ends.m_End)) _nodes.Add(ends.m_End);
                }
            }
            if (nodes)
            {
                Collect(_modifiedNodes, _hits);
                foreach (Entity n in _hits) _nodes.Add(n);
            }
            _hits.Clear();
            foreach (Entity n in _nodes)
            {
                if (!em.Exists(n) || IsTemp(em, n) || !em.HasBuffer<ConnectedEdge>(n)) continue;
                if (PruneNodeEdges(em, n, e => UsableEdge(em, e), "destroyed or not an edge") > 0) NoteRebuild(em, n);
            }
            _nodes.Clear();
        }

        /// <summary>PreCulling (part of <see cref="BeforeAggregateMeshes"/>): the edge lists of the nodes of a street's edges.</summary>
        private void CheckListedEdgeNodes(EntityManager em, Entity aggregate)
        {
            DynamicBuffer<AggregateElement> elements = em.GetBuffer<AggregateElement>(aggregate, true);
            _nodes.Clear();
            for (int i = 0; i < elements.Length; i++)
            {
                Entity e = elements[i].m_Edge;
                if (!UsableEdge(em, e)) continue;
                Edge ends = em.GetComponentData<Edge>(e);
                if (IsUsableNode(em, ends.m_Start)) _nodes.Add(ends.m_Start);
                if (IsUsableNode(em, ends.m_End)) _nodes.Add(ends.m_End);
            }
            foreach (Entity n in _nodes)
            {
                if (IsTemp(em, n)) continue;
                if (PruneNodeEdges(em, n, x => UsableEdge(em, x), "destroyed or not an edge") > 0) NoteRebuild(em, n);
            }
            _nodes.Clear();
        }

        // ================================================================== the start of the next frame

        /// <summary>
        /// Frame start, before the apply and every game system (<c>SyncController.ApplyIncoming</c>): the edges found ending
        /// at a node that is gone are deleted, and the nodes and edges whose lists were repaired after the Modification
        /// phases are tagged with their closure, so this frame's Modification phases re-derive them like any edit.
        /// </summary>
        public void BeforeFrame(EntityManager em)
        {
            if (_broken.Count == 0 && _rebuild.Count == 0) return;
            Ensure(em);
            if (_broken.Count > 0)
            {
                _work.Clear();
                _work.AddRange(_broken);
                _broken.Clear();
                foreach (Entity e in _work)
                {
                    if (!em.Exists(e) || IsTemp(em, e) || em.HasComponent<Deleted>(e) || !em.HasComponent<Edge>(e) || !HasBrokenEnd(em, e)) continue;
                    DeleteBroken(em, e, "gone or not a node");
                }
                _work.Clear();
            }
            if (_rebuild.Count > 0)
            {
                var rebuild = new List<Entity>(_rebuild);
                _rebuild.Clear();
                foreach (Entity e in rebuild) Rederive(em, e);
            }
        }

        // ================================================================== the reference audit's repairs

        /// <summary>The audit found a node list naming something that is not an edge: dropped, the node re-derived next frame. Null: nothing to do.</summary>
        public string RepairNodeEdges(EntityManager em, Entity node)
        {
            if (!em.Exists(node) || IsTemp(em, node) || !em.HasBuffer<ConnectedEdge>(node)) return null;
            int n = PruneNodeEdges(em, node, e => UsableEdge(em, e), "found by the reference audit");
            if (n == 0) return null;
            NoteRebuild(em, node);
            return n == 1 ? "element dropped" : n + " elements dropped";
        }

        /// <summary>The audit found a middle connection naming something that is not a node: dropped, the edge re-derived next frame.</summary>
        public string RepairMiddleNodes(EntityManager em, Entity edge)
        {
            if (!em.Exists(edge) || IsTemp(em, edge)) return null;
            int n = PruneMiddleNodes(em, edge, x => IsUsableNode(em, x), "found by the reference audit");
            if (n == 0) return null;
            NoteRebuild(em, edge);
            return n == 1 ? "element dropped" : n + " elements dropped";
        }

        /// <summary>The audit found an edge whose start or end node is gone: deleted at the start of the next frame.</summary>
        public string RepairEdgeNodes(EntityManager em, Entity edge)
        {
            if (!em.Exists(edge) || IsTemp(em, edge) || em.HasComponent<Deleted>(edge) || !em.HasComponent<Edge>(edge) || !HasBrokenEnd(em, edge)) return null;
            _broken.Add(edge);
            return "edge deleted next frame";
        }
    }
}
