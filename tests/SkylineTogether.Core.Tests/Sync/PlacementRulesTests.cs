using System;
using System.Collections.Generic;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>
    /// The host's placement checks of structure proposals (ARCHITECTURE.md#host-validation-and-commit): unlocks, owned map
    /// tiles, geometry, prefab classes; which checks a proposal gets; and the header, base-epoch and sender rules.
    /// </summary>
    public sealed class PlacementRulesTests
    {
        private static PrefabFacts Prefab(PrefabKind kind, long key = 1, bool locked = false, float maxEdge = 0, bool exempt = false)
            => new PrefabFacts { Known = true, Key = key, Kind = kind, Locked = locked, MaxEdgeLength = maxEdge, TileExempt = exempt };

        private static PlacementCandidate Building(float x, float z, bool created = true, bool locked = false)
        {
            var c = new PlacementCandidate();
            c.Reset();
            c.NetId = 7;
            c.Shape = CandidateShape.Object;
            c.Created = created;
            c.Prefab = Prefab(PrefabKind.Object, 5, locked);
            c.HasPosition = true;
            c.Position = new PlacementPoint(x, 0, z);
            return c;
        }

        private static List<PlacementPoint> Square(float x0, float z0, float size)
        {
            return new List<PlacementPoint>
            {
                new PlacementPoint(x0, 0, z0), new PlacementPoint(x0 + size, 0, z0), new PlacementPoint(x0 + size, 0, z0 + size), new PlacementPoint(x0, 0, z0 + size),
            };
        }

        /// <summary>Two tiles side by side: x 0..100 owned, x 100..200 not; the map spans x 0..200, z 0..100.</summary>
        private static PlacementContext TwoTiles()
        {
            var ctx = new PlacementContext();
            ctx.Tiles.Add(true, Square(0, 0, 100));
            ctx.Tiles.Add(false, Square(100, 0, 100));
            ctx.SetMapBounds(0, 0, 200, 100);
            ctx.SetHeightRange(0, 1000);
            return ctx;
        }

        private static RejectReason Check(PlacementCandidate c, PlacementContext ctx) => PlacementValidator.Check(c, ctx, out _);

        // ------------------------------------------------------------------------------------------------ policy

        [Test]
        public void PolicyRelaxesOnlyWhatModsAllow()
        {
            Assert.Equal(PlacementChecks.All, PlacementPolicy.For(ProposalFlags.ToolApply, true, PlacementChecks.None));
            Assert.Equal(PlacementChecks.Unlocks, PlacementPolicy.For(ProposalFlags.ToolApply, true, PlacementChecks.Tiles | PlacementChecks.Geometry), "Anarchy keeps the unlock check");
            Assert.Equal(PlacementChecks.None, PlacementPolicy.For(ProposalFlags.DirectTool, true, PlacementChecks.None), "Move It's moves are free");
            Assert.Equal(PlacementChecks.All, PlacementPolicy.For(ProposalFlags.DirectTool, false, PlacementChecks.None), "no mods on the host: the flag is a forgery");
            Assert.Equal(PlacementChecks.All, PlacementPolicy.For(ProposalFlags.UiDelete, true, PlacementChecks.None));
        }

        [Test]
        public void ChecksSwitchedOffAreNotApplied()
        {
            PlacementContext ctx = TwoTiles();
            PlacementCandidate c = Building(150, 50, locked: true);
            Assert.Equal(RejectReason.Locked, Check(c, ctx));
            ctx.Checks = PlacementChecks.Tiles | PlacementChecks.Geometry;
            Assert.Equal(RejectReason.OutsideTiles, Check(c, ctx));
            ctx.Checks = PlacementChecks.Geometry;
            Assert.Equal(RejectReason.None, Check(c, ctx));
            ctx.Checks = PlacementChecks.None;
            Assert.Equal(RejectReason.None, Check(c, ctx));
        }

        // ------------------------------------------------------------------------------------------------ unlocks

        [Test]
        public void LockedPrefabsCannotBeCreated()
        {
            var ctx = new PlacementContext();
            Assert.Equal(RejectReason.Locked, Check(Building(10, 10, locked: true), ctx));
            Assert.Equal(RejectReason.None, Check(Building(10, 10, locked: false), ctx));
            // an unknown prefab is the unknown-asset check's business
            PlacementCandidate unknown = Building(10, 10, locked: true);
            unknown.Prefab.Known = false;
            Assert.Equal(RejectReason.None, Check(unknown, ctx));
        }

        [Test]
        public void OwnedEntitiesAndNodesMayUseLockedPrefabs()
        {
            var ctx = new PlacementContext();
            PlacementCandidate subNet = Building(10, 10, locked: true);
            subNet.Shape = CandidateShape.Edge;
            subNet.Prefab = Prefab(PrefabKind.Net, 9, locked: true);
            subNet.Owned = true; // a cargo station's own tracks
            Assert.Equal(RejectReason.None, Check(subNet, ctx));
            subNet.Owned = false;
            Assert.Equal(RejectReason.Locked, Check(subNet, ctx));
            subNet.Shape = CandidateShape.Node; // nodes take their edges' prefab
            Assert.Equal(RejectReason.None, Check(subNet, ctx));
        }

        [Test]
        public void SplittingALockedNetKeepsItsPrefab()
        {
            // connecting a road to a pre-placed highway splits the highway: new edges of a locked prefab
            var ctx = new PlacementContext();
            var c = new PlacementCandidate();
            c.Shape = CandidateShape.Edge;
            c.Created = true;
            c.Prefab = Prefab(PrefabKind.Net, 42, locked: true);
            Assert.Equal(RejectReason.Locked, Check(c, ctx));
            ctx.TouchedPrefabs.Add(42); // the proposal deletes or edits an existing edge of that prefab
            Assert.Equal(RejectReason.None, Check(c, ctx));
        }

        [Test]
        public void EditsMayKeepALockedPrefabButNotSwapOneIn()
        {
            var ctx = new PlacementContext();
            var c = new PlacementCandidate();
            c.Shape = CandidateShape.Edge;
            c.Created = false;
            c.OldPrefab = Prefab(PrefabKind.Net, 42, locked: true);
            c.Prefab = Prefab(PrefabKind.Net, 42, locked: true);
            Assert.Equal(RejectReason.None, Check(c, ctx), "upgrading a pre-placed highway keeps its prefab");
            c.OldPrefab = Prefab(PrefabKind.Net, 3);
            Assert.Equal(RejectReason.Locked, Check(c, ctx), "replacing a road with a locked highway");
            c.Prefab.Locked = false;
            Assert.Equal(RejectReason.None, Check(c, ctx));
        }

        // ------------------------------------------------------------------------------------------------ classes

        [Test]
        public void PrefabsMustMatchTheEntityClass()
        {
            var ctx = new PlacementContext { Checks = PlacementChecks.None }; // not relaxable by mods
            PlacementCandidate c = Building(10, 10);
            c.Prefab = Prefab(PrefabKind.Net);
            Assert.Equal(RejectReason.PrefabClass, Check(c, ctx));
            c.Shape = CandidateShape.Node;
            Assert.Equal(RejectReason.None, Check(c, ctx));
            c.Shape = CandidateShape.Area;
            Assert.Equal(RejectReason.PrefabClass, Check(c, ctx));
            c.Shape = CandidateShape.Other; // mod entities, aggregates: not judged
            Assert.Equal(RejectReason.None, Check(c, ctx));
            c.Shape = CandidateShape.Object;
            c.Prefab = Prefab(PrefabKind.Unknown);
            Assert.Equal(RejectReason.None, Check(c, ctx));
        }

        [Test]
        public void EditsCannotSwapThePrefabAcrossClasses()
        {
            var ctx = new PlacementContext { Checks = PlacementChecks.None };
            var c = new PlacementCandidate();
            c.Shape = CandidateShape.Edge;
            c.Prefab = Prefab(PrefabKind.Net, 2);
            c.OldPrefab = Prefab(PrefabKind.Net, 1);
            Assert.Equal(RejectReason.None, Check(c, ctx), "road upgrade");
            c.OldPrefab = Prefab(PrefabKind.Object, 1);
            Assert.Equal(RejectReason.PrefabClass, Check(c, ctx));
        }

        // ------------------------------------------------------------------------------------------------ tiles

        [Test]
        public void TileMapClassifiesWithAMarginAroundOwnedTiles()
        {
            PlacementContext ctx = TwoTiles();
            Assert.Equal(TileVerdict.Owned, ctx.Tiles.Classify(50, 50, 2));
            Assert.Equal(TileVerdict.Unowned, ctx.Tiles.Classify(150, 50, 2));
            Assert.Equal(TileVerdict.Owned, ctx.Tiles.Classify(101.5f, 50, 2), "within the margin of the owned border");
            Assert.Equal(TileVerdict.Unowned, ctx.Tiles.Classify(102.5f, 50, 2));
            Assert.Equal(TileVerdict.NoTile, ctx.Tiles.Classify(250, 50, 2));
            Assert.Equal(TileVerdict.NoTile, new TileMap().Classify(0, 0, 2));
        }

        [Test]
        public void NewObjectsAndNodesMustBeOnOwnedTiles()
        {
            PlacementContext ctx = TwoTiles();
            Assert.Equal(RejectReason.None, Check(Building(50, 50), ctx));
            Assert.Equal(RejectReason.OutsideTiles, Check(Building(150, 50), ctx));
            PlacementCandidate node = Building(150, 50);
            node.Shape = CandidateShape.Node;
            node.Prefab = Prefab(PrefabKind.Net);
            Assert.Equal(RejectReason.OutsideTiles, Check(node, ctx));
            node.Prefab.TileExempt = true; // an airspace path
            Assert.Equal(RejectReason.None, Check(node, ctx));
        }

        [Test]
        public void TileChecksSkipOwnedEntitiesAndMapsWithoutTiles()
        {
            PlacementContext ctx = TwoTiles();
            PlacementCandidate c = Building(150, 50);
            c.Owned = true;
            Assert.Equal(RejectReason.None, Check(c, ctx));
            var noTiles = new PlacementContext();
            Assert.Equal(RejectReason.None, Check(Building(150, 50), noTiles));
        }

        [Test]
        public void OnlyMovesAreCheckedForExistingEntities()
        {
            PlacementContext ctx = TwoTiles();
            // an existing building on an unowned tile (pre-placed) that the player edits without moving it
            PlacementCandidate c = Building(150, 50, created: false);
            c.OldPrefab = c.Prefab;
            c.HasOldPosition = true;
            c.OldPosition = new PlacementPoint(150.2f, 0, 50);
            Assert.Equal(RejectReason.None, Check(c, ctx));
            // relocated from the owned tile onto the unowned one
            c.OldPosition = new PlacementPoint(50, 0, 50);
            Assert.Equal(RejectReason.OutsideTiles, Check(c, ctx));
        }

        [Test]
        public void AreaNodesMustBeOnOwnedTiles()
        {
            PlacementContext ctx = TwoTiles();
            var c = new PlacementCandidate();
            c.Shape = CandidateShape.Area;
            c.Created = true;
            c.Complete = true;
            c.Prefab = Prefab(PrefabKind.Area);
            c.Polygon.AddRange(Square(10, 10, 80));
            Assert.Equal(RejectReason.None, Check(c, ctx));
            c.Polygon.Clear();
            c.Polygon.AddRange(Square(60, 10, 80)); // reaches x 140
            Assert.Equal(RejectReason.OutsideTiles, Check(c, ctx));
            // an existing district reaching onto the unowned tile: only moved nodes count
            c.Created = false;
            c.OldPolygon.AddRange(c.Polygon);
            Assert.Equal(RejectReason.None, Check(c, ctx));
        }

        // ------------------------------------------------------------------------------------------------ geometry

        [Test]
        public void PositionsMustBeInsideTheMapAndNearTheTerrain()
        {
            var ctx = new PlacementContext();
            ctx.SetMapBounds(-100, -100, 100, 100);
            ctx.SetHeightRange(0, 500);
            Assert.Equal(RejectReason.None, Check(Building(100.5f, 0), ctx), "within the margin");
            Assert.Equal(RejectReason.Geometry, Check(Building(102, 0), ctx));
            Assert.Equal(RejectReason.Geometry, Check(Building(0, -5000), ctx));
            PlacementCandidate deep = Building(0, 0);
            deep.Position = new PlacementPoint(0, -900, 0); // a deep tunnel stays plausible
            Assert.Equal(RejectReason.None, Check(deep, ctx));
            deep.Position = new PlacementPoint(0, -2000, 0);
            Assert.Equal(RejectReason.Geometry, Check(deep, ctx));
            deep.Position = new PlacementPoint(0, 1500, 0);
            Assert.Equal(RejectReason.None, Check(deep, ctx));
            deep.Position = new PlacementPoint(0, 1600, 0);
            Assert.Equal(RejectReason.Geometry, Check(deep, ctx));
        }

        private static PlacementCandidate Edge(float length, float maxEdge = 200)
        {
            var c = new PlacementCandidate();
            c.Shape = CandidateShape.Edge;
            c.Created = true;
            c.Prefab = Prefab(PrefabKind.Net, 3, maxEdge: maxEdge);
            c.StartNode = 1;
            c.EndNode = 2;
            c.HasCurve = true;
            c.CurveStart = new PlacementPoint(0, 10, 0);
            c.CurveEnd = new PlacementPoint(length, 10, 0);
            c.HasNodePositions = true;
            c.StartPosition = c.CurveStart;
            c.EndPosition = c.CurveEnd;
            return c;
        }

        [Test]
        public void EdgesMustEndOnTheirNodes()
        {
            var ctx = new PlacementContext();
            PlacementCandidate c = Edge(100);
            Assert.Equal(RejectReason.None, Check(c, ctx));
            c.EndPosition = new PlacementPoint(100.8f, 30, 0); // a waterway keeps its own height; x within the tolerance
            Assert.Equal(RejectReason.None, Check(c, ctx));
            c.EndPosition = new PlacementPoint(120, 10, 0);
            Assert.Equal(RejectReason.Geometry, Check(c, ctx));
            c.HasNodePositions = false; // a node unknown here: nothing to compare
            Assert.Equal(RejectReason.None, Check(c, ctx));
        }

        [Test]
        public void EdgesCannotBeLoopsOrLongerThanTheGameBuildsThem()
        {
            var ctx = new PlacementContext();
            PlacementCandidate loop = Edge(100);
            loop.EndNode = loop.StartNode;
            Assert.Equal(RejectReason.Geometry, Check(loop, ctx));
            // 200 m edges: the game refuses more than 220 m between the node footprints
            Assert.Equal(RejectReason.None, Check(Edge(280), ctx), "node footprints add slack");
            Assert.Equal(RejectReason.Geometry, Check(Edge(300), ctx));
            Assert.Equal(RejectReason.None, Check(Edge(5000, 0), ctx), "no length limit known");
        }

        [Test]
        public void AreaOutlinesMustNotCrossThemselves()
        {
            var ctx = new PlacementContext();
            var c = new PlacementCandidate();
            c.Shape = CandidateShape.Area;
            c.Created = true;
            c.Complete = true;
            c.Prefab = Prefab(PrefabKind.Area);
            c.Polygon.AddRange(Square(0, 0, 10));
            Assert.Equal(RejectReason.None, Check(c, ctx));
            // a bow tie
            c.Polygon.Clear();
            c.Polygon.Add(new PlacementPoint(0, 0, 0));
            c.Polygon.Add(new PlacementPoint(10, 0, 10));
            c.Polygon.Add(new PlacementPoint(10, 0, 0));
            c.Polygon.Add(new PlacementPoint(0, 0, 10));
            Assert.Equal(RejectReason.Geometry, Check(c, ctx));
            // collinear: no area
            c.Polygon.Clear();
            c.Polygon.Add(new PlacementPoint(0, 0, 0));
            c.Polygon.Add(new PlacementPoint(5, 0, 0));
            c.Polygon.Add(new PlacementPoint(10, 0, 0));
            Assert.Equal(RejectReason.Geometry, Check(c, ctx));
            // too few nodes for a closed area; an open one is not judged
            c.Polygon.RemoveAt(2);
            Assert.Equal(RejectReason.Geometry, Check(c, ctx));
            c.Complete = false;
            Assert.Equal(RejectReason.None, Check(c, ctx));
        }

        [Test]
        public void PolygonHelpersAgreeWithHandComputedValues()
        {
            List<PlacementPoint> square = Square(0, 0, 10);
            Assert.Near(100, Math.Abs(PolygonGeometry.SignedArea(square)), 1e-6);
            Assert.False(PolygonGeometry.SelfIntersects(square, true));
            float[] xs = { 0, 10, 10, 0 }, zs = { 0, 0, 10, 10 };
            Assert.True(PolygonGeometry.Contains(xs, zs, 5, 5));
            Assert.False(PolygonGeometry.Contains(xs, zs, 15, 5));
            Assert.Near(5, PolygonGeometry.DistanceToOutline(xs, zs, 15, 5), 1e-5);
            Assert.Near(2, PolygonGeometry.DistanceToOutline(xs, zs, 5, 2), 1e-5);
            // an L-shaped (concave) outline does not cross itself
            var l = new List<PlacementPoint>
            {
                new PlacementPoint(0, 0, 0), new PlacementPoint(10, 0, 0), new PlacementPoint(10, 0, 5), new PlacementPoint(5, 0, 5), new PlacementPoint(5, 0, 10), new PlacementPoint(0, 0, 10),
            };
            Assert.False(PolygonGeometry.SelfIntersects(l, true));
            Assert.Near(75, Math.Abs(PolygonGeometry.SignedArea(l)), 1e-6);
        }

        [Test]
        public void ExistingEdgesAreJudgedOnlyWhenMoved()
        {
            // the game layer leaves HasCurve false for an edit that kept the curve (an upgrade)
            var ctx = new PlacementContext();
            PlacementCandidate c = Edge(5000);
            c.Created = false;
            c.HasCurve = false;
            Assert.Equal(RejectReason.None, Check(c, ctx));
        }

        // ------------------------------------------------------------------------------------------------ header, base, sender

        [Test]
        public void WorldChangingProposalsNeedExactlyOneHeader()
        {
            Assert.Equal(RejectReason.Invalid, ProposalRules.CheckHeader(new ProposalFacts { Spawns = 1 }));
            Assert.Equal(RejectReason.Invalid, ProposalRules.CheckHeader(new ProposalFacts { Despawns = 1 }));
            Assert.Equal(RejectReason.Invalid, ProposalRules.CheckHeader(new ProposalFacts { ZoneBlocks = 1 }));
            Assert.Equal(RejectReason.Invalid, ProposalRules.CheckHeader(new ProposalFacts { HasCost = true }));
            Assert.Equal(RejectReason.None, ProposalRules.CheckHeader(new ProposalFacts { Spawns = 1, Headers = 1 }));
            Assert.Equal(RejectReason.Invalid, ProposalRules.CheckHeader(new ProposalFacts { Spawns = 1, Headers = 2 }));
            Assert.Equal(RejectReason.None, ProposalRules.CheckHeader(new ProposalFacts()), "request-only and global batches need none");
            Assert.Equal(RejectReason.Invalid, ProposalRules.CheckHeader(new ProposalFacts { Headers = 2 }));
        }

        [Test]
        public void ProposalsBasedOnUnknownOrPrunedEpochsAreRefused()
        {
            var ledger = new AuthoredLedger();
            Assert.Equal(RejectReason.None, ledger.CheckBase(0), "nothing broadcast yet");
            Assert.Equal(RejectReason.Invalid, ledger.CheckBase(1), "an epoch the host never sent");
            for (int i = 0; i < 10; i++) ledger.Advance();
            Assert.Equal(RejectReason.None, ledger.CheckBase(10));
            Assert.Equal(RejectReason.None, ledger.CheckBase(0));
            Assert.Equal(RejectReason.Invalid, ledger.CheckBase(11));
            for (uint i = 0; i < AuthoredLedger.Horizon; i++) ledger.Advance();
            uint last = ledger.LastBroadcast;
            Assert.Equal(RejectReason.None, ledger.CheckBase(last - AuthoredLedger.Horizon + 1));
            Assert.Equal(RejectReason.None, ledger.CheckBase(ledger.Current - AuthoredLedger.Horizon));
            Assert.Equal(RejectReason.Timeout, ledger.CheckBase(ledger.Current - AuthoredLedger.Horizon - 1));
            Assert.Equal(RejectReason.Timeout, ledger.CheckBase(0));
        }

        [Test]
        public void TheTimeoutBoundNeverHidesAConflict()
        {
            // while a base is accepted, every authored change newer than it is still in the ledger (pruning included)
            var ledger = new AuthoredLedger();
            for (int i = 0; i < 5000; i++) ledger.Advance();
            ledger.MarkAuthored(77, 9);
            uint baseBefore = ledger.Advance() - 1; // a client that had not seen the change
            int accepted = 0;
            while (ledger.CheckBase(baseBefore) == RejectReason.None)
            {
                if (!ledger.IsConflict(77, baseBefore, 3)) Assert.Fail("the change was pruned while proposals based before it were still accepted (after " + accepted + " epochs)");
                ledger.Advance();
                accepted++;
            }
            Assert.Equal(RejectReason.Timeout, ledger.CheckBase(baseBefore));
            Assert.True(accepted >= AuthoredLedger.Horizon - 2, "accepted for " + accepted + " epochs only");
        }

        [Test]
        public void SenderMustBeLive()
        {
            Assert.Equal(RejectReason.None, ProposalRules.CheckSender(true));
            Assert.Equal(RejectReason.NotLive, ProposalRules.CheckSender(false));
        }

        [Test]
        public void HostOnlyCountsAsAViolationButPlacementRefusalsDoNot()
        {
            Assert.True(ViolationTracker.IsViolation(RejectReason.HostOnly));
            Assert.True(ViolationTracker.IsViolation(RejectReason.Invalid));
            foreach (RejectReason r in new[] { RejectReason.Locked, RejectReason.OutsideTiles, RejectReason.Geometry, RejectReason.PrefabClass, RejectReason.Timeout, RejectReason.NotLive })
            {
                Assert.False(ViolationTracker.IsViolation(r), r + " must not lead to a kick (a mod the rules do not know)");
            }
        }

        // ------------------------------------------------------------------------------------------------ toasts

        [Test]
        public void EveryReasonHasADistinctLocaleKey()
        {
            var keys = new HashSet<string>();
            foreach (RejectReason r in Enum.GetValues(typeof(RejectReason)))
            {
                if (r == RejectReason.None) continue;
                string key = ProposalRules.LocaleKey(r);
                Assert.True(key.StartsWith("st.reject.", StringComparison.Ordinal), key);
                Assert.True(keys.Add(key), "two reasons share " + key);
                Assert.DoesNotContain("rejected (", ProposalRules.Describe(r), r + " has no English description");
            }
            Assert.Equal("st.reject.other", ProposalRules.LocaleKey((RejectReason)999));
        }
    }
}
