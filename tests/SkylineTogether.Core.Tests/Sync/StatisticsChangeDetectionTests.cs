using System;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>
    /// The host's change detection of a statistics series (<see cref="StatisticsDelta.Plan"/>) at its edges: a change of
    /// only a total (the value unchanged) still travels, a changed older sample sends the series whole, and more samples
    /// appended than the window holds after a one-sample history send it whole instead of failing.
    /// </summary>
    public sealed class StatisticsChangeDetectionTests
    {
        private static StatisticSeries Series(long totalLength, double[] values, double[] totals)
        {
            return new StatisticSeries { Type = 3, Parameter = 1, Values = values, Totals = totals, TotalLength = totalLength };
        }

        private static StatisticSentState Sent(StatisticSeries window)
        {
            Assert.NotNull(StatisticsDelta.Plan(default(StatisticSentState), window, out StatisticSentState sent), "the first message");
            return sent;
        }

        [Test]
        public void AChangeOfOnlyTheNewestTotalIsSentAsAnAppend()
        {
            StatisticSentState sent = Sent(Series(5, new double[] { 1, 2, 3 }, new double[] { 10, 20, 30 }));
            StatisticSeries plan = StatisticsDelta.Plan(sent, Series(5, new double[] { 1, 2, 3 }, new double[] { 10, 20, 31 }), out _);
            Assert.NotNull(plan, "the newest total changed in place");
            Assert.True(plan.Append);
            Assert.SequenceEqual(new double[] { 3 }, plan.Values);
            Assert.SequenceEqual(new double[] { 31 }, plan.Totals);
            Assert.Equal(null, StatisticsDelta.Plan(sent, Series(5, new double[] { 1, 2, 3 }, new double[] { 10, 20, 30 }), out _), "nothing changed");
        }

        [Test]
        public void AChangeOfOnlyAnOlderTotalSendsTheSeriesWhole()
        {
            StatisticSentState sent = Sent(Series(5, new double[] { 1, 2, 3 }, new double[] { 10, 20, 30 }));
            // the sample before the newest one must not change any more (the game changes only the newest in place): when
            // its total did, an append would keep the client's stale total
            StatisticSeries plan = StatisticsDelta.Plan(sent, Series(5, new double[] { 1, 2, 3 }, new double[] { 10, 21, 31 }), out _);
            Assert.NotNull(plan);
            Assert.False(plan.Append, "sent whole");
            Assert.SequenceEqual(new double[] { 10, 21, 31 }, plan.Totals);
            // the same with a sample appended since
            plan = StatisticsDelta.Plan(sent, Series(6, new double[] { 1, 2, 3, 4 }, new double[] { 10, 21, 30, 40 }), out _);
            Assert.False(plan.Append, "sent whole after an append too");
        }

        [Test]
        public void MoreSamplesAppendedThanTheWindowHoldsAfterAOneSampleHistorySendTheSeriesWhole()
        {
            // the last message held a single sample (no previous one to compare)
            StatisticSentState one = Sent(Series(1, new double[] { 7 }, new double[] { 70 }));
            Assert.False(one.HasPrevious);
            foreach (long total in new long[] { 4, 5, 40 })
            {
                // a window of 3 samples, 3 or more appended since (a pause, a slow host)
                StatisticSeries window = Series(total, new double[] { 8, 9, 10 }, new double[] { 80, 90, 100 });
                StatisticSeries plan = null;
                try
                {
                    plan = StatisticsDelta.Plan(one, window, out _);
                }
                catch (Exception e)
                {
                    Assert.Fail("total " + total + ": " + e.GetType().Name + " " + e.Message);
                }
                Assert.NotNull(plan, "total " + total);
                Assert.False(plan.Append, "total " + total + ": sent whole");
                Assert.SequenceEqual(window.Values, plan.Values);
                Assert.Equal(total, plan.TotalLength);
            }
            // and with a two-sample history
            StatisticSentState two = Sent(Series(2, new double[] { 6, 7 }, new double[] { 60, 70 }));
            StatisticSeries whole = StatisticsDelta.Plan(two, Series(5, new double[] { 8, 9, 10 }, new double[] { 80, 90, 100 }), out _);
            Assert.False(whole.Append);
            // fewer appended than the window holds still travel as an append after a one-sample history
            StatisticSeries append = StatisticsDelta.Plan(one, Series(3, new double[] { 7, 8, 9 }, new double[] { 70, 80, 90 }), out _);
            Assert.True(append.Append);
            Assert.SequenceEqual(new double[] { 7, 8, 9 }, append.Values);
        }
    }
}
