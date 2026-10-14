using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>The sim-values and statistics message codecs.</summary>
    public sealed class SimValuesStatisticsTests
    {
        private static byte[] Shifted(ByteWriter w, int shift)
        {
            var copy = new byte[w.Length + shift];
            Buffer.BlockCopy(w.Buffer, 0, copy, shift, w.Length);
            return copy;
        }

        [Test]
        public void ValuesMessageRoundTripsWithAbsoluteOffsets()
        {
            var w = new ByteWriter();
            int count = SimValueMessage.BeginValues(w);
            int e1 = SimValueMessage.BeginEntity(w, 77, Family.Object);
            SimValueMessage.AddComponent(w, 0x1122334455667788UL, SyncComponentKind.Data, new byte[] { 0, 1, 2, 3 }, 1, 3);
            SimValueMessage.AddComponent(w, 0x99UL, SyncComponentKind.Buffer, new byte[0], 0, 0);
            SimValueMessage.EndEntity(w, e1, 2);
            int e2 = SimValueMessage.BeginEntity(w, 300000, Family.NetEdge);
            SimValueMessage.EndEntity(w, e2, 0);
            SimValueMessage.End(w, count, 2);
            byte[] data = Shifted(w, 7);

            List<SyncRecord> recs = SimValueMessage.Read(data, 7, w.Length, out byte type, out uint tick, out uint selected);
            Assert.Equal(SimValueMessage.TypeValues, type);
            Assert.Equal(0u, tick);
            Assert.Equal(0u, selected);
            Assert.Equal(2, recs.Count);
            Assert.Equal(SyncRecordKind.Values, recs[0].Kind);
            Assert.Equal(77u, recs[0].NetId);
            Assert.Equal(Family.Object, recs[0].Family);
            Assert.Equal(2, recs[0].Components.Count);
            SyncComponent c = recs[0].Components[0];
            Assert.Equal(0x1122334455667788UL, c.TypeHash);
            Assert.Equal(SyncComponentKind.Data, c.Kind);
            Assert.Equal(3, c.Count);
            Assert.Same(data, c.Data);
            Assert.Equal((byte)1, data[c.Offset]);
            Assert.Equal((byte)3, data[c.Offset + 2]);
            Assert.Equal(SyncComponentKind.Buffer, recs[0].Components[1].Kind);
            Assert.Equal(0, recs[0].Components[1].Count);
            Assert.Equal(300000u, recs[1].NetId);
            Assert.Equal(0, recs[1].Components.Count);
        }

        [Test]
        public void SampleMessageCarriesTickAndSelection()
        {
            var w = new ByteWriter();
            int count = SimValueMessage.BeginSample(w, 0xFFFFFF00u, 42);
            int e = SimValueMessage.BeginEntity(w, 42, Family.Mover);
            SimValueMessage.AddComponent(w, 5, SyncComponentKind.Data, new byte[] { 9 }, 0, 1);
            SimValueMessage.EndEntity(w, e, 1);
            SimValueMessage.End(w, count, 1);
            List<SyncRecord> recs = SimValueMessage.Read(w.Buffer, 0, w.Length, out byte type, out uint tick, out uint selected);
            Assert.Equal(SimValueMessage.TypeSample, type);
            Assert.Equal(0xFFFFFF00u, tick);
            Assert.Equal(42u, selected);
            Assert.Equal(1, recs.Count);
        }

        [Test]
        public void DroppedEntityLeavesAValidMessage()
        {
            var w = new ByteWriter();
            int count = SimValueMessage.BeginValues(w);
            int start = w.Length;
            int e = SimValueMessage.BeginEntity(w, 1, Family.Object);
            SimValueMessage.AddComponent(w, 5, SyncComponentKind.Data, new byte[] { 9 }, 0, 1);
            SimValueMessage.DropEntity(w, start);
            SimValueMessage.End(w, count, 0);
            Assert.Equal(0, SimValueMessage.Read(w.Buffer, 0, w.Length, out _, out _, out _).Count);
            Assert.True(e > start);
        }

        [Test]
        public void PresenceIsRejectedByTheWriter()
        {
            var w = new ByteWriter();
            Assert.Throws<ArgumentException>(() => SimValueMessage.AddComponent(w, 1, SyncComponentKind.Presence, null, 0, 0));
        }

        [Test]
        public void MalformedValuesMessagesThrowDecodeException()
        {
            var w = new ByteWriter();
            int count = SimValueMessage.BeginValues(w);
            int e = SimValueMessage.BeginEntity(w, 1, Family.Object);
            SimValueMessage.AddComponent(w, 5, SyncComponentKind.Data, new byte[] { 1, 2, 3, 4, 5, 6 }, 0, 6);
            SimValueMessage.EndEntity(w, e, 1);
            SimValueMessage.End(w, count, 1);
            byte[] good = w.ToArray();
            for (int cut = 0; cut < good.Length; cut++)
            {
                int length = cut;
                Assert.Throws<DecodeException>(() => SimValueMessage.Read(good, 0, length, out _, out _, out _), "truncated at " + cut);
            }
            var badVersion = (byte[])good.Clone();
            badVersion[0] = 99;
            Assert.Throws<DecodeException>(() => SimValueMessage.Read(badVersion, 0, badVersion.Length, out _, out _, out _));
            var badType = (byte[])good.Clone();
            badType[1] = 7;
            Assert.Throws<DecodeException>(() => SimValueMessage.Read(badType, 0, badType.Length, out _, out _, out _));
            var trailing = new byte[good.Length + 1];
            Buffer.BlockCopy(good, 0, trailing, 0, good.Length);
            Assert.Throws<DecodeException>(() => SimValueMessage.Read(trailing, 0, trailing.Length, out _, out _, out _));
            var huge = (byte[])good.Clone();
            huge[2] = 0xFF;
            huge[3] = 0xFF;
            huge[4] = 0xFF;
            huge[5] = 0x7F;
            Assert.Throws<DecodeException>(() => SimValueMessage.Read(huge, 0, huge.Length, out _, out _, out _));
        }

        [Test]
        public void RandomGarbageNeverThrowsAnythingButDecodeException()
        {
            var random = new DeterministicRandom(7001);
            for (int round = 0; round < 2000; round++)
            {
                var data = new byte[random.Next(64)];
                random.NextBytes(data, 0, data.Length);
                if (data.Length > 0) data[0] = SimValueMessage.Version;
                if (data.Length > 1) data[1] = (byte)(1 + random.Next(2));
                try
                {
                    SimValueMessage.Read(data, 0, data.Length, out _, out _, out _);
                }
                catch (DecodeException)
                {
                }
                try
                {
                    StatisticsMessage.Read(data, 0, data.Length, out _, out _);
                }
                catch (DecodeException)
                {
                }
            }
        }

        [Test]
        public void StatisticsRoundTrip()
        {
            var series = new List<StatisticSeries>
            {
                new StatisticSeries { Type = 3, Parameter = 0, Values = new[] { 1.5, -2.0, double.MaxValue }, Totals = new[] { 10.0, 20.0, 30.0 } },
                new StatisticSeries { Type = 17, Parameter = -4 },
            };
            var w = new ByteWriter();
            StatisticsMessage.Write(w, 33, 0xABCDEF01u, series);
            List<StatisticSeries> back = StatisticsMessage.Read(w.Buffer, 0, w.Length, out int samples, out uint last);
            Assert.Equal(33, samples);
            Assert.Equal(0xABCDEF01u, last);
            Assert.Equal(2, back.Count);
            Assert.Equal(3, back[0].Type);
            Assert.SequenceEqual(series[0].Values, back[0].Values);
            Assert.SequenceEqual(series[0].Totals, back[0].Totals);
            Assert.Equal(-4, back[1].Parameter);
            Assert.Equal(0, back[1].Values.Length);
            Assert.Equal(series[0].ContentHash(), back[0].ContentHash());
            Assert.NotEqual(series[0].ContentHash(), series[1].ContentHash());
        }

        [Test]
        public void StatisticsHashSeesEveryChange()
        {
            var a = new StatisticSeries { Type = 1, Parameter = 2, Values = new[] { 1.0, 2.0 }, Totals = new[] { 3.0, 4.0 } };
            ulong h = a.ContentHash();
            a.Totals[1] = 4.0000001;
            Assert.NotEqual(h, a.ContentHash());
            var b = new StatisticSeries { Type = 1, Parameter = 3, Values = new[] { 1.0, 2.0 }, Totals = new[] { 3.0, 4.0 } };
            Assert.NotEqual(new StatisticSeries { Type = 1, Parameter = 2, Values = new[] { 1.0, 2.0 }, Totals = new[] { 3.0, 4.0 } }.ContentHash(), b.ContentHash());
        }

        [Test]
        public void MalformedStatisticsThrowDecodeException()
        {
            var w = new ByteWriter();
            StatisticsMessage.Write(w, 1, 2, new List<StatisticSeries> { new StatisticSeries { Type = 1, Values = new[] { 1.0 }, Totals = new[] { 2.0 } } });
            byte[] good = w.ToArray();
            for (int cut = 0; cut < good.Length; cut++)
            {
                int length = cut;
                Assert.Throws<DecodeException>(() => StatisticsMessage.Read(good, 0, length, out _, out _), "truncated at " + cut);
            }
        }
    }
}
