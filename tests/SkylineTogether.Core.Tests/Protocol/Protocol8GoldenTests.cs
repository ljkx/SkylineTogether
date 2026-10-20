using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Protocol
{
    /// <summary>
    /// Golden byte vectors of the layouts protocol 8 changed (ProtocolVersion history): the load heartbeat of
    /// <c>LoadProgress</c>, the generation byte of mover records, and a statistics append that carries the newest sample
    /// sent again. Written by hand from the documented layouts, checked against the writers and decoded back.
    /// </summary>
    public sealed class Protocol8GoldenTests
    {
        private static byte[] Bytes(ByteWriter w)
        {
            var b = new byte[w.Length];
            Array.Copy(w.Buffer, b, w.Length);
            return b;
        }

        [Test]
        public void LoadProgressCarriesTheHeartbeat()
        {
            var w = new ByteWriter();
            SessionPayloads.WriteLoadProgress(w, JoinPhase.Loading, 0.25f, 300);
            byte[] expected =
            {
                0x00, 0x00, 0x80, 0x3E, // f32 0.25
                (byte)JoinPhase.Loading, // u8 phase
                0x2C, 0x01, 0x00, 0x00, // u32 heartbeat 300
            };
            Assert.SequenceEqual(expected, Bytes(w));
            var r = new ByteReader(expected, 0, expected.Length);
            Assert.Equal(JoinPhase.Loading, SessionPayloads.ReadLoadProgress(r, out float f, out uint beat));
            Assert.Near(0.25, f, 1e-9);
            Assert.Equal(300u, beat);
        }

        [Test]
        public void AMoverRecordCarriesTheGenerationAfterItsId()
        {
            var plain = new SyncBatch.Writer();
            plain.Mover(SyncRecordKind.MoverSpawn, 5, new MoverRecord { PrefabNetId = 2 }, 0);
            var withGeneration = new SyncBatch.Writer();
            withGeneration.Mover(SyncRecordKind.MoverSpawn, 5, new MoverRecord { PrefabNetId = 2 }, 9);
            ArraySegment<byte> a = plain.Finish(), b = withGeneration.Finish();
            Assert.Equal(a.Count, b.Count, "one byte either way");
            // after the batch header: u8 kind | varuint netId | u8 generation | the mover record
            Assert.Equal((byte)SyncRecordKind.MoverSpawn, b.Array[b.Offset + 5]);
            Assert.Equal((byte)5, b.Array[b.Offset + 6]);
            Assert.Equal((byte)0, a.Array[a.Offset + 7]);
            Assert.Equal((byte)9, b.Array[b.Offset + 7]);
            for (int i = 8; i < a.Count; i++) Assert.Equal(a.Array[a.Offset + i], b.Array[b.Offset + i], "byte " + i);
            List<SyncRecord> read = SyncBatch.Read(b.Array, b.Offset, b.Count);
            Assert.Equal((byte)9, read[0].Generation);
        }

        [Test]
        public void AStatisticsAppendStartsWithTheNewestSampleSent()
        {
            // the host's series held 3 samples when last sent; the newest changed in place and one was appended: the append
            // ends at host length 4 and carries 2 samples (host samples 2 and 3)
            var sent = new StatisticSeries { Type = 2, Parameter = 0, Values = new[] { 1.0, 2.0, 3.0 }, Totals = new[] { 1.0, 2.0, 3.0 }, TotalLength = 3 };
            StatisticsDelta.Plan(default(StatisticSentState), sent, out StatisticSentState state);
            var now = new StatisticSeries { Type = 2, Parameter = 0, Values = new[] { 1.0, 2.0, 3.5, 4.0 }, Totals = new[] { 1.0, 2.0, 3.5, 4.0 }, TotalLength = 4 };
            StatisticSeries append = StatisticsDelta.Plan(state, now, out _);
            var w = new ByteWriter();
            StatisticsMessage.WriteMixed(w, 4, 0x10, new[] { append });
            byte[] expected =
            {
                StatisticsMessage.Version, StatisticsMessage.TypeMixed, 4, 0, 0, 0, 0x10, 0, 0, 0, 1,
                1, 4, 0, 4, 2, // append, type 2 (zigzag 4), parameter 0, total 4, two samples
                0, 0, 0, 0, 0, 0, 0x0C, 0x40, 0, 0, 0, 0, 0, 0, 0x0C, 0x40, // 3.5, 3.5
                0, 0, 0, 0, 0, 0, 0x10, 0x40, 0, 0, 0, 0, 0, 0, 0x10, 0x40, // 4.0, 4.0
            };
            Assert.SequenceEqual(expected, Bytes(w));
        }
    }
}
