using System;
using System.Collections.Generic;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>Appending a cached batch (the join's prefab table) to another writer.</summary>
    public sealed class SyncBatchAppendTests
    {
        [Test]
        public void AppendedRecordsReadBackInOrder()
        {
            var table = new SyncBatch.Writer();
            table.Prefab(5, "Road", new byte[] { 1, 2, 3 }, 0, 3);
            table.Prefab(6, "House", new byte[] { 4 }, 0, 1);
            var w = new SyncBatch.Writer();
            w.Append(table);
            w.IdCeiling(1000);
            w.IdRange(900, 100);
            ArraySegment<byte> bytes = w.Finish();
            List<SyncRecord> records = SyncBatch.Read(bytes.Array, bytes.Offset, bytes.Count);
            Assert.Equal(4, records.Count);
            Assert.Equal(4, w.RecordCount);
            Assert.Equal(SyncRecordKind.Prefab, records[0].Kind);
            Assert.Equal(5u, records[0].NetId);
            Assert.Equal("Road", records[0].PrefabName);
            Assert.Equal("House", records[1].PrefabName);
            Assert.Equal(SyncRecordKind.IdCeiling, records[2].Kind);
            Assert.Equal(SyncRecordKind.IdRange, records[3].Kind);
        }

        [Test]
        public void TheCacheCanBeAppendedAgainAndAnEmptyOneAddsNothing()
        {
            var table = new SyncBatch.Writer();
            table.Prefab(1, "A", new byte[0], 0, 0);
            var first = new SyncBatch.Writer();
            first.Append(table);
            var second = new SyncBatch.Writer();
            second.Append(new SyncBatch.Writer());
            second.Append(table);
            ArraySegment<byte> a = first.Finish();
            ArraySegment<byte> b = second.Finish();
            Assert.Equal(a.Count, b.Count);
            Assert.Equal(1, SyncBatch.Read(b.Array, b.Offset, b.Count).Count);
        }

        [Test]
        public void AWriterCannotAppendItself()
        {
            var w = new SyncBatch.Writer();
            Assert.Throws<ArgumentException>(() => w.Append(w));
            Assert.Throws<ArgumentNullException>(() => w.Append(null));
        }
    }
}
