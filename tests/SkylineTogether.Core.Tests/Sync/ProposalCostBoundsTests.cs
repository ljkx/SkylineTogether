using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>The host's bounds on what a client's proposal costs.</summary>
    public sealed class ProposalCostBoundsTests
    {
        [Test]
        public void APlausibleCostIsKept()
        {
            Assert.Equal(1200L, ProposalRules.EffectiveCost(1200, 1000, 0), "more than the construction cost (sub-objects, nets)");
            Assert.Equal(-300L, ProposalRules.EffectiveCost(-300, 0, 500), "a refund within the recent modification costs");
            Assert.Equal(700L, ProposalRules.EffectiveCost(700, 1000, 400), "a replacement: new cost minus a refund");
            Assert.Equal(0L, ProposalRules.EffectiveCost(0, 0, 0));
        }

        [Test]
        public void AMissingOrUnderstatedChargeIsRaisedToTheConstructionCost()
        {
            Assert.Equal(1000L, ProposalRules.EffectiveCost(0, 1000, 0), "the cost record was left out");
            Assert.Equal(1000L, ProposalRules.EffectiveCost(10, 1000, 0));
        }

        [Test]
        public void AForgedRefundIsCappedAtWhatTheRemovedEntitiesMayRefund()
        {
            Assert.Equal(-500L, ProposalRules.EffectiveCost(-2_000_000_000, 0, 500));
            Assert.Equal(0L, ProposalRules.EffectiveCost(-1000, 0, 0), "nothing recent was removed: no refund at all");
            Assert.Equal(600L, ProposalRules.EffectiveCost(-5000, 1000, 400), "creation and removal in one proposal");
        }

        [Test]
        public void CostsStayWithinTheGamesMoney()
        {
            Assert.Equal((long)int.MaxValue, ProposalRules.EffectiveCost(long.MaxValue, 0, 0));
            Assert.Equal((long)int.MaxValue, ProposalRules.EffectiveCost(0, long.MaxValue / 2, 0));
            Assert.Equal(-(long)int.MaxValue, ProposalRules.EffectiveCost(long.MinValue, 0, long.MaxValue));
            Assert.Equal(0L, ProposalRules.EffectiveCost(0, -50, -50), "negative bounds count as none");
        }

        [Test]
        public void TheRefundBoundScalesTheRecentCostsByTheLargestRefundPercentage()
        {
            Assert.Equal(1000L, ProposalRules.RefundBound(1000, 1.0));
            Assert.Equal(750L, ProposalRules.RefundBound(1000, 0.75));
            Assert.Equal(1000L, ProposalRules.RefundBound(1000, double.NaN), "unknown percentage: the full cost");
            Assert.Equal(4000L, ProposalRules.RefundBound(1000, 50), "a modded percentage is clamped");
            Assert.Equal(0L, ProposalRules.RefundBound(-10, 1));
            Assert.Equal((long)int.MaxValue, ProposalRules.RefundBound(long.MaxValue, 1));
        }
    }
}
