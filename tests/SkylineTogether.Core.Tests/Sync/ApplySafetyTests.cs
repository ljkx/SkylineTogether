using System;
using System.Collections.Generic;
using SkylineTogether.Core.Scheduling;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>
    /// The rules that keep a client from holding a stale id binding (ARCHITECTURE.md#net-ids, #client-apply): ids are
    /// released only after the batch carrying their despawns was handed to the peers, records carry the id's generation
    /// and a record for a stale binding replaces the entity instead of writing into it, value messages hold the apply
    /// acknowledgement until they are applied, and a batch can be rolled back to a record boundary.
    /// </summary>
    public sealed class ApplySafetyTests
    {
        // ------------------------------------------------------------------ release after broadcast

        [Test]
        public void AStagedIdStaysAllocatedUntilItsBatchIsCommitted()
        {
            var ids = new NetIdAllocator { MinQuarantineSeconds = 0 };
            uint a = ids.Allocate(), b = ids.Allocate();
            var staged = new StagedReleases();
            staged.Stage(a);
            staged.Stage(b);
            staged.Stage(a); // twice: ignored
            Assert.Equal(2, staged.Count);
            Assert.True(ids.IsAllocated(a) && ids.IsAllocated(b), "nothing is released while the batch is being built");
            Assert.False(ids.IsQuarantined(a));
            ids.Seal(1, 0);
            Assert.Equal(0, ids.Update(uint.MaxValue, 0, 100), "an id that was never released can never be freed");
            Assert.Equal(2, staged.Commit(ids));
            Assert.Equal(0, staged.Count);
            Assert.True(ids.IsQuarantined(a) && ids.IsQuarantined(b), "committed: the quarantine starts");
            ids.Seal(2, 1);
            Assert.Equal(2, ids.Update(2, 0, 2));
            Assert.True(ids.IsFree(a));
        }

        [Test]
        public void AFrameThatSentNothingWritesItsDespawnsAgain()
        {
            var ids = new NetIdAllocator { MinQuarantineSeconds = 0 };
            uint a = ids.Allocate(), b = ids.Allocate();
            var staged = new StagedReleases();
            var batch = new SyncBatch.Writer();
            // frame 1: both despawns written, then the capture throws before the broadcast
            batch.Despawn(a);
            staged.Stage(a);
            batch.Despawn(b);
            staged.Stage(b);
            // frame 2: a fresh batch; the staged ids are still allocated and their despawns go out again
            batch.Reset();
            Assert.True(ids.IsAllocated(a) && ids.IsAllocated(b));
            staged.Rewrite(id => batch.Despawn(id));
            ArraySegment<byte> bytes = batch.Finish();
            List<SyncRecord> records = SyncBatch.Read(bytes.Array, bytes.Offset, bytes.Count);
            Assert.Equal(2, records.Count);
            Assert.Equal(SyncRecordKind.Despawn, records[0].Kind);
            Assert.Equal(a, records[0].NetId);
            Assert.Equal(b, records[1].NetId);
            Assert.Equal(2L, staged.Rewritten);
            Assert.Equal(2, staged.Commit(ids));
        }

        [Test]
        public void ARolledBackCaptureStepRewritesOnlyItsOwnDespawns()
        {
            var staged = new StagedReleases();
            var batch = new SyncBatch.Writer();
            batch.Despawn(10);
            staged.Stage(10);
            SyncBatch.Writer.Mark mark = batch.GetMark();
            int stagedMark = staged.Mark;
            // the failing step: a despawn, then a record left half-written by an exception
            batch.Despawn(11);
            staged.Stage(11);
            batch.BeginEntity(SyncRecordKind.Spawn, 12, Family.Object, true, 0);
            batch.AddComponent(0x1234UL, SyncComponentKind.Data, new byte[] { 1, 2, 3 }, 0, 3);
            batch.RollBack(mark);
            staged.Rewrite(id => batch.Despawn(id), stagedMark);
            batch.Despawn(13);
            ArraySegment<byte> bytes = batch.Finish();
            List<SyncRecord> records = SyncBatch.Read(bytes.Array, bytes.Offset, bytes.Count); // never malformed
            Assert.Equal(3, records.Count);
            Assert.Equal(10u, records[0].NetId);
            Assert.Equal(11u, records[1].NetId);
            Assert.Equal(SyncRecordKind.Despawn, records[1].Kind);
            Assert.Equal(13u, records[2].NetId);
        }

        [Test]
        public void RollBackRefusesAMarkFromAnotherBatch()
        {
            var a = new SyncBatch.Writer();
            a.Despawn(1);
            a.Despawn(2);
            SyncBatch.Writer.Mark late = a.GetMark();
            var b = new SyncBatch.Writer();
            Assert.Throws<ArgumentOutOfRangeException>(() => b.RollBack(late));
        }

        // ------------------------------------------------------------------ generations

        [Test]
        public void EntityRecordsCarryTheGeneration()
        {
            var w = new SyncBatch.Writer();
            w.BeginEntity(SyncRecordKind.Spawn, 300, Family.NetEdge, true, 7);
            w.EndEntity();
            w.BeginEntity(SyncRecordKind.Values, 301, Family.Object, false);
            w.EndEntity();
            ArraySegment<byte> bytes = w.Finish();
            // golden layout: version, count (u32), kind, id (varint), family, complete set, generation, component count (u16)
            byte[] expected =
            {
                SyncBatch.Version, 2, 0, 0, 0,
                (byte)SyncRecordKind.Spawn, 0xAC, 0x02, (byte)Family.NetEdge, 1, 7, 0, 0,
                (byte)SyncRecordKind.Values, 0xAD, 0x02, (byte)Family.Object, 0, 0, 0, 0,
            };
            var actual = new byte[bytes.Count];
            Buffer.BlockCopy(bytes.Array, bytes.Offset, actual, 0, bytes.Count);
            Assert.SequenceEqual(expected, actual);
            List<SyncRecord> records = SyncBatch.Read(bytes.Array, bytes.Offset, bytes.Count);
            Assert.Equal((byte)7, records[0].Generation);
            Assert.Equal((byte)0, records[1].Generation);
        }

        [Test]
        public void TheGenerationTableKnowsOnlyWhatWasSet()
        {
            var table = new IdGenerationTable();
            Assert.False(table.TryGet(5, out _));
            table.Set(5, 0);
            Assert.True(table.TryGet(5, out byte g) && g == 0, "generation 0 is a known value, not 'unknown'");
            table.Set(5, 200);
            Assert.True(table.TryGet(5, out g) && g == 200);
            table.Set(NetIdAllocator.MaxId, 3);
            Assert.True(table.TryGet(NetIdAllocator.MaxId, out g) && g == 3);
            table.Set(0, 1);
            table.Set(NetIdAllocator.MaxId + 1, 1);
            Assert.False(table.TryGet(0, out _));
            Assert.False(table.TryGet(NetIdAllocator.MaxId + 1, out _));
            table.Forget(5);
            Assert.False(table.TryGet(5, out _));
            Assert.Equal(1, table.KnownCount);
            table.Clear();
            Assert.False(table.TryGet(NetIdAllocator.MaxId, out _));
        }

        [Test]
        public void AKnownGenerationDecidesAlone()
        {
            // same generation: the same host entity, whatever changed in place (even its family or prefab class)
            Assert.Equal(BoundRecordVerdict.Write, BoundRecordCheck.Check(4, true, 4, Family.Object, Family.Object, true, true));
            Assert.Equal(BoundRecordVerdict.Write, BoundRecordCheck.Check(4, true, 4, Family.NetEdge, Family.Object, true, false));
            // another generation: the id was reused for another entity; never written into the old one
            Assert.Equal(BoundRecordVerdict.Replace, BoundRecordCheck.Check(5, true, 4, Family.Object, Family.Object, true, true));
            Assert.Equal(BoundRecordVerdict.Replace, BoundRecordCheck.Check(0, true, 255, Family.Object, Family.Object, false, true));
        }

        [Test]
        public void WithoutAGenerationTheFamilyAndPrefabClassDecide()
        {
            // a client's own prediction (granted id): the host's echo is written into it
            Assert.Equal(BoundRecordVerdict.Write, BoundRecordCheck.Check(9, false, 0, Family.Object, Family.Object, true, true));
            Assert.Equal(BoundRecordVerdict.Write, BoundRecordCheck.Check(9, false, 0, Family.Object, Family.Object, false, true));
            // a building bound where the host now sends a road edge: never morphed across types
            Assert.Equal(BoundRecordVerdict.Replace, BoundRecordCheck.Check(9, false, 0, Family.NetEdge, Family.Object, true, true));
            // same family, prefabs of different classes (a tree where a building was)
            Assert.Equal(BoundRecordVerdict.Replace, BoundRecordCheck.Check(9, false, 0, Family.Object, Family.Object, true, false));
        }

        // ------------------------------------------------------------------ acknowledgement holds of value messages

        [Test]
        public void AHeldValueMessageKeepsLaterEpochsUnacknowledged()
        {
            var t = new ApplyAckTracker();
            t.OnReceived(); // epoch 1, applied at once
            long hold = t.HoldNext(); // a value message arrives and waits
            Assert.Equal(2L, hold);
            Assert.Equal(1L, t.AppliedThrough, "what arrived before it stays acknowledgeable");
            long e2 = t.OnReceived(); // epoch 2 (say it despawns an id the value message names) is applied at once
            Assert.Equal(1L, t.AppliedThrough, "epoch 2 is not acknowledged while the older value message waits");
            long e3 = t.OnReceived();
            t.Hold(e3); // an epoch held by the structure applier
            t.Release(hold);
            Assert.Equal(e2, t.AppliedThrough);
            t.Release(e3);
            Assert.Equal(e3, t.AppliedThrough);
        }

        [Test]
        public void HoldNextBeforeAnyEpochHoldsTheFirstOne()
        {
            var t = new ApplyAckTracker();
            long hold = t.HoldNext();
            Assert.Equal(1L, hold);
            Assert.Equal(0L, t.AppliedThrough);
            t.OnReceived();
            t.Hold(1); // the structure applier holds the same sequence number: allowed
            Assert.Equal(0L, t.AppliedThrough);
            t.Release(1);
            Assert.Equal(0L, t.AppliedThrough);
            t.Release(hold);
            Assert.Equal(1L, t.AppliedThrough);
        }

        [Test]
        public void EveryWayOutOfTheHeldQueueReleasesItsHold()
        {
            var t = new ApplyAckTracker();
            var q = new AckHeldQueue<string>();
            q.Attach(t);
            t.OnReceived();
            q.Enqueue("a");
            t.OnReceived();
            q.Enqueue("b");
            t.OnReceived();
            q.Enqueue("c");
            t.OnReceived();
            Assert.Equal(1L, t.AppliedThrough);
            Assert.True(q.TryDequeue(out string first) && first == "a");
            Assert.Equal(2L, t.AppliedThrough);
            Assert.True(q.TryDequeueHeld(out string second, out long hold) && second == "b");
            Assert.Equal(2L, t.AppliedThrough, "a handed-out hold stays until its owner releases it");
            q.Release(hold);
            Assert.Equal(3L, t.AppliedThrough);
            Assert.True(q.EvictOldest());
            Assert.Equal(4L, t.AppliedThrough);
            q.Enqueue("d");
            q.Clear();
            Assert.Equal(4L, t.AppliedThrough);
            Assert.Equal(0, t.HeldCount);
            Assert.Equal(2L, q.Dropped);
        }

        [Test]
        public void AttachingAnotherTrackerDropsWhatWasHeldOnTheOldOne()
        {
            var old = new ApplyAckTracker();
            var q = new AckHeldQueue<int>();
            q.Attach(old);
            old.OnReceived();
            q.Enqueue(1);
            Assert.Equal(1, old.HeldCount);
            var fresh = new ApplyAckTracker();
            q.Attach(fresh);
            Assert.Equal(0, old.HeldCount);
            Assert.Equal(0, q.Count);
            q.Attach(null);
            q.Enqueue(2); // outside a session nothing is held
            Assert.True(q.TryDequeue(out int v) && v == 2);
        }

        [Test]
        public void AnEvictedInspectionSampleIsHandedBack()
        {
            var q = new InspectionSampleQueue<int>();
            var evicted = new List<int>();
            for (int i = 0; i < InspectionSampleQueue<int>.MaxPending + 2; i++) q.Enqueue(1000, 0, i, evicted.Add);
            Assert.SequenceEqual(new[] { 0, 1 }, evicted.ToArray());
            Assert.Equal(InspectionSampleQueue<int>.MaxPending, q.Count);
        }
    }
}
