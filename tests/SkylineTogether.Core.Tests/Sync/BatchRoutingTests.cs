using System;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>
    /// Integration review: how a received batch finds its consumer (global state and movers own whole batches and are
    /// recognised by the first record alone) and which record kinds a host accepts from a client.
    /// </summary>
    public sealed class BatchRoutingTests
    {
        private static byte[] Bytes(SyncBatch.Writer w, int lead = 0)
        {
            ArraySegment<byte> s = w.Finish();
            var copy = new byte[s.Count + lead];
            Buffer.BlockCopy(s.Array, s.Offset, copy, lead, s.Count);
            return copy;
        }

        [Test]
        public void TheFirstRecordNamesTheBatchOwner()
        {
            var global = new SyncBatch.Writer();
            global.Global(7, new byte[] { 1, 2 }, 0, 2);
            byte[] g = Bytes(global);
            Assert.Equal((byte)SyncRecordKind.Global, SyncBatch.FirstRecordKind(g, 0, g.Length));
            Assert.False(SyncBatch.IsMoverBatch(g, 0, g.Length));

            foreach (SyncRecordKind kind in new[] { SyncRecordKind.MoverSpawn, SyncRecordKind.MoverState })
            {
                var movers = new SyncBatch.Writer();
                movers.Mover(kind, 42, new MoverRecord { PrefabNetId = 3, QW = 1 });
                movers.MoverDespawn(43);
                byte[] m = Bytes(movers, 3);
                Assert.True(SyncBatch.IsMoverBatch(m, 3, m.Length - 3));
                Assert.NotEqual((byte)SyncRecordKind.Global, SyncBatch.FirstRecordKind(m, 3, m.Length - 3));
            }
            var despawnOnly = new SyncBatch.Writer();
            despawnOnly.MoverDespawn(5);
            byte[] d = Bytes(despawnOnly);
            Assert.True(SyncBatch.IsMoverBatch(d, 0, d.Length));

            // an entity batch (structure stream, repairs) belongs to neither
            var entities = new SyncBatch.Writer();
            entities.BeginEntity(SyncRecordKind.Spawn, 9, Family.Object, true);
            entities.EndEntity();
            entities.Global(1, new byte[0], 0, 0);
            byte[] e = Bytes(entities);
            Assert.Equal((byte)SyncRecordKind.Spawn, SyncBatch.FirstRecordKind(e, 0, e.Length));
            Assert.False(SyncBatch.IsMoverBatch(e, 0, e.Length));
        }

        [Test]
        public void EmptyTruncatedAndNullBatchesHaveNoOwner()
        {
            byte[] empty = Bytes(new SyncBatch.Writer());
            Assert.Equal(SyncBatch.HeaderBytes, empty.Length);
            Assert.Equal((byte)0, SyncBatch.FirstRecordKind(empty, 0, empty.Length));
            Assert.False(SyncBatch.IsMoverBatch(empty, 0, empty.Length));
            Assert.Equal((byte)0, SyncBatch.FirstRecordKind(null, 0, 10));
            Assert.Equal((byte)0, SyncBatch.FirstRecordKind(new byte[4], 0, 4));
            // a count that runs past the array is refused, not read out of bounds
            Assert.Equal((byte)0, SyncBatch.FirstRecordKind(new byte[8], 4, 8));
            Assert.Equal((byte)0, SyncBatch.FirstRecordKind(new byte[8], -1, 8));
        }

        [Test]
        public void HostAcceptsOnlyClientRecordKindsFromAClient()
        {
            SyncRecordKind[] fromClient =
            {
                SyncRecordKind.Spawn, SyncRecordKind.Upsert, SyncRecordKind.Values, SyncRecordKind.Despawn, SyncRecordKind.ZoneCells,
                SyncRecordKind.Cost, SyncRecordKind.Request, SyncRecordKind.Global, SyncRecordKind.RequestIds, SyncRecordKind.ProposalHeader,
            };
            SyncRecordKind[] hostOnly =
            {
                SyncRecordKind.Prefab, SyncRecordKind.IdRange, SyncRecordKind.Clock, SyncRecordKind.BucketDigest, SyncRecordKind.EntityHashes,
                SyncRecordKind.EntityExplain, SyncRecordKind.IconSet, SyncRecordKind.IconControl, SyncRecordKind.LineRows,
                SyncRecordKind.MoverSpawn, SyncRecordKind.MoverState, SyncRecordKind.MoverDespawn, SyncRecordKind.Epoch, SyncRecordKind.ProposalResult,
            };
            foreach (SyncRecordKind k in fromClient) Assert.True(SyncBatch.AcceptedFromClient(k), k.ToString());
            foreach (SyncRecordKind k in hostOnly) Assert.False(SyncBatch.AcceptedFromClient(k), k.ToString());
            // every kind is decided on purpose (a new kind must be added to one of the lists above)
            Assert.Equal(Enum.GetValues(typeof(SyncRecordKind)).Length, fromClient.Length + hostOnly.Length);
        }
    }
}
