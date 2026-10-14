using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>Decoding into reusable records (<see cref="SyncRecordPool"/>): same results, no steady-state allocations.</summary>
    public sealed class SyncRecordPoolTests
    {
        private static byte[] EntityBatch(int entities, int components, uint firstId)
        {
            var w = new SyncBatch.Writer();
            var data = new byte[24];
            for (int i = 0; i < entities; i++)
            {
                w.BeginEntity(i % 3 == 0 ? SyncRecordKind.Spawn : SyncRecordKind.Upsert, firstId + (uint)i, Family.Object, true);
                for (int c = 0; c < components; c++)
                {
                    data[0] = (byte)c;
                    w.AddComponent(0x1000UL + (ulong)c, c % 4 == 3 ? SyncComponentKind.Presence : SyncComponentKind.Data, data, 0, c % 4 == 3 ? 0 : 8 + c % 5);
                }
                w.EndEntity();
                if (i % 5 == 0) w.Despawn(firstId + 100000 + (uint)i);
            }
            w.Clock(77);
            w.Epoch(9);
            ArraySegment<byte> seg = w.Finish();
            var bytes = new byte[seg.Count];
            Buffer.BlockCopy(seg.Array, seg.Offset, bytes, 0, seg.Count);
            return bytes;
        }

        private static void AssertSame(List<SyncRecord> expected, List<SyncRecord> actual)
        {
            Assert.Equal(expected.Count, actual.Count, "record count");
            for (int i = 0; i < expected.Count; i++)
            {
                SyncRecord e = expected[i], a = actual[i];
                Assert.Equal(e.Kind, a.Kind, "kind of record " + i);
                Assert.Equal(e.NetId, a.NetId, "id of record " + i);
                Assert.Equal(e.Family, a.Family, "family of record " + i);
                Assert.Equal(e.CompleteSet, a.CompleteSet, "complete set of record " + i);
                Assert.Equal(e.Frame, a.Frame, "frame of record " + i);
                Assert.Equal(e.EpochNumber, a.EpochNumber, "epoch of record " + i);
                Assert.Equal(e.Components.Count, a.Components.Count, "components of record " + i);
                for (int c = 0; c < e.Components.Count; c++)
                {
                    Assert.Equal(e.Components[c].TypeHash, a.Components[c].TypeHash);
                    Assert.Equal(e.Components[c].Kind, a.Components[c].Kind);
                    Assert.Equal(e.Components[c].Offset, a.Components[c].Offset);
                    Assert.Equal(e.Components[c].Count, a.Components[c].Count);
                }
            }
        }

        [Test]
        public void PooledDecodesMatchFreshDecodesAcrossDifferentBatches()
        {
            var pool = new SyncRecordPool();
            byte[] big = EntityBatch(40, 12, 1);
            byte[] small = EntityBatch(3, 2, 500);
            AssertSame(SyncBatch.Read(big, 0, big.Length), SyncBatch.Read(big, 0, big.Length, pool));
            // a smaller batch after a bigger one: reused records carry nothing of their previous use
            AssertSame(SyncBatch.Read(small, 0, small.Length), SyncBatch.Read(small, 0, small.Length, pool));
            AssertSame(SyncBatch.Read(big, 0, big.Length), SyncBatch.Read(big, 0, big.Length, pool));
        }

        [Test]
        public void AReusedRecordForgetsTheFieldsOfItsPreviousKind()
        {
            var pool = new SyncRecordPool();
            var w = new SyncBatch.Writer();
            w.Global(7, new byte[] { 1, 2, 3 }, 0, 3);
            w.RequestIds(SyncRequest.BucketDetail, new uint[] { 4, 5 });
            ArraySegment<byte> first = w.Finish();
            List<SyncRecord> a = SyncBatch.Read(first.Array, first.Offset, first.Count, pool);
            Assert.NotNull(a[0].Payload);
            Assert.NotNull(a[1].Ids);
            var w2 = new SyncBatch.Writer();
            w2.Despawn(9);
            w2.Clock(3);
            ArraySegment<byte> second = w2.Finish();
            List<SyncRecord> b = SyncBatch.Read(second.Array, second.Offset, second.Count, pool);
            Assert.Equal(SyncRecordKind.Despawn, b[0].Kind);
            Assert.Null(b[0].Payload, "a despawn has no payload");
            Assert.Equal(0u, b[0].Channel);
            Assert.Null(b[1].Ids, "a clock record has no ids");
            Assert.Equal(3u, b[1].Frame);
            Assert.Equal(2, pool.Created, "the second decode reused the first decode's records");
        }

        [Test]
        public void SteadyStateDecodingOfEntityBatchesAllocatesNothing()
        {
            var pool = new SyncRecordPool();
            byte[] batch = EntityBatch(200, 16, 1);
            for (int warm = 0; warm < 3; warm++) SyncBatch.Read(batch, 0, batch.Length, pool);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 50; i++) SyncBatch.Read(batch, 0, batch.Length, pool);
            Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before, "pooled decodes allocate nothing");
            // the fresh decode for comparison: several allocations per record
            long fresh = GC.GetAllocatedBytesForCurrentThread();
            SyncBatch.Read(batch, 0, batch.Length);
            long freshBytes = GC.GetAllocatedBytesForCurrentThread() - fresh;
            Assert.True(freshBytes > 200 * 64, "a fresh decode allocates per record (" + freshBytes + " bytes)");
        }

        [Test]
        public void SteadyStateDecodingOfValueMessagesAllocatesNothing()
        {
            var w = new ByteWriter(4096);
            int count = SimValueMessage.BeginValues(w);
            var data = new byte[12];
            for (int i = 0; i < 64; i++)
            {
                int pos = SimValueMessage.BeginEntity(w, (uint)(i + 1), Family.Agent);
                for (int c = 0; c < 6; c++) SimValueMessage.AddComponent(w, 0x2000UL + (ulong)c, SyncComponentKind.Data, data, 0, 4 + c);
                SimValueMessage.EndEntity(w, pos, 6);
            }
            SimValueMessage.End(w, count, 64);
            byte[] bytes = new byte[w.Length];
            Buffer.BlockCopy(w.Buffer, 0, bytes, 0, w.Length);
            var pool = new SyncRecordPool();
            List<SyncRecord> expected = SimValueMessage.Read(bytes, 0, bytes.Length, out _, out _, out _);
            AssertSame(expected, SimValueMessage.Read(bytes, 0, bytes.Length, pool, out _, out _, out _));
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 50; i++) SimValueMessage.Read(bytes, 0, bytes.Length, pool, out _, out _, out _);
            Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before, "pooled decodes allocate nothing");
        }

        [Test]
        public void AMalformedBatchLeavesThePoolUsable()
        {
            var pool = new SyncRecordPool();
            byte[] good = EntityBatch(10, 4, 1);
            byte[] bad = (byte[])good.Clone();
            Array.Resize(ref bad, bad.Length - 3);
            Assert.Throws<DecodeException>(() => SyncBatch.Read(bad, 0, bad.Length, pool));
            AssertSame(SyncBatch.Read(good, 0, good.Length), SyncBatch.Read(good, 0, good.Length, pool));
        }
    }
}
