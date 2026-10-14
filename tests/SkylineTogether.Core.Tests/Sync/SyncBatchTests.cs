using System;
using System.Collections.Generic;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    public sealed class SyncBatchTests
    {
        [Test]
        public void EveryRecordKindRoundTrips()
        {
            var w = new SyncBatch.Writer();
            w.BeginEntity(SyncRecordKind.Spawn, 77, Family.NetEdge, true);
            w.AddComponent(0x1122334455667788UL, SyncComponentKind.Data, new byte[] { 1, 2, 3 }, 0, 3);
            w.AddComponent(0xAAUL, SyncComponentKind.Presence, null, 0, 0);
            w.AddComponent(0xBBUL, SyncComponentKind.Buffer, new byte[] { 9, 8, 7, 6 }, 1, 2);
            w.EndEntity();
            w.BeginEntity(SyncRecordKind.Values, 5, Family.Route, false);
            w.EndEntity();
            w.Despawn(12);
            w.Prefab(3, "Hospital01", new byte[] { 5, 6, 7 }, 0, 3);
            w.ZoneCells(new ZoneBlockKey { OwnerNetId = 9, X = -100, Z = 64, DirX = 1024, DirZ = 0, SizeX = 4, SizeY = 6 }, new uint[] { 0, 0, 0, 7, 7, 8 });
            w.IdRange(1000, 4096);
            w.Cost(-12345);
            w.Clock(123456);
            w.Request(SyncRequest.ResendRoutes);
            ArraySegment<byte> bytes = w.Finish();
            var copy = new byte[bytes.Count + 10];
            Buffer.BlockCopy(bytes.Array, 0, copy, 10, bytes.Count);

            List<SyncRecord> recs = SyncBatch.Read(copy, 10, bytes.Count);
            Assert.Equal(9, recs.Count);
            Assert.Equal(SyncRequest.ResendRoutes, recs[8].Request);
            SyncRecord spawn = recs[0];
            Assert.Equal(SyncRecordKind.Spawn, spawn.Kind);
            Assert.Equal(77u, spawn.NetId);
            Assert.Equal(Family.NetEdge, spawn.Family);
            Assert.True(spawn.CompleteSet);
            Assert.Equal(3, spawn.Components.Count);
            Assert.Equal(0x1122334455667788UL, spawn.Components[0].TypeHash);
            Assert.SequenceEqual(new byte[] { 1, 2, 3 }, Slice(spawn.Components[0]));
            Assert.Equal(SyncComponentKind.Presence, spawn.Components[1].Kind);
            Assert.SequenceEqual(new byte[] { 8, 7 }, Slice(spawn.Components[2]));
            Assert.Equal(SyncRecordKind.Values, recs[1].Kind);
            Assert.False(recs[1].CompleteSet);
            Assert.Equal(12u, recs[2].NetId);
            Assert.Equal("Hospital01", recs[3].PrefabName);
            Assert.SequenceEqual(new byte[] { 5, 6, 7 }, recs[3].PrefabData);
            Assert.SequenceEqual(new uint[] { 0, 0, 0, 7, 7, 8 }, recs[4].ZoneIds);
            Assert.Equal(-100, recs[4].Block.X);
            Assert.Equal(4096u, recs[5].RangeCount);
            Assert.Equal(-12345L, recs[6].Amount);
            Assert.Equal(123456u, recs[7].Frame);
        }

        [Test]
        public void MalformedInputIsADecodeException()
        {
            var w = new SyncBatch.Writer();
            w.Despawn(1);
            ArraySegment<byte> b = w.Finish();
            byte[] bad = new byte[b.Count];
            Buffer.BlockCopy(b.Array, 0, bad, 0, b.Count);
            bad[1] = 5; // claims 5 records
            Assert.Throws<DecodeException>(() => SyncBatch.Read(bad, 0, bad.Length));
            Assert.Throws<DecodeException>(() => SyncBatch.Read(new byte[] { 2, 0, 0, 0, 0 }, 0, 5));
            Assert.Throws<DecodeException>(() => SyncBatch.Read(new byte[] { 1, 1, 0, 0, 0, 99 }, 0, 6));
        }

        [Test]
        public void ResetStartsAnEmptyBatch()
        {
            var w = new SyncBatch.Writer();
            w.Despawn(1);
            w.Reset();
            Assert.True(w.IsEmpty);
            ArraySegment<byte> b = w.Finish();
            Assert.Equal(0, SyncBatch.Read(b.Array, 0, b.Count).Count);
        }

        [Test]
        public void HostGrantsDisjointRangesAndClientsTakeFromThem()
        {
            var host = new NetIdAllocator();
            Assert.Equal(1u, host.Allocate());
            var runs = new List<(uint Start, uint Count)>();
            host.Grant(10, runs);
            host.Grant(5, runs);
            Assert.Equal(2, runs.Count);
            (uint Start, uint Count) g1 = runs[0], g2 = runs[1];
            Assert.Equal(2u, g1.Start);
            Assert.Equal(12u, g2.Start);
            Assert.Equal(17u, host.Allocate());
            var pool = new ClientIdPool();
            Assert.False(pool.TryTake(out _));
            pool.Add(g1.Start, g1.Count);
            pool.Add(g2.Start, g2.Count);
            var taken = new List<uint>();
            while (pool.TryTake(out uint id)) taken.Add(id);
            Assert.Equal(15, taken.Count);
            Assert.Equal(2u, taken[0]);
            Assert.Equal(16u, taken[14]);
            Assert.True(ClientIdPool.InRanges(new[] { g1, g2 }, 13));
            Assert.False(ClientIdPool.InRanges(new[] { g1, g2 }, 17));
            Assert.True(host.Claim(500));
            Assert.Equal(18u, host.Allocate(), "ids skipped by a claim are free (reused lowest first)");
        }

        [Test]
        public void PoolAsksForMoreOnceWhenLow()
        {
            var pool = new ClientIdPool();
            Assert.True(pool.ShouldRequestMore());
            Assert.False(pool.ShouldRequestMore(), "only once until an answer arrives");
            pool.Add(1, 5000);
            Assert.False(pool.ShouldRequestMore());
        }

        private static byte[] Slice(SyncComponent c)
        {
            var b = new byte[c.Count];
            Buffer.BlockCopy(c.Data, c.Offset, b, 0, c.Count);
            return b;
        }
    }
}
