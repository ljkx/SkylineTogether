using System.Collections.Generic;
using System.Text;

namespace SkylineTogether.Core.Sync
{
    /// <summary>How a record names another entity in one of its structural references.</summary>
    public enum StructureRefKind : byte
    {
        /// <summary>The record does not carry the component.</summary>
        Absent,
        /// <summary>The component travelled as presence only: the sender could not name the entity it references.</summary>
        Unnamed,
        /// <summary>An explicit null reference.</summary>
        Null,
        /// <summary>A net id.</summary>
        Id,
        /// <summary>A derived key (a lane or prefab sub-object of a bound owner), resolved by every peer itself.</summary>
        Derived,
    }

    /// <summary>A structural reference of a record (an edge's node, an owner) as the sender wrote it.</summary>
    public readonly struct StructureRef
    {
        public readonly StructureRefKind Kind;
        public readonly uint Id;

        private StructureRef(StructureRefKind kind, uint id)
        {
            Kind = kind;
            Id = id;
        }

        public static StructureRef Absent => default;

        public static StructureRef Unnamed => new StructureRef(StructureRefKind.Unnamed, 0);

        public static StructureRef Null => new StructureRef(StructureRefKind.Null, 0);

        public static StructureRef Derived => new StructureRef(StructureRefKind.Derived, 0);

        /// <summary>A reference to the net id <paramref name="id"/> (0 is the null reference).</summary>
        public static StructureRef To(uint id) => id == 0 ? Null : new StructureRef(StructureRefKind.Id, id);

        /// <summary>A reference as the wire carries it: a net id, 0 or -1 for null, -2 - k for derived key k.</summary>
        public static StructureRef FromRaw(int raw) => raw > 0 ? To((uint)raw) : raw >= -1 ? Null : Derived;

        /// <summary>True when the record carries the referencing component's value.</summary>
        public bool HasValue => Kind >= StructureRefKind.Null;

        public override string ToString() => Kind == StructureRefKind.Id ? "#" + Id : Kind.ToString();
    }

    /// <summary>
    /// One entity record of a proposal or a host batch, reduced to what referential integrity needs
    /// (ARCHITECTURE.md#host-validation-and-commit): its kind, id, whether it is a net node or edge, the edge's two nodes
    /// and the entity's owner. A reference is <see cref="StructureRef.Absent"/> when the record does not carry the
    /// component and <see cref="StructureRef.Unnamed"/> when it carries it as presence only.
    /// </summary>
    public sealed class StructureChange
    {
        public SyncRecordKind Kind;
        public uint NetId;
        public bool IsNode;
        public bool IsEdge;
        public StructureRef Start;
        public StructureRef End;
        public StructureRef Owner;

        public static StructureChange Despawn(uint netId) => new StructureChange { Kind = SyncRecordKind.Despawn, NetId = netId };

        public static StructureChange Node(SyncRecordKind kind, uint netId) => new StructureChange { Kind = kind, NetId = netId, IsNode = true };

        public static StructureChange Edge(SyncRecordKind kind, uint netId, StructureRef start, StructureRef end)
            => new StructureChange { Kind = kind, NetId = netId, IsEdge = true, Start = start, End = end };

        /// <summary>A record of anything else (an object, an area, ...) with its owner.</summary>
        public static StructureChange Owned(SyncRecordKind kind, uint netId, StructureRef owner) => new StructureChange { Kind = kind, NetId = netId, Owner = owner };

        /// <summary>True for records that write the entity (spawn, upsert, values).</summary>
        public bool Writes => Kind == SyncRecordKind.Spawn || Kind == SyncRecordKind.Upsert || Kind == SyncRecordKind.Values;
    }

    /// <summary>
    /// The world of the peer that applies the records, by net id (the host's for a proposal, the client's own for a
    /// host batch or for its own proposal). Entities without a net id are reported as 0.
    /// </summary>
    public interface IStructureWorld
    {
        /// <summary>True when the id is bound to an entity that exists and is not being deleted.</summary>
        bool IsAlive(uint netId);

        /// <summary>True when the id is bound to a net node.</summary>
        bool IsNode(uint netId);

        /// <summary>The edge's current nodes (0 for a node without a net id); false when the id is not an edge.</summary>
        bool TryGetEdge(uint netId, out uint start, out uint end);

        /// <summary>The entity's current owner (0: none, or one without a net id); false when it has no owner.</summary>
        bool TryGetOwner(uint netId, out uint owner);

        /// <summary>
        /// Adds the edges that start or end at the node (not edges connected in their middle, tool previews or edges
        /// already being deleted); 0 for such an edge without a net id.
        /// </summary>
        void GetEdgesAt(uint nodeId, List<uint> into);
    }

    /// <summary>What would dangle.</summary>
    public enum StructureIssue : byte
    {
        None,
        /// <summary>A new edge whose nodes the record does not name (its Edge value is missing or presence only).</summary>
        NewEdgeWithoutNodes,
        /// <summary>An edge whose record names a null or derived node.</summary>
        EdgeNodeUnnamed,
        /// <summary>An edge that is kept and ends at a node the same records delete.</summary>
        EdgeNodeRemoved,
        /// <summary>An edge that ends at a node this peer does not have.</summary>
        EdgeNodeMissing,
        /// <summary>An edge that ends at something that is not a node.</summary>
        EdgeNodeNotNode,
        /// <summary>A node is deleted while an edge the records neither delete nor re-point still ends there.</summary>
        EdgeLeftAtRemovedNode,
        /// <summary>A new entity whose owner the sender could not name.</summary>
        OwnerUnnamed,
        /// <summary>An entity that is kept while the same records delete its owner.</summary>
        OwnerRemoved,
        /// <summary>An entity whose owner this peer does not have.</summary>
        OwnerMissing,
    }

    /// <summary>One reference that would dangle after the records are applied.</summary>
    public readonly struct StructureProblem
    {
        public readonly StructureIssue Issue;
        /// <summary>The entity that would dangle (an edge, an owned entity; for <see cref="StructureIssue.EdgeLeftAtRemovedNode"/> the edge, 0 when it has no net id).</summary>
        public readonly uint Subject;
        /// <summary>The entity it would reference (a node, an owner; 0 when unnamed).</summary>
        public readonly uint Other;
        public readonly RejectReason Reason;

        public StructureProblem(StructureIssue issue, uint subject, uint other, RejectReason reason)
        {
            Issue = issue;
            Subject = subject;
            Other = other;
            Reason = reason;
        }

        /// <summary>One line for the log.</summary>
        public string Describe()
        {
            string subject = "#" + Subject, other = "#" + Other;
            switch (Issue)
            {
                case StructureIssue.NewEdgeWithoutNodes:
                    return "new edge " + subject + " names no nodes (its Edge value was not sent: the sender could not name a node)";
                case StructureIssue.EdgeNodeUnnamed:
                    return "edge " + subject + " names no node at one end";
                case StructureIssue.EdgeNodeRemoved:
                    return "edge " + subject + " is kept but ends at node " + other + ", which the same change deletes";
                case StructureIssue.EdgeNodeMissing:
                    return "edge " + subject + " ends at node " + other + ", which does not exist here";
                case StructureIssue.EdgeNodeNotNode:
                    return "edge " + subject + " ends at " + other + ", which is not a node";
                case StructureIssue.EdgeLeftAtRemovedNode:
                    return "node " + other + " is deleted but edge " + (Subject == 0 ? "(without a net id)" : subject) + " still ends there and is not deleted";
                case StructureIssue.OwnerUnnamed:
                    return "new " + subject + " has an owner the sender could not name";
                case StructureIssue.OwnerRemoved:
                    return subject + " is kept but its owner " + other + " is deleted by the same change";
                case StructureIssue.OwnerMissing:
                    return subject + " belongs to " + other + ", which does not exist here";
                default:
                    return Issue.ToString();
            }
        }

        public override string ToString() => Describe();
    }

    /// <summary>
    /// Referential integrity of structure records (ARCHITECTURE.md#host-validation-and-commit, #client-apply), judged on
    /// "the world after these records": every kept edge ends at two nodes that exist and are not deleted (or that the
    /// same records create); every node the records delete leaves no edge behind that still ends there; every kept
    /// entity's owner survives. The host refuses a proposal that fails (a sanitized or untagged edge with a missing node
    /// still crashes the game's Burst jobs, which follow <c>Edge</c>, <c>ConnectedEdge</c> and <c>Owner</c> unchecked);
    /// a client checks its own proposal before sending it and every host batch before applying it.
    /// <para>
    /// Refusal reasons: <see cref="RejectReason.Invalid"/> when the records contradict themselves (an edge kept at a node
    /// they delete, a new edge or owned entity whose reference the sender could not name), otherwise
    /// <see cref="RejectReason.Conflict"/> (this peer's world differs: a node or owner it lacks, an edge it has and the
    /// sender did not know of).
    /// </para>
    /// Pure and allocation-light (reused collections); main thread only.
    /// </summary>
    public sealed class StructureIntegrity
    {
        private struct State
        {
            public bool Alive;
            public bool Removed;   // a despawn of the records is the id's last word
            public bool New;       // created by the records (no earlier entity is edited)
            public int Write;      // index of the last applied write record, -1: none
        }

        private readonly Dictionary<uint, State> _states = new Dictionary<uint, State>();
        private readonly List<uint> _edges = new List<uint>();
        private readonly List<StructureProblem> _found = new List<StructureProblem>();
        private IReadOnlyList<StructureChange> _changes;
        private IStructureWorld _world;
        private int _limit;
        private bool _anyInvalid, _anyConflict;

        /// <summary>Problems found by the last check, including those beyond the reported limit.</summary>
        public int ProblemCount { get; private set; }

        /// <summary>
        /// Checks <paramref name="changes"/> (in record order) against <paramref name="world"/>; the first
        /// <paramref name="maxProblems"/> problems are added to <paramref name="problems"/>. With
        /// <paramref name="worldIncludesChanges"/> the world already shows the change (a client checking its own proposal
        /// after its tool applied it): every spawn is then a new entity, although it exists in that world.
        /// Returns <see cref="RejectReason.None"/>, <see cref="RejectReason.Invalid"/> or <see cref="RejectReason.Conflict"/>.
        /// </summary>
        public RejectReason Check(IReadOnlyList<StructureChange> changes, IStructureWorld world, List<StructureProblem> problems, int maxProblems = 8, bool worldIncludesChanges = false)
        {
            ProblemCount = 0;
            _anyInvalid = _anyConflict = false;
            _found.Clear();
            if (changes == null || world == null || changes.Count == 0) return RejectReason.None;
            _changes = changes;
            _world = world;
            _limit = maxProblems < 0 ? 0 : maxProblems;
            try
            {
                Resolve(worldIncludesChanges);
                CheckWrites();
                CheckRemovedNodes();
                if (problems != null) problems.AddRange(_found);
                return _anyInvalid ? RejectReason.Invalid : _anyConflict ? RejectReason.Conflict : RejectReason.None;
            }
            finally
            {
                _states.Clear();
                _changes = null;
                _world = null;
            }
        }

        /// <summary>One line for the log: the problems, and how many more were found.</summary>
        public static string Summarize(IReadOnlyList<StructureProblem> problems, int total)
        {
            if (problems == null || problems.Count == 0) return "no problems";
            var sb = new StringBuilder();
            for (int i = 0; i < problems.Count; i++)
            {
                if (i > 0) sb.Append("; ");
                sb.Append(problems[i].Describe());
            }
            if (total > problems.Count) sb.Append(" (+").Append(total - problems.Count).Append(" more)");
            return sb.ToString();
        }

        // the final state of every id the records name, as EntityCodec.Apply leaves it: a despawn is final unless a
        // spawn of the id follows it; a write of an id that a despawn removes stays removed
        private void Resolve(bool worldIncludesChanges)
        {
            for (int i = 0; i < _changes.Count; i++)
            {
                StructureChange c = _changes[i];
                if (c == null || c.NetId == 0) continue;
                _states.TryGetValue(c.NetId, out State s);
                bool seen = _states.ContainsKey(c.NetId);
                if (!seen) s.Write = -1;
                switch (c.Kind)
                {
                    case SyncRecordKind.Despawn:
                        s.Alive = false;
                        s.Removed = true;
                        s.New = false;
                        s.Write = -1;
                        break;
                    case SyncRecordKind.Spawn:
                        // a spawn of an id that is alive here edits that entity; after a despawn it is a new one
                        s.New = worldIncludesChanges || s.Removed || !_world.IsAlive(c.NetId);
                        s.Alive = true;
                        s.Removed = false;
                        s.Write = i;
                        break;
                    case SyncRecordKind.Upsert:
                    case SyncRecordKind.Values:
                        if (s.Removed) break; // written, then removed by the despawn
                        if (!s.Alive) s.Alive = _world.IsAlive(c.NetId);
                        if (s.Alive) s.Write = i;
                        break;
                    default:
                        continue;
                }
                _states[c.NetId] = s;
            }
        }

        private bool TryState(uint id, out State s) => _states.TryGetValue(id, out s);

        /// <summary>Alive once the records are applied.</summary>
        private bool AliveAfter(uint id)
        {
            if (id == 0) return false;
            if (TryState(id, out State s)) return s.Alive;
            return _world.IsAlive(id);
        }

        private bool RemovedByChanges(uint id) => id != 0 && TryState(id, out State s) && s.Removed;

        private bool NodeAfter(uint id)
        {
            if (TryState(id, out State s) && s.Write >= 0 && _changes[s.Write].IsNode) return true;
            return !(s.New) && _world.IsNode(id);
        }

        private StructureChange WriteOf(uint id) => TryState(id, out State s) && s.Write >= 0 ? _changes[s.Write] : null;

        private void Add(StructureIssue issue, uint subject, uint other, RejectReason reason)
        {
            ProblemCount++;
            if (reason == RejectReason.Invalid) _anyInvalid = true;
            else _anyConflict = true;
            if (_found.Count < _limit) _found.Add(new StructureProblem(issue, subject, other, reason));
        }

        private void CheckWrites()
        {
            foreach (KeyValuePair<uint, State> pair in _states)
            {
                State s = pair.Value;
                if (!s.Alive || s.Write < 0) continue;
                StructureChange w = _changes[s.Write];
                uint id = pair.Key;
                CheckEdge(id, s, w);
                CheckOwner(id, s, w);
            }
        }

        // the nodes a kept edge ends at once the records are applied: its record's, else (an edit that does not carry
        // the Edge value) the ones it has here
        private void CheckEdge(uint id, in State s, StructureChange w)
        {
            uint hereStart = 0, hereEnd = 0;
            bool edgeHere = !s.New && _world.TryGetEdge(id, out hereStart, out hereEnd);
            if (!w.IsEdge && !edgeHere && !w.Start.HasValue && !w.End.HasValue) return;
            if (!w.Start.HasValue || !w.End.HasValue)
            {
                if (s.New || !edgeHere)
                {
                    // a new edge (or an entity that becomes one) without its nodes: the game indexes Edge.m_Start unchecked
                    if (w.IsEdge || w.Start.Kind == StructureRefKind.Unnamed || w.End.Kind == StructureRefKind.Unnamed) Add(StructureIssue.NewEdgeWithoutNodes, id, 0, RejectReason.Invalid);
                    return;
                }
                CheckEnd(id, w.Start.HasValue ? w.Start : StructureRef.To(hereStart), w.Start.HasValue);
                CheckEnd(id, w.End.HasValue ? w.End : StructureRef.To(hereEnd), w.End.HasValue);
                return;
            }
            CheckEnd(id, w.Start, true);
            CheckEnd(id, w.End, true);
        }

        private void CheckEnd(uint edge, StructureRef node, bool named)
        {
            if (node.Kind != StructureRefKind.Id)
            {
                // a node of this peer without a net id cannot be judged here (the host binds every node)
                if (!named) return;
                Add(StructureIssue.EdgeNodeUnnamed, edge, 0, RejectReason.Invalid);
                return;
            }
            uint n = node.Id;
            if (RemovedByChanges(n))
            {
                Add(StructureIssue.EdgeNodeRemoved, edge, n, RejectReason.Invalid);
                return;
            }
            if (!AliveAfter(n))
            {
                Add(StructureIssue.EdgeNodeMissing, edge, n, RejectReason.Conflict);
                return;
            }
            if (!NodeAfter(n)) Add(StructureIssue.EdgeNodeNotNode, edge, n, RejectReason.Invalid);
        }

        private void CheckOwner(uint id, in State s, StructureChange w)
        {
            StructureRef owner = w.Owner;
            if (owner.Kind == StructureRefKind.Absent) return;
            if (owner.Kind == StructureRefKind.Unnamed)
            {
                if (s.New)
                {
                    Add(StructureIssue.OwnerUnnamed, id, 0, RejectReason.Invalid);
                    return;
                }
                // an edit keeps the owner it has here
                if (!_world.TryGetOwner(id, out uint here) || here == 0) return;
                owner = StructureRef.To(here);
            }
            if (owner.Kind != StructureRefKind.Id) return; // none, or a derived owner each peer resolves itself
            if (RemovedByChanges(owner.Id)) Add(StructureIssue.OwnerRemoved, id, owner.Id, RejectReason.Invalid);
            else if (!AliveAfter(owner.Id)) Add(StructureIssue.OwnerMissing, id, owner.Id, RejectReason.Conflict);
        }

        // every node the records delete: the edges that end there must be deleted too (or re-pointed by their record)
        private void CheckRemovedNodes()
        {
            foreach (KeyValuePair<uint, State> pair in _states)
            {
                if (!pair.Value.Removed) continue;
                uint node = pair.Key;
                if (!_world.IsAlive(node) || !_world.IsNode(node)) continue; // nothing here to delete
                _edges.Clear();
                _world.GetEdgesAt(node, _edges);
                foreach (uint edge in _edges)
                {
                    if (edge == 0)
                    {
                        Add(StructureIssue.EdgeLeftAtRemovedNode, 0, node, RejectReason.Conflict);
                        continue;
                    }
                    if (!AliveAfter(edge)) continue; // deleted as well
                    if (WriteOf(edge) != null) continue; // a kept edge with a record: judged by CheckEdge
                    Add(StructureIssue.EdgeLeftAtRemovedNode, edge, node, RejectReason.Conflict);
                }
            }
            _edges.Clear();
        }
    }
}
