using System;
using System.Collections.Generic;
using System.Linq;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>
    /// The derived net links (ARCHITECTURE.md#derived-net-links): street lists and road edges travel, an edge's street and
    /// building list are derived. The rules, the membership pass after an apply and the deletion pass at the end of the
    /// Modification phases, and a model of the client crash they prevent (a street list still naming a destroyed edge when
    /// a road is connected next to it).
    /// </summary>
    public sealed class DerivedNetLinksTests
    {
        // ------------------------------------------------------------------ rules

        [Test]
        public void EveryLinkHasAFieldName()
        {
            var names = new HashSet<string>();
            for (int i = 0; i < DerivedNetLinkRules.Count; i++)
            {
                string name = DerivedNetLinkRules.Name((DerivedNetLink)i);
                Assert.NotEqual("?", name);
                Assert.True(names.Add(name), "unique: " + name);
            }
            Assert.Equal(DerivedNetLinkRules.Count, Enum.GetValues(typeof(DerivedNetLink)).Length);
            Assert.Equal("Net.Aggregated.m_Aggregate", DerivedNetLinkRules.Name(DerivedNetLink.AggregatePointer));
            Assert.Equal("Buildings.ConnectedBuilding.m_Building", DerivedNetLinkRules.Name(DerivedNetLink.ConnectedBuilding));
        }

        [Test]
        public void APointerIsClearedOnlyWhenItNamesSomethingUnusableOrDying()
        {
            Assert.False(DerivedNetLinkRules.MustClear(isNull: true, usable: false, dying: false), "null is a normal value (no street, no road)");
            Assert.False(DerivedNetLinkRules.MustClear(isNull: true, usable: false, dying: true));
            Assert.False(DerivedNetLinkRules.MustClear(isNull: false, usable: true, dying: false), "a live target stays");
            Assert.True(DerivedNetLinkRules.MustClear(isNull: false, usable: false, dying: false), "destroyed or the wrong kind");
            Assert.True(DerivedNetLinkRules.MustClear(isNull: false, usable: true, dying: true), "deleted this frame: gone after it");
        }

        [Test]
        public void PruningKeepsTheOrderOfTheRest()
        {
            var list = new List<int> { 1, 2, 3, 4, 5, 6 };
            Assert.Equal(3, DerivedNetLinkRules.Prune(list, x => x % 2 == 1));
            Assert.SequenceEqual(new[] { 1, 3, 5 }, list, "a street's order is its course along the road");
            Assert.Equal(0, DerivedNetLinkRules.Prune(list, x => true));
            Assert.Equal(3, DerivedNetLinkRules.Prune(list, x => false));
            Assert.Equal(0, list.Count);
            Assert.Throws<ArgumentNullException>(() => DerivedNetLinkRules.Prune<int>(null, x => true));
            Assert.Throws<ArgumentNullException>(() => DerivedNetLinkRules.Prune(new List<int>(), null));
        }

        // ------------------------------------------------------------------ membership after an apply

        [Test]
        public void EveryListedEdgeNamesTheStreetThatListsIt()
        {
            // street 100 (written by a host record) lists edges 1, 2, 3; edge 1 already names it, edge 2 names the
            // client's own street 200, edge 3 names nothing (spawned this batch, presence only)
            var pointer = new Dictionary<int, int> { [1] = 100, [2] = 200, [3] = 0 };
            var ops = new List<MembershipOp<int>>();
            int n = AggregateMembership.Plan(100, new[] { 1, 2, 3 }, e => pointer[e], e => pointer.ContainsKey(e), ops);
            Assert.Equal(2, n);
            Assert.Equal(2, ops.Count);
            Assert.Equal(2, ops[0].Edge);
            Assert.Equal(200, ops[0].From, "edge 2 leaves the client's own street");
            Assert.Equal(100, ops[0].To);
            Assert.Equal(3, ops[1].Edge);
            Assert.Equal(0, ops[1].From);
        }

        [Test]
        public void MembershipSkipsUnusableAndDuplicateElementsAndAppends()
        {
            var pointer = new Dictionary<int, int> { [1] = 0, [2] = 0 };
            var ops = new List<MembershipOp<int>> { new MembershipOp<int>(9, 9, 9) };
            // 7 is not an edge the pass may write (deleted, a preview, gone); 1 is listed twice
            int n = AggregateMembership.Plan(100, new[] { 1, 7, 1, 2 }, e => pointer[e], e => pointer.ContainsKey(e), ops);
            Assert.Equal(2, n);
            Assert.Equal(3, ops.Count, "appended to what the caller had");
            Assert.SequenceEqual(new[] { 9, 1, 2 }, ops.Select(o => o.Edge));
            Assert.Equal(0, AggregateMembership.Plan(100, new int[0], e => 0, e => true, ops));
            Assert.Equal(0, AggregateMembership.Plan(100, null, e => 0, e => true, ops));
            Assert.Equal(0, AggregateMembership.Plan(100, new[] { 1 }, e => 0, e => true, null));
        }

        // ------------------------------------------------------------------ deletions of a frame

        [Test]
        public void TheDeletionsOfAFrameAnswerPerKind()
        {
            var d = new DerivedLinkDeletions<int>();
            Assert.False(d.HasWork);
            d.Edges.Add(1);
            d.Aggregates.Add(100);
            d.Buildings.Add(50);
            Assert.True(d.HasWork);
            Assert.True(d.IsDeletedEdge(1));
            Assert.False(d.IsDeletedEdge(100), "kinds are kept apart");
            Assert.True(d.IsGoneAggregate(100));
            Assert.False(d.IsGoneAggregate(101));
            d.Emptied.Add(101);
            Assert.True(d.IsGoneAggregate(101), "a street whose list became empty counts as gone for the pointers");
            Assert.True(d.IsDeletedBuilding(50));
            d.Clear();
            Assert.False(d.HasWork);
            Assert.False(d.IsGoneAggregate(101));
            d.Emptied.Add(5);
            Assert.False(d.HasWork, "emptied streets alone are found by the edge pass, not a reason for one");
        }

        // ------------------------------------------------------------------ the crash, modelled

        /// <summary>
        /// A toy world with the game's two link pairs and the guard's passes built from the Core rules (the game layer's
        /// <c>DerivedLinkGuard</c> does the same on the entity manager).
        /// </summary>
        private sealed class World
        {
            public readonly HashSet<int> Alive = new HashSet<int>();
            public readonly Dictionary<int, List<int>> Streets = new Dictionary<int, List<int>>(); // AggregateElement
            public readonly Dictionary<int, int> StreetOf = new Dictionary<int, int>(); // Aggregated
            public readonly Dictionary<int, int> RoadOf = new Dictionary<int, int>(); // Building.m_RoadEdge
            public readonly Dictionary<int, List<int>> Buildings = new Dictionary<int, List<int>>(); // ConnectedBuilding

            public void Edge(int e, int street)
            {
                Alive.Add(e);
                StreetOf[e] = street;
                Buildings[e] = new List<int>();
                if (street == 0) return;
                if (!Streets.ContainsKey(street))
                {
                    Alive.Add(street);
                    Streets[street] = new List<int>();
                }
                Streets[street].Add(e);
            }

            public void Building(int b, int road)
            {
                Alive.Add(b);
                RoadOf[b] = road;
                Buildings[road].Add(b);
            }

            /// <summary>What the game does when it deletes an edge: the street it names drops it (ValidateAggregate).</summary>
            public void GameDeletesEdge(int e, DerivedLinkDeletions<int> frame)
            {
                int street = StreetOf[e];
                if (street != 0) Streets[street].Remove(e);
                frame.Edges.Add(e);
            }

            /// <summary>The applier writes a street's list from a host record, then the membership pass (or not).</summary>
            public void HostWritesStreet(int street, int[] edges, bool membership)
            {
                Alive.Add(street);
                Streets[street] = edges.ToList();
                if (!membership) return;
                var ops = new List<MembershipOp<int>>();
                AggregateMembership.Plan(street, edges, e => StreetOf[e], e => Alive.Contains(e), ops);
                foreach (MembershipOp<int> op in ops)
                {
                    StreetOf[op.Edge] = op.To;
                    if (op.From != 0 && op.From != op.To && Streets.TryGetValue(op.From, out List<int> old)) old.Remove(op.Edge);
                }
            }

            /// <summary>The applier writes a building's road edge from a host record (the list side stays as derived).</summary>
            public void HostMovesBuilding(int b, int road) => RoadOf[b] = road;

            /// <summary>DerivedLinkCleanupSystem: nothing keeps naming what this frame deleted.</summary>
            public void EndOfModification(DerivedLinkDeletions<int> frame)
            {
                foreach (List<int> list in Streets.Values) DerivedNetLinkRules.Prune(list, e => !frame.IsDeletedEdge(e));
                foreach (int b in RoadOf.Keys.ToList())
                {
                    if (frame.IsDeletedEdge(RoadOf[b])) RoadOf[b] = 0;
                }
                foreach (List<int> list in Buildings.Values) DerivedNetLinkRules.Prune(list, b => !frame.IsDeletedBuilding(b));
            }

            /// <summary>The end of the frame: deleted entities are destroyed.</summary>
            public void Cleanup(DerivedLinkDeletions<int> frame)
            {
                foreach (int e in frame.Edges)
                {
                    Alive.Remove(e);
                    Buildings.Remove(e);
                }
                foreach (int b in frame.Buildings) Alive.Remove(b);
                frame.Clear();
            }

            /// <summary>What AggregateSystem / AggregateMeshSystem / RoadConnectionSystem index unchecked: every reference must be alive.</summary>
            public List<string> Dangling()
            {
                var found = new List<string>();
                foreach (KeyValuePair<int, List<int>> s in Streets)
                {
                    foreach (int e in s.Value)
                    {
                        if (!Alive.Contains(e)) found.Add("street " + s.Key + " lists destroyed edge " + e);
                    }
                }
                foreach (KeyValuePair<int, int> b in RoadOf)
                {
                    if (Alive.Contains(b.Key) && b.Value != 0 && !Alive.Contains(b.Value)) found.Add("building " + b.Key + " names destroyed road " + b.Value);
                }
                foreach (KeyValuePair<int, List<int>> e in Buildings)
                {
                    foreach (int b in e.Value)
                    {
                        if (!Alive.Contains(b)) found.Add("edge " + e.Key + " lists destroyed building " + b);
                    }
                }
                return found;
            }
        }

        [Test]
        public void WithoutTheGuardAHostListedStreetKeepsAnEdgeThePlayerReplaced()
        {
            // the client derived street 200 for edges 1-2; the host then sends its street 100 listing 1-3 (presence-only
            // pointers: edges 1 and 2 still name 200). The player connects a road at edge 2: the game splits it and drops
            // it from street 200, the street it names
            var w = new World();
            w.Edge(1, 200);
            w.Edge(2, 200);
            w.Edge(3, 0);
            w.HostWritesStreet(100, new[] { 1, 2, 3 }, membership: false);
            var frame = new DerivedLinkDeletions<int>();
            w.GameDeletesEdge(2, frame);
            w.Cleanup(frame);
            Assert.SequenceEqual(new[] { "street 100 lists destroyed edge 2" }, w.Dangling(),
                "the next road built next to street 100 makes AggregateSystem index the destroyed edge: the native crash");
        }

        [Test]
        public void TheMembershipPassKeepsOneStreetPerEdge()
        {
            var w = new World();
            w.Edge(1, 200);
            w.Edge(2, 200);
            w.Edge(3, 0);
            w.HostWritesStreet(100, new[] { 1, 2, 3 }, membership: true);
            Assert.Equal(100, w.StreetOf[1]);
            Assert.Equal(100, w.StreetOf[2]);
            Assert.Equal(100, w.StreetOf[3]);
            Assert.Equal(0, w.Streets[200].Count, "the client's own street gives its edges up (the guard deletes it when it has no id)");
            var frame = new DerivedLinkDeletions<int>();
            w.GameDeletesEdge(2, frame);
            w.Cleanup(frame);
            Assert.Equal(0, w.Dangling().Count);
        }

        [Test]
        public void TheEndOfFramePassCatchesWhatTheMembershipPassCouldNot()
        {
            // the stale list is already there (the join snapshot or an older session): the deletion pass alone repairs it
            var w = new World();
            w.Edge(1, 200);
            w.Edge(2, 200);
            w.HostWritesStreet(100, new[] { 1, 2 }, membership: false);
            var frame = new DerivedLinkDeletions<int>();
            w.GameDeletesEdge(2, frame);
            w.EndOfModification(frame);
            w.Cleanup(frame);
            Assert.Equal(0, w.Dangling().Count);
            Assert.SequenceEqual(new[] { 1 }, w.Streets[100]);
            Assert.SequenceEqual(new[] { 1 }, w.Streets[200]);
        }

        [Test]
        public void ABuildingTheHostMovedNeverNamesAReplacedRoadOrStaysListedWhenDeleted()
        {
            var w = new World();
            w.Edge(1, 0);
            w.Edge(2, 0);
            w.Building(50, road: 1);
            w.HostMovesBuilding(50, road: 2); // the host's value: still listed by edge 1 here, not by edge 2
            // the player replaces edge 2 (the game moves the buildings edge 2 lists: none)
            var frame = new DerivedLinkDeletions<int>();
            frame.Edges.Add(2);
            w.EndOfModification(frame);
            w.Cleanup(frame);
            Assert.Equal(0, w.RoadOf[50], "cleared instead of naming the destroyed edge (RoadConnectionSystem reads its geometry)");
            // the host deletes the building: the game removes it from its road's list (none now); edge 1's stale entry goes
            frame.Buildings.Add(50);
            w.EndOfModification(frame);
            w.Cleanup(frame);
            Assert.Equal(0, w.Buildings[1].Count);
            Assert.Equal(0, w.Dangling().Count);
        }
    }
}
