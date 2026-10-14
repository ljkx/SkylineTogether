using System;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>The host's construction-cost bounds for nets and trees (<see cref="ConstructionCosts"/>).</summary>
    public sealed class ConstructionCostsTests
    {
        [Test]
        public void EdgesAreBilledPerEightMetresLikeTheGame()
        {
            Assert.Equal(1, ConstructionCosts.EdgeUnits(0.5f), "at least one unit");
            Assert.Equal(1, ConstructionCosts.EdgeUnits(8f));
            Assert.Equal(2, ConstructionCosts.EdgeUnits(12.1f));
            Assert.Equal(2, ConstructionCosts.EdgeUnits(12f), "a half unit rounds to even, as Mathf.RoundToInt does");
            Assert.Equal(12, ConstructionCosts.EdgeUnits(100f), "12.5 rounds to even");
            Assert.Equal(13, ConstructionCosts.EdgeUnits(101f));
            Assert.Equal(1, ConstructionCosts.EdgeUnits(float.NaN));
            Assert.Equal(0, ConstructionCosts.ElevationSteps(-5f, 4f));
            Assert.Equal(2, ConstructionCosts.ElevationSteps(0f, 20f), "the higher end decides");
            // 100 m at 50 per unit, 2 steps of 30: 12 × (50 + 60)
            Assert.Equal(12L * 110, ConstructionCosts.EdgeCost(100f, 50, 30, 2));
        }

        [Test]
        public void ACreatedEdgeNeverCostsLessThanTheFloor()
        {
            var rng = new Random(5);
            for (int i = 0; i < 2000; i++)
            {
                float length = (float)(rng.NextDouble() * 600);
                uint unit = (uint)rng.Next(1, 5000);
                uint elevation = (uint)rng.Next(0, 3000);
                int steps = rng.Next(0, 6);
                long vanilla = ConstructionCosts.EdgeCost(length, unit, elevation, steps);
                Assert.True(ConstructionCosts.CreatedEdgeFloor(length, unit) <= vanilla, "floor above the game's price at " + length + " m");
            }
            Assert.Equal(11L * 50, ConstructionCosts.CreatedEdgeFloor(100f, 50), "one unit of slack, no elevation surcharge");
            Assert.Equal(0L, ConstructionCosts.CreatedEdgeFloor(6f, 50), "a one-unit edge has no floor");
        }

        [Test]
        public void SplittingAnEdgeStaysFree()
        {
            // the game charges nothing for the halves of an edge split by a new connection; the floor must not either
            var rng = new Random(9);
            for (int i = 0; i < 5000; i++)
            {
                float whole = (float)(4 + rng.NextDouble() * 400);
                int pieces = rng.Next(2, 4);
                float rest = whole;
                long created = 0;
                for (int p = 0; p < pieces; p++)
                {
                    float piece = p == pieces - 1 ? rest : (float)(rest * rng.NextDouble());
                    rest -= piece;
                    created += ConstructionCosts.CreatedEdgeFloor(piece, 70);
                }
                long removed = ConstructionCosts.RemovedEdgeCeiling(whole, 70);
                Assert.Equal(0L, ConstructionCosts.NetFloor(created, removed, 0), "a split of " + whole + " m into " + pieces + " pieces");
            }
        }

        [Test]
        public void ANewRoadBesideASplitStillPaysForItsOwnLength()
        {
            // a 200 m road joined to the middle of a 100 m road: the old road is split, the new one is billed
            long created = ConstructionCosts.CreatedEdgeFloor(200f, 50) + ConstructionCosts.CreatedEdgeFloor(40f, 50) + ConstructionCosts.CreatedEdgeFloor(60f, 50);
            long removed = ConstructionCosts.RemovedEdgeCeiling(100f, 50);
            long floor = ConstructionCosts.NetFloor(created, removed, 0);
            long vanillaNewRoad = ConstructionCosts.EdgeCost(200f, 50, 0, 0);
            Assert.True(floor > 0 && floor <= vanillaNewRoad, "floor " + floor + " of " + vanillaNewRoad);
            Assert.True(floor >= vanillaNewRoad - 4 * 50, "at most a few units of slack");
        }

        [Test]
        public void UpgradesPayTheDifferenceAndDowngradesNothing()
        {
            // 80 m: 10 units; old 40 per unit, new 65 per unit, at ground level
            Assert.Equal(10L * 25 - 65, ConstructionCosts.UpgradeFloor(80f, 0, 40, 0, 65, 0));
            Assert.Equal(0L, ConstructionCosts.UpgradeFloor(80f, 0, 65, 0, 40, 0), "a cheaper composition is never charged");
            // elevated: a cheaper elevation surcharge can make the game's price lower than the unit difference suggests
            long vanilla = Math.Max(0, ConstructionCosts.EdgeCost(80f, 65, 10, 3) - ConstructionCosts.EdgeCost(80f, 40, 30, 3));
            Assert.True(ConstructionCosts.UpgradeFloor(80f, 3, 40, 30, 65, 10) <= vanilla);
        }

        [Test]
        public void TreesArePricedByGrowthStage()
        {
            Assert.Equal(100L, ConstructionCosts.TreeCost(100, TreeGrowth.Base, 2, 5, 10));
            Assert.Equal(200L, ConstructionCosts.TreeCost(100, TreeGrowth.Teen, 2, 5, 10));
            Assert.Equal(500L, ConstructionCosts.TreeCost(100, TreeGrowth.Adult, 2, 5, 10));
            Assert.Equal(1000L, ConstructionCosts.TreeCost(100, TreeGrowth.Elderly, 2, 5, 10));
            Assert.Equal(0L, ConstructionCosts.TreeCost(100, TreeGrowth.Adult, 2, -5, 10), "a broken multiplier never refunds");
        }

        [Test]
        public void TheFloorsKeepAnHonestClientsCostAndRaiseAForgedOne()
        {
            long floor = ConstructionCosts.NetFloor(ConstructionCosts.CreatedEdgeFloor(160f, 50), 0, 0); // 19 units
            long honest = ConstructionCosts.EdgeCost(160f, 50, 0, 0);
            Assert.Equal(honest, ProposalRules.EffectiveCost(honest, floor, 0), "an honest client's cost stays");
            Assert.Equal(floor, ProposalRules.EffectiveCost(0, floor, 0), "a forged zero is raised to the floor");
        }
    }
}
