using System;
using System.Collections.Generic;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    public sealed class IntegrityRecordsTests
    {
        [Test]
        public void IntegrityRecordsRoundTrip()
        {
            var w = new SyncBatch.Writer();
            w.BucketDigest(17, 120, 0xDEADBEEFCAFEF00DUL);
            w.EntityHashes(17, new List<EntityHash> { new EntityHash(2177, 1), new EntityHash(2200, ulong.MaxValue) });
            w.RequestIds(SyncRequest.ResendEntities, new uint[] { 5, 70000, 1 });
            w.RequestIds(SyncRequest.IconFullSync, Array.Empty<uint>());
            w.EntityExplain(2177, new List<ComponentHash>
            {
                new ComponentHash(0x1111UL, 0x2222UL, ComponentHashState.Value),
                new ComponentHash(0x3333UL, 0, ComponentHashState.Presence),
            });
            var lane = new SyncRef { Kind = SyncRef.LaneKey, Id = 900, Prefab = 12, Index = -1, X = -6400, Y = 128, Z = 3 };
            w.IconSet(SyncRef.ForNetId(42), new List<SyncIcon>
            {
                new SyncIcon { Prefab = 7, Target = lane, Priority = 100, Flags = 0x24, X = 1.5f, Y = -2f, Z = 1e6f },
                new SyncIcon { Prefab = 8, Target = new SyncRef { Kind = SyncRef.Unnamed }, Priority = 10 },
            });
            w.IconSet(new SyncRef { Kind = SyncRef.SubObjectKey, Id = 3, Prefab = 4, Index = 2 }, new List<SyncIcon>());
            w.IconControl(IconControlMode.FullEnd, 300, 412, 99);
            w.LineRows(new byte[] { 9, 8, 7 }, 1, 2);
            ArraySegment<byte> bytes = w.Finish();

            List<SyncRecord> recs = SyncBatch.Read(bytes.Array, bytes.Offset, bytes.Count);
            Assert.Equal(9, recs.Count);
            Assert.Equal(SyncRecordKind.BucketDigest, recs[0].Kind);
            Assert.Equal(17u, recs[0].Bucket);
            Assert.Equal(120u, recs[0].Count);
            Assert.Equal(0xDEADBEEFCAFEF00DUL, recs[0].Hash);
            Assert.Equal(2, recs[1].Hashes.Count);
            Assert.Equal(2200u, recs[1].Hashes[1].NetId);
            Assert.Equal(ulong.MaxValue, recs[1].Hashes[1].Hash);
            Assert.Equal(SyncRequest.ResendEntities, recs[2].Request);
            Assert.SequenceEqual(new uint[] { 5, 70000, 1 }, recs[2].Ids);
            Assert.Equal(SyncRequest.IconFullSync, recs[3].Request);
            Assert.Equal(0, recs[3].Ids.Length);
            Assert.Equal(2177u, recs[4].NetId);
            Assert.Equal(ComponentHashState.Presence, recs[4].ComponentHashes[1].State);
            Assert.Equal(0x2222UL, recs[4].ComponentHashes[0].ValueHash);
            Assert.True(recs[5].Owner.Equals(SyncRef.ForNetId(42)));
            Assert.Equal(2, recs[5].Icons.Count);
            Assert.True(recs[5].Icons[0].Target.Equals(lane));
            Assert.Equal(1e6f, recs[5].Icons[0].Z);
            Assert.Equal((byte)0x24, recs[5].Icons[0].Flags);
            Assert.Equal(SyncRef.Unnamed, recs[5].Icons[1].Target.Kind);
            Assert.Equal(SyncRef.SubObjectKey, recs[6].Owner.Kind);
            Assert.Equal(0, recs[6].Icons.Count);
            Assert.Equal(IconControlMode.FullEnd, recs[7].IconMode);
            Assert.Equal(300u, recs[7].Count);
            Assert.Equal(412u, recs[7].Count2);
            Assert.SequenceEqual(new byte[] { 8, 7 }, recs[8].Payload);
        }

        [Test]
        public void MalformedIntegrityRecordsAreDecodeExceptions()
        {
            // an icon set claiming more icons than the bytes can hold
            var w = new SyncBatch.Writer();
            w.IconSet(SyncRef.ForNetId(1), new List<SyncIcon> { new SyncIcon { Prefab = 1 } });
            ArraySegment<byte> b = w.Finish();
            byte[] bad = Copy(b);
            int countPos = 1 + 4 + 1 + 2; // version, record count, kind, owner (kind + varint 1)
            bad[countPos] = 100;
            Assert.Throws<DecodeException>(() => SyncBatch.Read(bad, 0, bad.Length));

            // an unknown reference kind
            byte[] badRef = Copy(b);
            badRef[1 + 4 + 1] = 9;
            Assert.Throws<DecodeException>(() => SyncBatch.Read(badRef, 0, badRef.Length));

            // an unknown icon control mode
            var w2 = new SyncBatch.Writer();
            w2.IconControl(IconControlMode.Digest, 1, 2, 3);
            byte[] badMode = Copy(w2.Finish());
            badMode[1 + 4 + 1] = 77;
            Assert.Throws<DecodeException>(() => SyncBatch.Read(badMode, 0, badMode.Length));

            // a truncated hash list
            var w3 = new SyncBatch.Writer();
            w3.EntityHashes(1, new List<EntityHash> { new EntityHash(1, 2) });
            byte[] truncated = Copy(w3.Finish());
            Assert.Throws<DecodeException>(() => SyncBatch.Read(truncated, 0, truncated.Length - 3));
        }

        [Test]
        public void WriterRefusesOversizedLists()
        {
            var w = new SyncBatch.Writer();
            Assert.Throws<ArgumentException>(() => w.RequestIds(SyncRequest.ResendEntities, new uint[SyncBatch.MaxIdsPerRecord + 1]));
            Assert.Throws<ArgumentException>(() => w.IconSet(SyncRef.ForNetId(1), new SyncIcon[SyncBatch.MaxIconsPerOwner + 1]));
        }

        [Test]
        public void NormalizedRefHashIgnoresKeyIndexAndPosition()
        {
            var a = new SyncRef { Kind = SyncRef.LaneKey, Id = 10, Prefab = 3, Index = 1, X = 5, Y = 6, Z = 7 };
            var b = new SyncRef { Kind = SyncRef.LaneKey, Id = 10, Prefab = 3, Index = 4, X = 6, Y = 6, Z = 8 };
            var c = new SyncRef { Kind = SyncRef.SubObjectKey, Id = 10, Prefab = 3 };
            Assert.Equal(a.NormalizedHash(), b.NormalizedHash());
            Assert.NotEqual(a.NormalizedHash(), c.NormalizedHash());
            Assert.Equal(default(SyncRef).NormalizedHash(), new SyncRef { Kind = SyncRef.Unnamed }.NormalizedHash());
            Assert.NotEqual(SyncRef.ForNetId(10).NormalizedHash(), a.NormalizedHash());
            Assert.True(SyncRef.ForNetId(0).IsNone);
            Assert.False(a.Equals(b));
        }

        private static byte[] Copy(ArraySegment<byte> b)
        {
            var copy = new byte[b.Count];
            Buffer.BlockCopy(b.Array, b.Offset, copy, 0, b.Count);
            return copy;
        }
    }
}
