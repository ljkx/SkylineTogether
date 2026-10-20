using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>
    /// The game adds events (income, trade, population) into the newest statistics sample in place (R2 S3): those series
    /// travel as the newest sample plus the new ones, never whole again, and every receiver ends with the host's values.
    /// </summary>
    public sealed class StatisticsInPlaceTests
    {
        private static StatisticSeries Window(List<double> host)
        {
            int n = host.Count;
            var s = new StatisticSeries { Type = 1, Parameter = 0, Values = new double[n], Totals = new double[n], TotalLength = n };
            for (int i = 0; i < n; i++)
            {
                s.Values[i] = host[i];
                s.Totals[i] = host[i] * 2;
            }
            return s;
        }

        private static StatisticSeries RoundTrip(StatisticSeries s)
        {
            var w = new ByteWriter();
            StatisticsMessage.WriteMixed(w, 1, 1, new[] { s });
            return StatisticsMessage.Read(w.Buffer, 0, w.Length, out _, out _)[0];
        }

        [Test]
        public void EventsAddedIntoTheNewestSampleNeverSendTheSeriesWhole()
        {
            var host = new List<double>();
            for (int i = 0; i < 40; i++) host.Add(i);
            StatisticSeries first = StatisticsDelta.Plan(default(StatisticSentState), Window(host), out StatisticSentState state);
            double[] v = first.Values, t = first.Totals;
            long copyTotal = first.TotalLength;
            var rng = new Random(5);
            int sentSamples = 0;
            for (int step = 0; step < 200; step++)
            {
                // most steps: events add into the newest sample (the 30 s compare); every tenth a new sample starts
                if (step % 10 == 9) host.Add(0);
                else host[host.Count - 1] += rng.Next(1, 100);
                StatisticSeries plan = StatisticsDelta.Plan(state, Window(host), out state);
                Assert.NotNull(plan, "step " + step);
                Assert.True(plan.Append, "step " + step + ": never the whole series again");
                Assert.True(plan.Values.Length <= 2, "step " + step);
                sentSamples += plan.Values.Length;
                plan = RoundTrip(plan);
                Assert.True(StatisticsDelta.TryAppend(v, t, copyTotal, plan, out v, out t), "step " + step);
                copyTotal = plan.TotalLength;
                Assert.SequenceEqual(Window(host).Values, v, "step " + step);
                Assert.SequenceEqual(Window(host).Totals, t, "step " + step);
            }
            Assert.True(sentSamples < 300, "about one sample per message (" + sentSamples + ")");
        }

        [Test]
        public void AnUnchangedSeriesSendsNothing()
        {
            var host = new List<double> { 1, 2, 3 };
            StatisticsDelta.Plan(default(StatisticSentState), Window(host), out StatisticSentState state);
            Assert.Null(StatisticsDelta.Plan(state, Window(host), out _));
        }

        [Test]
        public void ACopyThatHoldsTheNewestSampleGetsItOverwritten()
        {
            // a joiner's snapshot holds the newest sample as it was; the next message carries its new value
            var copy = new double[] { 1, 2, 3 };
            var append = new StatisticSeries { Append = true, TotalLength = 4, Values = new double[] { 30, 4 }, Totals = new double[] { 60, 8 } };
            Assert.True(StatisticsDelta.TryAppend(copy, copy, 3, append, out double[] v, out double[] t));
            Assert.SequenceEqual(new double[] { 1, 2, 30, 4 }, v);
            Assert.SequenceEqual(new double[] { 1, 2, 60, 8 }, t);
            // only the newest sample changed: same length, overwritten
            append = new StatisticSeries { Append = true, TotalLength = 4, Values = new double[] { 5 }, Totals = new double[] { 10 } };
            Assert.True(StatisticsDelta.TryAppend(v, t, 4, append, out v, out t));
            Assert.SequenceEqual(new double[] { 1, 2, 30, 5 }, v);
            // a copy ahead of the message (a newer snapshot) is left alone
            Assert.True(StatisticsDelta.TryAppend(v, t, 9, append, out double[] same, out _));
            Assert.Same(v, same);
        }
    }
}
