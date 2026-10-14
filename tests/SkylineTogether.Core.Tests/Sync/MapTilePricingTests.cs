using System.Collections.Generic;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>w9/perf (trust): the host's own map tile price and purchase checks.</summary>
    public sealed class MapTilePricingTests
    {
        [Test]
        public void BaselineModifiersNormaliseAreaResourcesAndWater()
        {
            Assert.Near(1.0 / (623.304347826087 * 623.304347826087), MapTilePricing.BaselineModifier(0), 1e-18);
            Assert.Equal(MapTilePricing.BaselineModifier(0), MapTilePricing.BaselineModifier(1));
            foreach (int f in new[] { 2, 3, 4, 5, 7 }) Assert.Equal(8.0718994140625E-07, MapTilePricing.BaselineModifier(f));
            Assert.Equal(1.0, MapTilePricing.BaselineModifier(6));
            Assert.Equal(1.0, MapTilePricing.BaselineModifier(8));
            Assert.Equal(1.0, MapTilePricing.BaselineModifier(-1));
        }

        [Test]
        public void ATilesValueSumsItsFeatures()
        {
            // surface water (feature 6, baseline 1): 2 × 1 × 10 × cost 3 × factor 5 = 300
            var amounts = new List<float> { 0, 0, 0, 0, 0, 0, 2 };
            var costs = new List<float> { 1, 1, 1, 1, 1, 1, 3 };
            Assert.Near(300.0, MapTilePricing.TileValue(amounts, costs, 5), 1e-3);
            // a feature without a prefab cost is free
            Assert.Near(0.0, MapTilePricing.TileValue(new List<float> { 0, 0, 0, 0, 0, 0, 0, 0, 9 }, costs, 5), 1e-6);
        }

        [Test]
        public void CorruptedFeatureValuesNeverMakeATileCheaperThanFreeOrNaN()
        {
            var costs = new List<float> { 1, 1, 1, 1, 1, 1, 1 };
            Assert.Equal(0f, MapTilePricing.TileValue(new List<float> { 0, 0, 0, 0, 0, 0, -50 }, costs, 1));
            Assert.Equal(0f, MapTilePricing.TileValue(new List<float> { 0, 0, 0, 0, 0, 0, float.NaN }, costs, 1));
            Assert.Equal(0f, MapTilePricing.TileValue(new List<float> { 0, 0, 0, 0, 0, 0, 1 }, costs, float.PositiveInfinity));
            Assert.Equal(0f, MapTilePricing.TileValue(null, costs, 1));
        }

        [Test]
        public void TheMostValuableTileIsMultipliedByTheOwnedCount()
        {
            // 9 owned: the 300 tile × 9, the 100 tile × 10, the 50 tile × 11
            var values = new List<float> { 100, 50, 300 };
            Assert.Equal(300 * 9 + 100 * 10 + 50 * 11, MapTilePricing.Cost(values, 9));
        }

        [Test]
        public void CostRoundsAndClamps()
        {
            Assert.Equal(0, MapTilePricing.Cost(new List<float>(), 9));
            Assert.Equal(0, MapTilePricing.Cost(null, 9));
            Assert.Equal(2, MapTilePricing.Cost(new List<float> { 2.5f }, 1), "half to even, as the game's rounding");
            Assert.Equal(4, MapTilePricing.Cost(new List<float> { 3.5f }, 1));
            Assert.Equal(int.MaxValue, MapTilePricing.Cost(new List<float> { float.MaxValue }, 9));
            Assert.Equal(0, MapTilePricing.Cost(new List<float> { 100 }, -5), "a negative owned count counts as none");
        }

        [Test]
        public void PermitsAreNineOrTheStartTilesPlusMilestonesMinusOwned()
        {
            Assert.Equal(0, MapTilePricing.AvailablePermits(0, 0, 9));
            Assert.Equal(3, MapTilePricing.AvailablePermits(0, 4, 10));
            Assert.Equal(5, MapTilePricing.AvailablePermits(12, 0, 7), "more start tiles than nine");
            Assert.Equal(0, MapTilePricing.AvailablePermits(9, 0, 50));
        }

        [Test]
        public void ValidateChecksCountPermitsAndMoney()
        {
            Assert.Equal(RejectReason.None, MapTilePricing.Validate(2, 2, 1000, 1000, false));
            Assert.Equal(RejectReason.Locked, MapTilePricing.Validate(3, 2, 10, 1000, false));
            Assert.Equal(RejectReason.NotEnoughMoney, MapTilePricing.Validate(1, 2, 1001, 1000, false));
            Assert.Equal(RejectReason.None, MapTilePricing.Validate(1, 2, 1001, 0, true), "unlimited money");
            Assert.Equal(RejectReason.None, MapTilePricing.Validate(0, 0, 0, 0, false), "already bought: nothing to do");
            Assert.Equal(RejectReason.Invalid, MapTilePricing.Validate(MapTilePricing.MaxTilesPerPurchase + 1, int.MaxValue, 0, 0, true));
            Assert.Equal(RejectReason.Invalid, MapTilePricing.Validate(1, 1, -1, 0, false));
        }

        [Test]
        public void AFreePurchaseClaimIsIgnored()
        {
            // the client claims it paid nothing: the host's own price is what is checked and charged
            int cost = MapTilePricing.Cost(new List<float> { 500 }, 9);
            Assert.Equal(4500, cost);
            Assert.Equal(RejectReason.NotEnoughMoney, MapTilePricing.Validate(1, 5, cost, 100, false));
            Assert.True(MapTilePricing.Differs(0, cost));
            Assert.False(MapTilePricing.Differs(4450, cost), "small drift is not worth a log line");
        }

        [Test]
        public void APurchaseNamingTooManyTilesIsMalformed()
        {
            var a = new ActionIntent { Kind = ActionKind.PurchaseMapTiles, Ids = new uint[MapTilePricing.MaxTilesPerPurchase + 1], Amount = 0 };
            Assert.Equal(RejectReason.Invalid, ActionPayload.Validate(a));
            a.Ids = new uint[] { 5 };
            Assert.Equal(RejectReason.None, ActionPayload.Validate(a));
        }
    }
}
