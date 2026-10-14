using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>Range and shape checks of clients' global-state proposals.</summary>
    public sealed class GlobalProposalRulesTests
    {
        [Test]
        public void TaxOffsetsAreBounded()
        {
            Assert.Equal(12, GlobalProposalRules.ClampTax(12));
            Assert.Equal(100, GlobalProposalRules.ClampTax(int.MaxValue));
            Assert.Equal(-100, GlobalProposalRules.ClampTax(int.MinValue));
        }

        [Test]
        public void BudgetsMustBeAdjustableAndAreClamped()
        {
            Assert.True(GlobalProposalRules.TryBudget(120, true, out int b));
            Assert.Equal(120, b);
            Assert.True(GlobalProposalRules.TryBudget(5000, true, out b));
            Assert.Equal(GlobalProposalRules.MaxBudget, b);
            Assert.True(GlobalProposalRules.TryBudget(-3, true, out b));
            Assert.Equal(0, b);
            Assert.False(GlobalProposalRules.TryBudget(100, false, out _), "a service whose budget the panel does not offer");
        }

        [Test]
        public void FeesStayWithinTheGamesMaximum()
        {
            Assert.True(GlobalProposalRules.TryFee(0.3f, true, 0.5f, out float f));
            Assert.Equal(0.3f, f);
            Assert.True(GlobalProposalRules.TryFee(9f, true, 0.5f, out f));
            Assert.Equal(0.5f, f);
            Assert.True(GlobalProposalRules.TryFee(-1f, true, 0.5f, out f));
            Assert.Equal(0f, f);
            Assert.True(GlobalProposalRules.TryFee(1e9f, true, 0f, out f), "no maximum known: the generic cap");
            Assert.Equal(GlobalProposalRules.MaxFee, f);
            Assert.False(GlobalProposalRules.TryFee(float.NaN, true, 1f, out _));
            Assert.False(GlobalProposalRules.TryFee(float.PositiveInfinity, true, 1f, out _));
            Assert.False(GlobalProposalRules.TryFee(0.1f, false, 1f, out _), "a fee players cannot adjust");
        }

        [Test]
        public void LoansAreNeverNegative()
        {
            Assert.Equal(0, GlobalProposalRules.ClampLoan(-500));
            Assert.Equal(1000, GlobalProposalRules.ClampLoan(1000));
        }

        [Test]
        public void PolicySlidersStayInTheirRange()
        {
            Assert.True(GlobalProposalRules.TryPolicyAdjustment(0.5f, true, 0f, 1f, out float a));
            Assert.Equal(0.5f, a);
            Assert.True(GlobalProposalRules.TryPolicyAdjustment(7f, true, 0f, 1f, out a));
            Assert.Equal(1f, a);
            Assert.True(GlobalProposalRules.TryPolicyAdjustment(-7f, true, 0f, 1f, out a));
            Assert.Equal(0f, a);
            Assert.True(GlobalProposalRules.TryPolicyAdjustment(3e9f, false, 0f, 0f, out a));
            Assert.Equal(GlobalProposalRules.MaxUnrangedAdjustment, a);
            Assert.False(GlobalProposalRules.TryPolicyAdjustment(float.NaN, true, 0f, 1f, out _));
            Assert.False(GlobalProposalRules.TryPolicyAdjustment(0.5f, true, 2f, 1f, out _), "an inverted range");
        }

        [Test]
        public void NamesAreSanitisedAndBounded()
        {
            Assert.Equal("Main Street", GlobalProposalRules.CleanName("Main\u0000 Street‮"));
            Assert.Equal("", GlobalProposalRules.CleanName(null));
            Assert.Equal(GlobalProposalRules.MaxNameChars, GlobalProposalRules.CleanName(new string('a', 5000)).Length);
            Assert.True(GlobalProposalRules.TryCityName("  New\nTown ", out string city));
            Assert.Equal("NewTown", city);
            Assert.False(GlobalProposalRules.TryCityName("\t\r\n  ", out _), "nothing left");
        }

        [Test]
        public void OversizedProposalsAreRefused()
        {
            Assert.True(GlobalProposalRules.WithinEntryLimit(GlobalProposalRules.MaxEntries));
            Assert.False(GlobalProposalRules.WithinEntryLimit(GlobalProposalRules.MaxEntries + 1));
            Assert.False(GlobalProposalRules.WithinEntryLimit(-1));
        }

        [Test]
        public void ProposedWaterSourcesAreCheckedForSaneFloats()
        {
            Assert.Contains("Game.Simulation.WaterSourceData", FloatSanity.CheckedTypeNames);
        }
    }
}
