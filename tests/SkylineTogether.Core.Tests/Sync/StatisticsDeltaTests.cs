using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>Statistics travel as appends after the first whole series, and receivers end with the host's window.</summary>
    public sealed class StatisticsDeltaTests
    {
        private static StatisticSeries Window(List<double> host)
        {
            int n = Math.Min(host.Count, StatisticsMessage.MaxSamples);
            var s = new StatisticSeries { Type = 3, Parameter = 7, Values = new double[n], Totals = new double[n], TotalLength = host.Count };
            for (int i = 0; i < n; i++)
            {
                s.Values[i] = host[host.Count - n + i];
                s.Totals[i] = host[host.Count - n + i] * 10;
            }
            return s;
        }

        private static StatisticSeries RoundTrip(StatisticSeries s)
        {
            var w = new ByteWriter();
            StatisticsMessage.WriteMixed(w, 5, 9, new[] { s });
            List<StatisticSeries> read = StatisticsMessage.Read(w.Buffer, 0, w.Length, out int count, out uint frame);
            Assert.Equal(5, count);
            Assert.Equal(9u, frame);
            return read[0];
        }

        [Test]
        public void OnlyAppendedSamplesTravelAndTheCopyEndsEqual()
        {
            var host = new List<double>();
            for (int i = 0; i < 100; i++) host.Add(i);
            var state = default(StatisticSentState);
            StatisticSeries first = StatisticsDelta.Plan(state, Window(host), out state);
            Assert.False(first.Append, "the first message is the whole series");
            Assert.Null(StatisticsDelta.Plan(state, Window(host), out state), "unchanged: nothing");

            double[] copyV = first.Values, copyT = first.Totals;
            long copyTotal = first.TotalLength;
            for (int sample = 0; sample < 3; sample++)
            {
                host.Add(1000 + sample);
                StatisticSeries plan = RoundTrip(StatisticsDelta.Plan(state, Window(host), out state));
                Assert.True(plan.Append);
                Assert.Equal(2, plan.Values.Length, "the newest sample sent and the new one, not the history");
                Assert.True(StatisticsDelta.TryAppend(copyV, copyT, copyTotal, plan, out copyV, out copyT));
                copyTotal = plan.TotalLength;
            }
            StatisticSeries expected = Window(host);
            Assert.SequenceEqual(expected.Values, copyV);
            Assert.SequenceEqual(expected.Totals, copyT);
        }

        [Test]
        public void AJoinersLongerCopyAndARepeatedAppendAreHandled()
        {
            var host = new List<double>();
            for (int i = 0; i < 50; i++) host.Add(i);
            StatisticsDelta.Plan(default(StatisticSentState), Window(host), out StatisticSentState state);
            // the joiner's snapshot was taken after one more sample (its copy already holds it)
            host.Add(50);
            double[] snapV = Window(host).Values, snapT = Window(host).Totals;
            StatisticSeries append = StatisticsDelta.Plan(state, Window(host), out state);
            Assert.True(StatisticsDelta.TryAppend(snapV, snapT, host.Count, append, out double[] v, out double[] t));
            Assert.Equal(51, v.Length, "the sample it already had is not appended twice");
            host.Add(51);
            append = StatisticsDelta.Plan(state, Window(host), out _);
            Assert.True(StatisticsDelta.TryAppend(v, t, 51, append, out v, out _));
            Assert.Equal(51.0, v[v.Length - 1]);
            Assert.Equal(52, v.Length);
            // a gap (the copy ends before the append starts) is refused
            Assert.False(StatisticsDelta.TryAppend(new double[3], new double[3], 3, append, out _, out _));
        }

        [Test]
        public void AnAppendOlderThanTheCopyDoesNotMoveItsTotalBack()
        {
            // R3 T1: an append (total 99) queued from admission arrives after the joiner's snapshot, whose copy holds 100
            var host = new List<double>();
            for (int i = 0; i < 99; i++) host.Add(i);
            StatisticsDelta.Plan(default(StatisticSentState), Window(host), out StatisticSentState state);
            host.Add(99);
            double[] v = Window(host).Values, t = Window(host).Totals;
            long copyTotal = host.Count; // 100
            var old = new StatisticSeries { Type = 3, Parameter = 7, Append = true, TotalLength = 99, Values = new double[] { 98 }, Totals = new double[] { 980 } };
            Assert.True(StatisticsDelta.TryAppend(v, t, copyTotal, old, out v, out t), "the copy is newer: left alone");
            copyTotal = StatisticsDelta.CopyTotalAfter(copyTotal, old);
            Assert.Equal(100L, copyTotal, "not moved back to 99");
            // the next append resends sample 99 (maybe changed) and adds 100: sample 99 is overwritten, not duplicated
            host[99] = 99.5;
            host.Add(100);
            StatisticSeries next = StatisticsDelta.Plan(state, Window(host), out _);
            Assert.True(next.Append);
            Assert.True(StatisticsDelta.TryAppend(v, t, copyTotal, next, out v, out _));
            copyTotal = StatisticsDelta.CopyTotalAfter(copyTotal, next);
            Assert.Equal(101L, copyTotal);
            Assert.SequenceEqual(Window(host).Values, v);
            // a whole series sets the total, also backwards
            Assert.Equal(40L, StatisticsDelta.CopyTotalAfter(101, new StatisticSeries { TotalLength = 40, Values = new double[0], Totals = new double[0] }));
        }

        [Test]
        public void AHistoryThatDoesNotContinueIsSentWhole()
        {
            var host = new List<double> { 1, 2, 3 };
            StatisticsDelta.Plan(default(StatisticSentState), Window(host), out StatisticSentState state);
            host[1] = 99; // a sample before the newest one sent changed
            host.Add(4);
            Assert.False(StatisticsDelta.Plan(state, Window(host), out state).Append);
            host.RemoveAt(0); // shorter
            host.RemoveAt(0);
            Assert.False(StatisticsDelta.Plan(state, Window(host), out _).Append);
        }

        [Test]
        public void TheWindowKeepsItsNewestSamples()
        {
            var copy = new double[StatisticsMessage.MaxSamples];
            for (int i = 0; i < copy.Length; i++) copy[i] = i;
            var append = new StatisticSeries { Append = true, TotalLength = StatisticsMessage.MaxSamples + 2, Values = new double[] { -1, -2 }, Totals = new double[] { -1, -2 } };
            Assert.True(StatisticsDelta.TryAppend(copy, copy, StatisticsMessage.MaxSamples, append, out double[] v, out _));
            Assert.Equal(StatisticsMessage.MaxSamples, v.Length);
            Assert.Equal(2.0, v[0]);
            Assert.Equal(-2.0, v[v.Length - 1]);
        }

        [Test]
        public void TheMixedLayoutIsPinned()
        {
            var w = new ByteWriter();
            var append = new StatisticSeries { Append = true, Type = 2, Parameter = -1, TotalLength = 300, Values = new[] { 1.0 }, Totals = new[] { 2.0 } };
            StatisticsMessage.WriteMixed(w, 7, 0x01020304u, new[] { append });
            byte[] expected =
            {
                // literals, not StatisticsMessage.Version/TypeMixed: changing either constant is a wire change
                1, 2, 7, 0, 0, 0, 0x04, 0x03, 0x02, 0x01, 1, // version 1, type mixed (2), sampleCount 7, lastSampleFrame, 1 series
                1, 4, 1, 0xAC, 0x02, 1, // append, type 2 (zigzag 4), parameter -1 (zigzag 1), total 300, one sample
                0, 0, 0, 0, 0, 0, 0xF0, 0x3F, 0, 0, 0, 0, 0, 0, 0, 0x40, // 1.0, 2.0
            };
            var actual = new byte[w.Length];
            Array.Copy(w.Buffer, actual, w.Length);
            Assert.SequenceEqual(expected, actual);
        }
    }
}
