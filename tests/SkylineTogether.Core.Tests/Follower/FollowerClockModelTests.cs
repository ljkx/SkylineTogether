using SkylineTogether.Core.Follower;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Follower
{
    /// <summary>The follower clock's host-frame estimate and correction rule.</summary>
    public sealed class FollowerClockModelTests
    {
        [Test]
        public void EstimateAdvancesAtTheRateAndFollowsFreshSamples()
        {
            var m = new FollowerClockModel();
            Assert.False(m.HasEstimate);
            m.SetRate(60, 0);
            m.AddSample(1000, 0);
            Assert.Near(1060, m.Estimate(1.0), 1e-9);
            // a fresher (larger) sample wins at once
            m.AddSample(1070, 1.0);
            Assert.Near(1070, m.Estimate(1.0), 1e-9);
            // a late sample only pulls the estimate down slowly
            m.AddSample(1050, 1.0);
            Assert.InRange(m.Estimate(1.0), 1068, 1070);
        }

        [Test]
        public void RateChangesKeepTheEstimateContinuousAndPauseFreezesIt()
        {
            var m = new FollowerClockModel();
            m.SetRate(60, 0);
            m.AddSample(0, 0);
            m.SetRate(240, 2.0);
            Assert.Near(120, m.Estimate(2.0), 1e-9);
            Assert.Near(360, m.Estimate(3.0), 1e-9);
            m.SetRate(0, 3.0);
            Assert.Near(360, m.Estimate(10.0), 1e-9);
        }

        [Test]
        public void FarSamplesResetTheEstimate()
        {
            var m = new FollowerClockModel();
            m.SetRate(60, 0);
            m.AddSample(100, 0);
            m.AddSample(50000, 0.5);
            Assert.Near(50000, m.Estimate(0.5), 1e-9);
        }

        [Test]
        public void SamplesAcrossTheWrapAreUnwrapped()
        {
            var m = new FollowerClockModel();
            m.SetRate(60, 0);
            m.AddSample(uint.MaxValue - 10, 0);
            m.AddSample(5, 0.1);
            Assert.InRange(m.Estimate(0.1), 4.0, 12.0);
        }

        [Test]
        public void CorrectionRule()
        {
            ClockCorrection none = FollowerClockModel.Decide(100, 101, false);
            Assert.False(none.Jump);
            Assert.Equal(0, none.SkipTicks);
            Assert.False(none.Hold);
            Assert.Equal(FollowerClockModel.MaxSkipPerFrame, FollowerClockModel.Decide(100, 150, false).SkipTicks);
            Assert.Equal(1, FollowerClockModel.Decide(100, 103, false).SkipTicks);
            Assert.True(FollowerClockModel.Decide(100, 90, false).Hold);
            ClockCorrection jump = FollowerClockModel.Decide(100, 1000, false);
            Assert.True(jump.Jump);
            Assert.Equal(1000u, jump.SetFrame);
            Assert.True(FollowerClockModel.Decide(100, 101, true).Jump, "paused: any difference jumps");
            Assert.Equal(3, FollowerClockModel.Decide(uint.MaxValue - 1, 3, false).SkipTicks);
        }
    }
}
