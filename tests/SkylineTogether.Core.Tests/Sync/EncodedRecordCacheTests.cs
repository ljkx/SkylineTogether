using System;
using System.Collections.Generic;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>Records encoded once and appended to several batches (<see cref="EncodedRecordCache"/>).</summary>
    public sealed class EncodedRecordCacheTests
    {
        private static int Writes;

        /// <summary>Stands in for EntityCodec.WriteEntity: an entity record followed by its enabled bits.</summary>
        private static void WriteEntity(SyncBatch.Writer w, uint id, SyncRecordKind kind)
        {
            Writes++;
            w.BeginEntity(kind, id, Family.Route, kind != SyncRecordKind.Values);
            var data = new byte[16];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(id + i);
            w.AddComponent(0xABCDUL, SyncComponentKind.Data, data, 0, data.Length);
            if (kind != SyncRecordKind.Values) w.AddComponent(0x1234UL, SyncComponentKind.Presence, null, 0, 0);
            w.EndEntity();
            w.EnabledBits(id, new[] { new SyncEnabledBit(0x55UL, id % 2 == 0) });
        }

        private static void WriteCached(EncodedRecordCache cache, SyncBatch.Writer w, uint id, SyncRecordKind kind)
        {
            ulong key = EncodedRecordCache.Key(id, kind);
            if (cache.TryAppend(key, w)) return;
            int length = w.Length, records = w.RecordCount;
            WriteEntity(w, id, kind);
            cache.Store(key, w, length, records);
        }

        private static byte[] Bytes(SyncBatch.Writer w)
        {
            ArraySegment<byte> s = w.Finish();
            var b = new byte[s.Count];
            Buffer.BlockCopy(s.Array, s.Offset, b, 0, s.Count);
            return b;
        }

        [Test]
        public void EveryPeerGetsTheSameBytesAndTheEntityIsEncodedOnce()
        {
            var cache = new EncodedRecordCache();
            Writes = 0;
            var peers = new List<SyncBatch.Writer>();
            for (int p = 0; p < 4; p++)
            {
                var w = new SyncBatch.Writer();
                w.Prefab(900 + (uint)p, "per batch", new byte[] { 1 }, 0, 1); // written per batch, never cached
                for (uint id = 1; id <= 20; id++) WriteCached(cache, w, id, id % 3 == 0 ? SyncRecordKind.Upsert : SyncRecordKind.Values);
                peers.Add(w);
            }
            Assert.Equal(20, Writes, "each entity is encoded once for four peers");
            Assert.Equal(60L, cache.Hits);
            // byte-identical to writing every batch from scratch
            for (int p = 0; p < 4; p++)
            {
                var direct = new SyncBatch.Writer();
                direct.Prefab(900 + (uint)p, "per batch", new byte[] { 1 }, 0, 1);
                for (uint id = 1; id <= 20; id++) WriteEntity(direct, id, id % 3 == 0 ? SyncRecordKind.Upsert : SyncRecordKind.Values);
                Assert.SequenceEqual(Bytes(direct), Bytes(peers[p]), "batch of peer " + p);
            }
            byte[] any = Bytes(peers[2]);
            List<SyncRecord> records = SyncBatch.Read(any, 0, any.Length);
            Assert.Equal(41, records.Count, "a prefab plus an entity and an enabled-bits record per entity");
        }

        [Test]
        public void KindsAreKeptApartAndClearForgets()
        {
            var cache = new EncodedRecordCache();
            Writes = 0;
            var w = new SyncBatch.Writer();
            WriteCached(cache, w, 7, SyncRecordKind.Values);
            WriteCached(cache, w, 7, SyncRecordKind.Upsert);
            Assert.Equal(2, Writes, "a values record never stands in for an upsert");
            WriteCached(cache, w, 7, SyncRecordKind.Upsert);
            Assert.Equal(2, Writes);
            cache.Clear();
            Assert.Equal(0, cache.Count);
            WriteCached(cache, w, 7, SyncRecordKind.Upsert);
            Assert.Equal(3, Writes, "after a clear the entity is encoded again");
        }

        [Test]
        public void ServingFromTheCacheAllocatesNothing()
        {
            var cache = new EncodedRecordCache();
            var w = new SyncBatch.Writer();
            for (uint id = 1; id <= 64; id++) WriteCached(cache, w, id, SyncRecordKind.Values);
            for (int warm = 0; warm < 2; warm++)
            {
                w.Reset();
                for (uint id = 1; id <= 64; id++) cache.TryAppend(EncodedRecordCache.Key(id, SyncRecordKind.Values), w);
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int round = 0; round < 20; round++)
            {
                w.Reset();
                for (uint id = 1; id <= 64; id++) cache.TryAppend(EncodedRecordCache.Key(id, SyncRecordKind.Values), w);
            }
            Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        [Test]
        public void AWriteThatProducedNothingIsNotStored()
        {
            var cache = new EncodedRecordCache();
            var w = new SyncBatch.Writer();
            cache.Store(EncodedRecordCache.Key(3, SyncRecordKind.Values), w, w.Length, w.RecordCount);
            Assert.Equal(0, cache.Count);
            Assert.False(cache.TryAppend(EncodedRecordCache.Key(3, SyncRecordKind.Values), w));
        }
    }
}
