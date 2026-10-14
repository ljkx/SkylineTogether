using System.Collections.Generic;
using SkylineTogether.Core.Diagnostics;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Diagnostics
{
    /// <summary>The shared per-frame budget: priorities, reservations, floors, carry-over and telemetry.</summary>
    public sealed class FrameBudgetServiceTests
    {
        [Test]
        public void AQuietFrameGivesEveryFeatureItsCap()
        {
            var b = new FrameBudgetService(10);
            FrameBudgetService.Feature apply = b.Register("apply", BudgetPriority.Apply, 6);
            FrameBudgetService.Feature send = b.Register("send", BudgetPriority.CriticalSend, 1.5);
            FrameBudgetService.Feature stats = b.Register("stats", BudgetPriority.Statistics, 1);
            Assert.Equal(6.0, b.Begin(apply));
            b.End(apply, 0.5);
            Assert.Equal(1.5, b.Begin(send));
            b.End(send, 0.5);
            Assert.Equal(1.0, b.Begin(stats));
            b.End(stats, 0.2);
            b.EndFrame();
            Assert.Equal(0L, b.OverBudgetFrames);
            Assert.Equal(0.0, b.DebtMs);
        }

        [Test]
        public void ApplyIsNeverLimitedByTheOthers()
        {
            var b = new FrameBudgetService(4);
            FrameBudgetService.Feature apply = b.Register("apply", BudgetPriority.Apply, 6);
            FrameBudgetService.Feature stats = b.Register("stats", BudgetPriority.Statistics, 1);
            b.Begin(stats);
            b.End(stats, 3.9);
            Assert.Equal(6.0, b.Begin(apply), "apply keeps its cap even though the frame is nearly spent");
        }

        [Test]
        public void LowerPrioritiesGetWhatIsLeftButNeverLessThanTheirFloor()
        {
            var b = new FrameBudgetService(8);
            FrameBudgetService.Feature apply = b.Register("apply", BudgetPriority.Apply, 6);
            FrameBudgetService.Feature integrity = b.Register("integrity", BudgetPriority.Integrity, 2, 0.25);
            b.Begin(apply);
            b.End(apply, 7);
            Assert.Near(1.0, b.Begin(integrity), 1e-9, "8 ms frame - 7 ms spent");
            b.End(integrity, 1.0);
            FrameBudgetService.Feature stats = b.Register("stats", BudgetPriority.Statistics, 2, 0.1);
            Assert.Near(0.2, b.Begin(stats), 1e-9, "nothing left: the floor (10 % of the cap)");
        }

        [Test]
        public void HigherPrioritiesThatDidNotRunYetAreReserved()
        {
            var b = new FrameBudgetService(10);
            FrameBudgetService.Feature send = b.Register("send", BudgetPriority.CriticalSend, 4);
            FrameBudgetService.Feature stats = b.Register("stats", BudgetPriority.Statistics, 8, 0.1);
            // the send feature usually spends 4 ms
            for (int i = 0; i < 30; i++)
            {
                b.Begin(send);
                b.End(send, 4);
                b.EndFrame();
            }
            // statistics run before the sends this frame: their usual 4 ms are kept free
            Assert.Near(6.0, b.Begin(stats), 1e-6);
            b.End(stats, 6);
            Assert.Near(4.0, b.Begin(send), 1e-6);
        }

        [Test]
        public void OverrunsAreCarriedIntoTheNextFramesAndRepaid()
        {
            var b = new FrameBudgetService(10);
            FrameBudgetService.Feature apply = b.Register("apply", BudgetPriority.Apply, 30);
            FrameBudgetService.Feature stats = b.Register("stats", BudgetPriority.Statistics, 5, 0.2);
            b.Begin(apply);
            b.End(apply, 25);
            b.EndFrame();
            Assert.Equal(1L, b.OverBudgetFrames);
            Assert.Near(15.0, b.DebtMs, 1e-9);
            Assert.Near(1.0, b.Begin(stats), 1e-9, "a frame in debt gives the statistics only their floor");
            b.End(stats, 1.0);
            b.EndFrame();
            Assert.Near(6.0, b.DebtMs, 1e-9, "9 ms unused repaid");
            b.EndFrame();
            Assert.Equal(0.0, b.DebtMs);
            b.Begin(apply);
            b.End(apply, 0);
            Assert.Equal(5.0, b.Begin(stats));
        }

        [Test]
        public void DebtIsCappedAtTwoFrames()
        {
            var b = new FrameBudgetService(5);
            FrameBudgetService.Feature apply = b.Register("apply", BudgetPriority.Apply, 100);
            b.Begin(apply);
            b.End(apply, 100);
            b.EndFrame();
            Assert.Near(10.0, b.DebtMs, 1e-9);
        }

        [Test]
        public void ACallMayAskForLessThanTheRegisteredCap()
        {
            var b = new FrameBudgetService(10);
            FrameBudgetService.Feature f = b.Register("values", BudgetPriority.CriticalSend, 1);
            Assert.Near(0.35, b.Begin(f, 0.35), 1e-9);
            Assert.Equal(1.0, b.Begin(f, double.NaN), "an invalid cap falls back to the registered one");
        }

        [Test]
        public void StatisticsReportAverageP95AndMaxOverTheWindow()
        {
            var b = new FrameBudgetService(10);
            FrameBudgetService.Feature f = b.Register("scan", BudgetPriority.CriticalSend, 2);
            for (int i = 1; i <= 100; i++)
            {
                b.Begin(f);
                b.End(f, i / 100.0); // 0.01 .. 1.00 ms
                b.EndFrame();
            }
            FrameBudgetService.FeatureStats s = b.GetStats(f);
            Assert.Near(0.505, s.AverageMs, 1e-9);
            Assert.Near(0.95, s.P95Ms, 1e-9);
            Assert.Near(1.0, s.MaxMs, 1e-9);
            Assert.Equal(100L, s.Frames);
        }

        [Test]
        public void AFrameAFeatureSkipsCountsAsZero()
        {
            var b = new FrameBudgetService(10);
            FrameBudgetService.Feature f = b.Register("maps", BudgetPriority.Statistics, 1);
            b.Begin(f);
            b.End(f, 1.0);
            b.EndFrame();
            b.EndFrame();
            FrameBudgetService.FeatureStats s = b.GetStats(f);
            Assert.Near(0.5, s.AverageMs, 1e-9, "averages are per frame");
            Assert.Equal(1L, s.Frames, "frames run");
        }

        [Test]
        public void TheWindowForgetsOldFrames()
        {
            var b = new FrameBudgetService(10);
            FrameBudgetService.Feature f = b.Register("x", BudgetPriority.Integrity, 9);
            b.Begin(f);
            b.End(f, 9);
            b.EndFrame();
            for (int i = 0; i < FrameBudgetService.Window; i++) b.EndFrame();
            Assert.Equal(0.0, b.GetStats(f).MaxMs);
        }

        [Test]
        public void ClippedFramesAreCountedOncePerFrame()
        {
            var b = new FrameBudgetService(2);
            FrameBudgetService.Feature apply = b.Register("apply", BudgetPriority.Apply, 10);
            FrameBudgetService.Feature f = b.Register("send", BudgetPriority.CriticalSend, 1);
            b.Begin(apply);
            b.End(apply, 2);
            b.Begin(f);
            b.Begin(f);
            b.End(f, 0.25);
            b.EndFrame();
            Assert.Equal(1L, b.GetStats(f).ClippedFrames);
        }

        [Test]
        public void RegisterReturnsTheKnownFeatureAndRejectsBadInput()
        {
            var b = new FrameBudgetService(10);
            FrameBudgetService.Feature a = b.Register("a", BudgetPriority.Integrity, 1);
            Assert.Same(a, b.Register("a", BudgetPriority.Statistics, 3));
            Assert.Same(a, b.Find("a"));
            Assert.Null(b.Find("b"));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => b.Register("c", BudgetPriority.Integrity, 0));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => b.Register("c", BudgetPriority.Integrity, 1, 2));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new FrameBudgetService(double.NaN));
        }

        [Test]
        public void BadMeasurementsAreIgnored()
        {
            var b = new FrameBudgetService(10);
            FrameBudgetService.Feature f = b.Register("a", BudgetPriority.Integrity, 1);
            b.End(f, double.NaN);
            b.End(f, -1);
            b.End(f, double.PositiveInfinity);
            Assert.Equal(0.0, b.SpentMs);
        }

        [Test]
        public void DescribeListsEveryFeatureAndResetForgetsMeasurements()
        {
            var b = new FrameBudgetService(10);
            FrameBudgetService.Feature f = b.Register("integrity", BudgetPriority.Integrity, 1);
            b.Begin(f);
            b.End(f, 0.5);
            b.EndFrame();
            var lines = new List<string>();
            b.Describe(lines);
            Assert.Equal(2, lines.Count);
            Assert.Contains("frame budget: 10.0 ms shared", lines[0]);
            Assert.Contains("budget integrity (integrity, cap 1.00 ms): avg 0.500 ms, p95 0.50 ms, max 0.50 ms", lines[1]);
            b.Reset();
            Assert.Equal(0L, b.Frames);
            Assert.Equal(0.0, b.GetStats(f).MaxMs);
            Assert.Same(f, b.Find("integrity"), "registrations survive a reset");
        }

        [Test]
        public void Percentile95UsesTheNearestRank()
        {
            Assert.Equal(0, FrameBudgetService.Percentile95Index(1));
            Assert.Equal(18, FrameBudgetService.Percentile95Index(20));
            Assert.Equal(94, FrameBudgetService.Percentile95Index(100));
            Assert.Equal(284, FrameBudgetService.Percentile95Index(300));
        }
    }
}
