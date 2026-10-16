using System.Collections.Generic;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>
    /// Referential integrity of structure proposals and host batches (ARCHITECTURE.md#host-validation-and-commit,
    /// #client-apply): judged on the world after the records, a proposal that would leave an edge without a node, a node
    /// deleted under an edge, or an entity without its owner is refused as a whole.
    /// </summary>
    public sealed class StructureIntegrityTests
    {
        /// <summary>A peer's world by net id.</summary>
        private sealed class World : IStructureWorld
        {
            private readonly HashSet<uint> _alive = new HashSet<uint>();
            private readonly HashSet<uint> _nodes = new HashSet<uint>();
            private readonly Dictionary<uint, (uint Start, uint End)> _edges = new Dictionary<uint, (uint, uint)>();
            private readonly Dictionary<uint, uint> _owners = new Dictionary<uint, uint>();
            private readonly Dictionary<uint, int> _unboundEdgesAt = new Dictionary<uint, int>();

            public World Node(params uint[] ids)
            {
                foreach (uint id in ids)
                {
                    _alive.Add(id);
                    _nodes.Add(id);
                }
                return this;
            }

            public World Edge(uint id, uint start, uint end)
            {
                _alive.Add(id);
                _edges[id] = (start, end);
                return this;
            }

            public World Object(uint id, uint owner = 0)
            {
                _alive.Add(id);
                if (owner != 0) _owners[id] = owner;
                return this;
            }

            /// <summary>An edge without a net id ends at the node (the host binds every edge; a client may not).</summary>
            public World UnboundEdgeAt(uint node)
            {
                _unboundEdgesAt.TryGetValue(node, out int n);
                _unboundEdgesAt[node] = n + 1;
                return this;
            }

            public bool IsAlive(uint netId) => _alive.Contains(netId);

            public bool IsNode(uint netId) => _nodes.Contains(netId);

            public bool TryGetEdge(uint netId, out uint start, out uint end)
            {
                start = end = 0;
                if (!_edges.TryGetValue(netId, out (uint Start, uint End) e)) return false;
                start = e.Start;
                end = e.End;
                return true;
            }

            public bool TryGetOwner(uint netId, out uint owner) => _owners.TryGetValue(netId, out owner);

            public void GetEdgesAt(uint nodeId, List<uint> into)
            {
                foreach (KeyValuePair<uint, (uint Start, uint End)> e in _edges)
                {
                    if (e.Value.Start == nodeId || e.Value.End == nodeId) into.Add(e.Key);
                }
                if (_unboundEdgesAt.TryGetValue(nodeId, out int n))
                {
                    for (int i = 0; i < n; i++) into.Add(0);
                }
            }
        }

        private static StructureRef Id(uint id) => StructureRef.To(id);

        private static StructureChange SpawnEdge(uint id, uint start, uint end) => StructureChange.Edge(SyncRecordKind.Spawn, id, Id(start), Id(end));

        private static StructureChange UpsertEdge(uint id, uint start, uint end) => StructureChange.Edge(SyncRecordKind.Upsert, id, Id(start), Id(end));

        private static StructureChange SpawnNode(uint id) => StructureChange.Node(SyncRecordKind.Spawn, id);

        private static StructureChange UpsertNode(uint id) => StructureChange.Node(SyncRecordKind.Upsert, id);

        private static StructureChange Despawn(uint id) => StructureChange.Despawn(id);

        private static RejectReason Check(World world, List<StructureProblem> problems, params StructureChange[] changes)
            => new StructureIntegrity().Check(changes, world, problems);

        private static RejectReason Check(World world, params StructureChange[] changes) => Check(world, null, changes);

        /// <summary>An elevated road 1-2-3 with a third elevated edge 2-4 (pillars are derived and never travel).</summary>
        private static World ElevatedJunction() => new World().Node(1, 2, 3, 4).Edge(100, 1, 2).Edge(101, 2, 3).Edge(102, 2, 4);

        // ------------------------------------------------------------------------------------------------ building

        [Test]
        public void NewRoadWithNewNodesIsAccepted()
        {
            var world = new World();
            Assert.Equal(RejectReason.None, Check(world, SpawnNode(10), SpawnNode(11), SpawnEdge(20, 10, 11)));
        }

        [Test]
        public void NewEdgeOrderInsideTheRecordsDoesNotMatter()
        {
            var world = new World().Node(1);
            Assert.Equal(RejectReason.None, Check(world, SpawnEdge(20, 1, 10), SpawnNode(10)), "the codec creates every spawn before it writes a value");
        }

        [Test]
        public void NewEdgeWhoseNodesTheSenderCouldNotNameIsRefused()
        {
            // the host crash of a bulldozed elevated road: the edge's Edge value travelled as presence only, so the host
            // created an edge with null nodes and the game's reference system indexed ConnectedEdge[Entity.Null]
            var world = new World().Node(1, 3);
            var problems = new List<StructureProblem>();
            RejectReason reason = Check(world, problems, StructureChange.Edge(SyncRecordKind.Spawn, 200, StructureRef.Unnamed, StructureRef.Unnamed));
            Assert.Equal(RejectReason.Invalid, reason);
            Assert.Equal(StructureIssue.NewEdgeWithoutNodes, problems[0].Issue);
            Assert.Equal(200u, problems[0].Subject);
            Assert.Contains("names no nodes", problems[0].Describe());
        }

        [Test]
        public void NewEdgeWithoutAnyEdgeValueIsRefused()
        {
            var world = new World().Node(1, 3);
            var edge = new StructureChange { Kind = SyncRecordKind.Spawn, NetId = 200, IsEdge = true };
            Assert.Equal(RejectReason.Invalid, Check(world, edge));
        }

        [Test]
        public void EdgeWithANullNodeIsRefused()
        {
            var world = new World().Node(1);
            var problems = new List<StructureProblem>();
            Assert.Equal(RejectReason.Invalid, Check(world, problems, StructureChange.Edge(SyncRecordKind.Spawn, 200, Id(1), StructureRef.Null)));
            Assert.Equal(StructureIssue.EdgeNodeUnnamed, problems[0].Issue);
        }

        [Test]
        public void EdgeEndingAtANodeTheHostLacksIsAConflict()
        {
            var world = new World().Node(1);
            var problems = new List<StructureProblem>();
            Assert.Equal(RejectReason.Conflict, Check(world, problems, SpawnEdge(200, 1, 77)));
            Assert.Equal(StructureIssue.EdgeNodeMissing, problems[0].Issue);
            Assert.Equal(77u, problems[0].Other);
        }

        [Test]
        public void EdgeEndingAtSomethingElseThanANodeIsRefused()
        {
            var world = new World().Node(1).Object(50);
            var problems = new List<StructureProblem>();
            Assert.Equal(RejectReason.Invalid, Check(world, problems, SpawnEdge(200, 1, 50)));
            Assert.Equal(StructureIssue.EdgeNodeNotNode, problems[0].Issue);
            var spawnedObject = StructureChange.Owned(SyncRecordKind.Spawn, 60, StructureRef.Absent);
            Assert.Equal(RejectReason.Invalid, Check(world, spawnedObject, SpawnEdge(201, 1, 60)), "a new non-node is no node either");
        }

        // ------------------------------------------------------------------------------------------------ bulldozing

        [Test]
        public void BulldozingAnElevatedEndEdgeWithItsOrphanNodeIsAccepted()
        {
            // 101 (2-3) goes, node 3 has no edge left and goes too; node 2 keeps 100 and 102
            World world = ElevatedJunction();
            Assert.Equal(RejectReason.None, Check(world, Despawn(101), Despawn(3), UpsertNode(2)));
        }

        [Test]
        public void NodeMergeOnLastEdgeRemovalIsAccepted()
        {
            // bulldozing 102 leaves node 2 with two matching elevated edges: the game deletes 2, 100 and 101 and creates
            // the combined edge 200 from 1 to 3 (node reduction); node 4 is orphaned
            World world = ElevatedJunction();
            Assert.Equal(RejectReason.None, Check(world, Despawn(102), Despawn(4), Despawn(2), Despawn(100), Despawn(101), SpawnEdge(200, 1, 3), UpsertNode(1), UpsertNode(3)));
        }

        [Test]
        public void NodeMergeWhoseDespawnsComeAfterTheNewEdgeIsAccepted()
        {
            World world = ElevatedJunction();
            Assert.Equal(RejectReason.None, Check(world, SpawnEdge(200, 1, 3), Despawn(2), Despawn(100), Despawn(102), Despawn(101), Despawn(4)));
        }

        [Test]
        public void NodeMergeThatKeepsAMergedEdgeAtTheDeletedNodeIsRefused()
        {
            // the proposal deletes node 2 but sends no despawn for 101: the host would keep 101 ending at a destroyed node
            World world = ElevatedJunction();
            var problems = new List<StructureProblem>();
            RejectReason reason = Check(world, problems, Despawn(102), Despawn(4), Despawn(2), Despawn(100), SpawnEdge(200, 1, 3));
            Assert.Equal(RejectReason.Conflict, reason);
            Assert.Equal(StructureIssue.EdgeLeftAtRemovedNode, problems[0].Issue);
            Assert.Equal(101u, problems[0].Subject);
            Assert.Equal(2u, problems[0].Other);
        }

        [Test]
        public void NodeDeletedWhileTheHostHasAnotherEdgeThereIsAConflict()
        {
            // the host has an edge at node 2 the client had not received when it bulldozed
            World world = ElevatedJunction().Node(5).Edge(103, 2, 5);
            var problems = new List<StructureProblem>();
            Assert.Equal(RejectReason.Conflict, Check(world, problems, Despawn(102), Despawn(4), Despawn(2), Despawn(100), Despawn(101), SpawnEdge(200, 1, 3)));
            Assert.Equal(1, problems.Count);
            Assert.Equal(103u, problems[0].Subject);
        }

        [Test]
        public void NodeDeletedUnderAnEdgeWithoutANetIdIsAConflict()
        {
            World world = new World().Node(1, 2).UnboundEdgeAt(2);
            var problems = new List<StructureProblem>();
            Assert.Equal(RejectReason.Conflict, Check(world, problems, Despawn(2)));
            Assert.Equal(0u, problems[0].Subject);
            Assert.Contains("without a net id", problems[0].Describe());
        }

        [Test]
        public void EdgeKeptAtANodeTheSameProposalDeletesIsInvalid()
        {
            World world = ElevatedJunction();
            var problems = new List<StructureProblem>();
            Assert.Equal(RejectReason.Invalid, Check(world, problems, Despawn(2), Despawn(100), Despawn(102), UpsertEdge(101, 2, 3)));
            Assert.Equal(StructureIssue.EdgeNodeRemoved, problems[0].Issue);
            Assert.Equal(101u, problems[0].Subject);
        }

        [Test]
        public void EditWithoutTheEdgeValueKeepsTheHostsNodes()
        {
            // an edit that does not carry Edge (presence only, or a values record) keeps the host's nodes, so deleting
            // one of them under it is a contradiction
            World world = ElevatedJunction();
            var keep = StructureChange.Edge(SyncRecordKind.Upsert, 101, StructureRef.Unnamed, StructureRef.Unnamed);
            Assert.Equal(RejectReason.Invalid, Check(world, Despawn(2), Despawn(100), Despawn(102), keep));
            Assert.Equal(RejectReason.None, Check(world, keep), "nothing deleted: the host's nodes stay valid");
        }

        [Test]
        public void EdgeRePointedAwayFromTheDeletedNodeIsAccepted()
        {
            World world = ElevatedJunction();
            Assert.Equal(RejectReason.None, Check(world, SpawnNode(10), Despawn(2), Despawn(100), Despawn(102), UpsertEdge(101, 10, 3)));
        }

        [Test]
        public void NewEdgeAtANodeTheSameProposalDeletesIsInvalid()
        {
            World world = ElevatedJunction();
            var problems = new List<StructureProblem>();
            Assert.Equal(RejectReason.Invalid, Check(world, problems, Despawn(102), Despawn(4), SpawnEdge(200, 1, 4)));
            Assert.Equal(StructureIssue.EdgeNodeRemoved, problems[0].Issue);
            Assert.Equal(4u, problems[0].Other);
        }

        [Test]
        public void WriteFollowedByADespawnOfTheSameEdgeIsNoKeptEdge()
        {
            World world = ElevatedJunction();
            Assert.Equal(RejectReason.None, Check(world, UpsertEdge(101, 2, 3), Despawn(101), Despawn(3)));
        }

        [Test]
        public void IdReusedAfterItsDespawnIsANewEntity()
        {
            World world = new World().Node(1, 2).Edge(100, 1, 2);
            // 100 is deleted and the id created again without nodes: refused as a new edge
            Assert.Equal(RejectReason.Invalid, Check(world, Despawn(100), StructureChange.Edge(SyncRecordKind.Spawn, 100, StructureRef.Unnamed, StructureRef.Unnamed)));
            Assert.Equal(RejectReason.None, Check(world, Despawn(100), SpawnEdge(100, 2, 1)));
        }

        [Test]
        public void DespawnOfAnUnknownNodeChecksNothing()
        {
            Assert.Equal(RejectReason.None, Check(new World(), Despawn(9)));
        }

        // ------------------------------------------------------------------------------------------------ owners

        [Test]
        public void OwnedEntitiesNeedTheirOwner()
        {
            World world = ElevatedJunction().Object(400);
            var problems = new List<StructureProblem>();
            Assert.Equal(RejectReason.None, Check(world, StructureChange.Owned(SyncRecordKind.Spawn, 300, Id(400))), "existing owner");
            Assert.Equal(RejectReason.None, Check(world, StructureChange.Owned(SyncRecordKind.Spawn, 401, StructureRef.Absent), StructureChange.Owned(SyncRecordKind.Spawn, 300, Id(401))), "owner created by the same proposal");
            Assert.Equal(RejectReason.None, Check(world, StructureChange.Owned(SyncRecordKind.Spawn, 300, StructureRef.Derived)), "a derived owner is resolved by each peer");
            Assert.Equal(RejectReason.None, Check(world, StructureChange.Owned(SyncRecordKind.Spawn, 300, StructureRef.Null)), "no owner");

            Assert.Equal(RejectReason.Invalid, Check(world, problems, StructureChange.Owned(SyncRecordKind.Spawn, 300, StructureRef.Unnamed)));
            Assert.Equal(StructureIssue.OwnerUnnamed, problems[0].Issue);
            problems.Clear();
            Assert.Equal(RejectReason.Invalid, Check(world, problems, Despawn(101), Despawn(3), StructureChange.Owned(SyncRecordKind.Spawn, 300, Id(101))));
            Assert.Equal(StructureIssue.OwnerRemoved, problems[0].Issue);
            problems.Clear();
            Assert.Equal(RejectReason.Conflict, Check(world, problems, StructureChange.Owned(SyncRecordKind.Spawn, 300, Id(999))));
            Assert.Equal(StructureIssue.OwnerMissing, problems[0].Issue);
        }

        [Test]
        public void EditWhoseOwnerTravelsAsPresenceKeepsTheHostsOwner()
        {
            World world = ElevatedJunction().Object(300, owner: 101);
            var edit = StructureChange.Owned(SyncRecordKind.Upsert, 300, StructureRef.Unnamed);
            Assert.Equal(RejectReason.None, Check(world, edit));
            Assert.Equal(RejectReason.Invalid, Check(world, Despawn(101), Despawn(3), edit), "kept while its owner (the bulldozed edge) goes");
            Assert.Equal(RejectReason.None, Check(world, Despawn(101), Despawn(3), Despawn(300)), "pillars and other owned objects go with their owner");
        }

        // ------------------------------------------------------------------------------------------------ views and reports

        [Test]
        public void SendersOwnWorldTreatsEverySpawnAsNew()
        {
            // a client checking its own proposal: the new edge exists in its world, but the host does not have it
            World world = new World().Node(1, 3).Edge(200, 1, 3);
            var edge = StructureChange.Edge(SyncRecordKind.Spawn, 200, StructureRef.Unnamed, StructureRef.Unnamed);
            Assert.Equal(RejectReason.None, new StructureIntegrity().Check(new[] { edge }, world, null), "the host's view: an edit of an existing edge");
            Assert.Equal(RejectReason.Invalid, new StructureIntegrity().Check(new[] { edge }, world, null, worldIncludesChanges: true));
        }

        [Test]
        public void InvalidWinsOverConflictAndProblemsAreCapped()
        {
            World world = ElevatedJunction().Node(5).Edge(103, 2, 5);
            var problems = new List<StructureProblem>();
            var integrity = new StructureIntegrity();
            RejectReason reason = integrity.Check(new[] { Despawn(2), SpawnEdge(200, 1, 2), SpawnEdge(201, 1, 77) }, world, problems, maxProblems: 2);
            Assert.Equal(RejectReason.Invalid, reason);
            Assert.Equal(2, problems.Count);
            Assert.True(integrity.ProblemCount > 2, "every problem is counted: " + integrity.ProblemCount);
            string line = StructureIntegrity.Summarize(problems, integrity.ProblemCount);
            Assert.Contains("more)", line);
        }

        [Test]
        public void RawReferencesMapToKinds()
        {
            Assert.Equal(StructureRefKind.Id, StructureRef.FromRaw(12).Kind);
            Assert.Equal(12u, StructureRef.FromRaw(12).Id);
            Assert.Equal(StructureRefKind.Null, StructureRef.FromRaw(0).Kind);
            Assert.Equal(StructureRefKind.Null, StructureRef.FromRaw(-1).Kind);
            Assert.Equal(StructureRefKind.Derived, StructureRef.FromRaw(-2).Kind);
            Assert.False(StructureRef.Absent.HasValue);
            Assert.False(StructureRef.Unnamed.HasValue);
            Assert.True(StructureRef.Null.HasValue);
        }

        [Test]
        public void EmptyInputIsAccepted()
        {
            Assert.Equal(RejectReason.None, new StructureIntegrity().Check(new StructureChange[0], new World(), null));
            Assert.Equal(RejectReason.None, new StructureIntegrity().Check(null, new World(), null));
            Assert.Equal("no problems", StructureIntegrity.Summarize(null, 0));
        }
    }
}
