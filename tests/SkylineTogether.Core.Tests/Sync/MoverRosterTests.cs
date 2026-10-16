using System;
using System.Collections.Generic;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>
    /// The mover roster (ARCHITECTURE.md#movers): the host's live movers per window of ids, so a client removes movers the host
    /// no longer has (a plane stuck in its sky) and asks for the ones it lacks.
    /// </summary>
    public sealed class MoverRosterTests
    {
        private static List<SyncRecord> RoundTrip(SyncBatch.Writer w)
        {
            ArraySegment<byte> bytes = w.Finish();
            var copy = new byte[bytes.Count];
            Buffer.BlockCopy(bytes.Array, bytes.Offset, copy, 0, bytes.Count);
            return SyncBatch.Read(copy, 0, copy.Length);
        }

        private static List<uint> Runs(params uint[] ids)
        {
            var runs = new List<uint>();
            MoverRoster.BuildRuns(ids, runs);
            return runs;
        }

        [Test]
        public void IdsBecomeRuns()
        {
            Assert.SequenceEqual(new uint[0], Runs());
            Assert.SequenceEqual(new uint[] { 5, 1 }, Runs(5));
            Assert.SequenceEqual(new uint[] { 5, 3, 10, 1, 12, 2 }, Runs(5, 6, 7, 10, 12, 13));
            Assert.Throws<ArgumentException>(() => Runs(5, 5));
            Assert.Throws<ArgumentException>(() => Runs(6, 5));
        }

        [Test]
        public void TheRecordLayoutIsPinned()
        {
            // kind 23 | first 100 | span 1000 | 2 runs | (gap 0, len 3) (gap 2, len 1): ids 100-102 and 105
            var w = new SyncBatch.Writer();
            w.MoverRoster(100, 1000, Runs(100, 101, 102, 105));
            ArraySegment<byte> bytes = w.Finish();
            var body = new byte[bytes.Count - SyncBatch.HeaderBytes];
            Buffer.BlockCopy(bytes.Array, bytes.Offset + SyncBatch.HeaderBytes, body, 0, body.Length);
            Assert.SequenceEqual(new byte[] { 23, 100, 0xE8, 0x07, 2, 0, 3, 2, 1 }, body);
        }

        [Test]
        public void RosterRoundTripsAndRoutesAsAMoversBatch()
        {
            var w = new SyncBatch.Writer();
            w.MoverDespawn(7);
            w.MoverRoster(1, 4096, Runs(1, 2, 3, 4000, 4096));
            List<SyncRecord> recs = RoundTrip(w);
            Assert.Equal(2, recs.Count);
            SyncRecord r = recs[1];
            Assert.Equal(SyncRecordKind.MoverRoster, r.Kind);
            Assert.Equal(1u, r.RangeStart);
            Assert.Equal(4096u, r.RangeCount);
            Assert.SequenceEqual(new uint[] { 1, 3, 4000, 1, 4096, 1 }, r.Ids);
            Assert.False(SyncBatch.AcceptedFromClient(SyncRecordKind.MoverRoster), "host only");
        }

        [Test]
        public void TheWriterRefusesRunsOutsideTheWindow()
        {
            var w = new SyncBatch.Writer();
            Assert.Throws<ArgumentException>(() => w.MoverRoster(0, 10, new uint[0]));
            Assert.Throws<ArgumentException>(() => w.MoverRoster(10, 5, new uint[] { 9, 1 }));
            Assert.Throws<ArgumentException>(() => w.MoverRoster(10, 5, new uint[] { 14, 2 }));
            Assert.Throws<ArgumentException>(() => w.MoverRoster(10, 5, new uint[] { 10, 2, 12, 1 }), "touching runs must be one");
            Assert.Throws<ArgumentException>(() => w.MoverRoster(10, 5, new uint[] { 10, 0 }));
            Assert.Throws<ArgumentException>(() => w.MoverRoster(10, MoverRoster.MaxSpan + 1, new uint[0]));
        }

        [Test]
        public void TheDecoderRefusesRunsOutsideTheWindow()
        {
            // hand-made bodies: header (version, count 1) + record
            byte[] Body(params byte[] record)
            {
                var b = new byte[SyncBatch.HeaderBytes + record.Length];
                b[0] = SyncBatch.Version;
                b[1] = 1;
                Buffer.BlockCopy(record, 0, b, SyncBatch.HeaderBytes, record.Length);
                return b;
            }
            Assert.Equal(1, SyncBatch.Read(Body(23, 10, 5, 1, 0, 5), 0, SyncBatch.HeaderBytes + 6).Count);
            Assert.Throws<DecodeException>(() => SyncBatch.Read(Body(23, 10, 5, 1, 0, 6), 0, SyncBatch.HeaderBytes + 6)); // past the end
            Assert.Throws<DecodeException>(() => SyncBatch.Read(Body(23, 10, 5, 1, 0, 0), 0, SyncBatch.HeaderBytes + 6)); // empty run
            Assert.Throws<DecodeException>(() => SyncBatch.Read(Body(23, 10, 5, 2, 0, 1, 0, 1), 0, SyncBatch.HeaderBytes + 8)); // runs that touch
            Assert.Throws<DecodeException>(() => SyncBatch.Read(Body(23, 0, 5, 0), 0, SyncBatch.HeaderBytes + 4)); // id 0
            Assert.Throws<DecodeException>(() => SyncBatch.Read(Body(23, 10, 5, 100, 0, 1), 0, SyncBatch.HeaderBytes + 6)); // more runs than bytes
        }

        [Test]
        public void DiffFindsMoversOnlyOneSideHas()
        {
            uint[] host = Runs(10, 11, 12, 20, 30, 31).ToArray();
            var extra = new List<uint>();
            var missing = new List<uint>();
            // the client has 11 and 12 of the first run, 15 (a ghost), 20, 31, and 5 and 50 outside the window
            MoverRoster.Diff(10, 30, host, new uint[] { 5, 11, 12, 15, 20, 31, 50 }, extra, missing, 100);
            Assert.SequenceEqual(new uint[] { 15 }, extra);
            Assert.SequenceEqual(new uint[] { 10, 30 }, missing);

            extra.Clear();
            missing.Clear();
            MoverRoster.Diff(10, 30, new uint[0], new uint[] { 10, 39, 40 }, extra, missing, 100);
            Assert.SequenceEqual(new uint[] { 10, 39 }, extra, "an empty roster removes every mover of its window");
            Assert.Equal(0, missing.Count);

            extra.Clear();
            missing.Clear();
            MoverRoster.Diff(1, 5000, Runs(1, 2, 3, 4, 5).ToArray(), new uint[0], extra, missing, 3);
            Assert.SequenceEqual(new uint[] { 1, 2, 3 }, missing, "capped");
        }

        [Test]
        public void ScheduleCoversTheIdSpaceAndBurstsAfterAJoin()
        {
            var s = new MoverRosterSchedule();
            Assert.False(s.TryNext(0, 1, out _, out _), "no ids yet");
            // 640 001 ids: 64 windows of 10 000
            const uint limit = 640_001;
            var covered = new List<(uint, uint)>();
            double now = 0;
            while (s.Passes == 0)
            {
                if (s.TryNext(now, limit, out uint first, out uint span)) covered.Add((first, span));
                now += 0.25; // exact in binary
                Assert.True(now < 1000, "a pass ends");
            }
            Assert.Equal(64, covered.Count);
            uint expect = 1;
            foreach ((uint first, uint span) in covered)
            {
                Assert.Equal(expect, first);
                expect = first + span;
            }
            Assert.Equal(limit, expect);
            Assert.Near(64 * MoverRosterSchedule.IntervalSeconds, now, 0.5);

            // a join: one window per call for a whole pass, then the normal pace again
            s.Burst();
            int burst = 0;
            while (s.TryNext(now, limit, out _, out _)) burst++;
            Assert.Equal(MoverRosterSchedule.Windows, burst);
            Assert.Equal(2L, s.Passes);
            Assert.False(s.TryNext(now + 0.1, limit, out _, out _));
            Assert.True(s.TryNext(now + MoverRosterSchedule.IntervalSeconds, limit, out uint again, out _));
            Assert.Equal(1u, again);

            // small cities: windows are at least MinWidth ids
            var small = new MoverRosterSchedule();
            Assert.True(small.TryNext(0, 3000, out uint f1, out uint s1));
            Assert.Equal(1u, f1);
            Assert.Equal((uint)MoverRosterSchedule.MinWidth, s1);
        }

        [Test]
        public void AuditRemovesGhostsAtOnceAndAsksForMissingMoversOnTheSecondRoster()
        {
            var audit = new MoverRosterAudit();
            var remove = new List<uint>();
            var request = new List<uint>();
            uint[] host = Runs(100, 101, 102).ToArray();
            // the client still has 150 (the host landed and removed that plane, the despawn never applied) and lacks 102
            audit.OnRoster(100, 100, host, new uint[] { 100, 101, 150 }, remove, request);
            Assert.SequenceEqual(new uint[] { 150 }, remove);
            Assert.Equal(0, request.Count);
            Assert.Equal(1, audit.Pending);

            remove.Clear();
            audit.OnRoster(100, 100, host, new uint[] { 100, 101 }, remove, request);
            Assert.Equal(0, remove.Count);
            Assert.SequenceEqual(new uint[] { 102 }, request, "missing twice in a row");
            Assert.Equal(0, audit.Pending);

            // asking resets the count; a mover that arrived meanwhile is forgotten
            request.Clear();
            audit.OnRoster(100, 100, host, new uint[] { 100, 101 }, remove, request);
            Assert.Equal(0, request.Count);
            audit.OnRoster(100, 100, host, new uint[] { 100, 101, 102 }, remove, request);
            Assert.Equal(0, request.Count);
            Assert.Equal(0, audit.Pending);
            // another window leaves this one's pending ids alone
            audit.OnRoster(100, 100, Runs(100, 101, 102, 103).ToArray(), new uint[] { 100, 101, 102 }, remove, request);
            audit.OnRoster(1000, 100, new uint[0], new uint[0], remove, request);
            Assert.Equal(1, audit.Pending);
            Assert.Equal(1L, audit.Removed);
            Assert.Equal(1L, audit.Requested);
            Assert.Equal(6L, audit.Rosters);
        }
    }
}
