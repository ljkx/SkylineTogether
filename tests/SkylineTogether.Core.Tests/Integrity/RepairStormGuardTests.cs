using System.Collections.Generic;
using SkylineTogether.Core.Integrity;
using SkylineTogether.Core.Scheduling;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Integrity
{
    /// <summary>
    /// crashfix: the storm guards of the rolling verification. A real two-PC session re-sent the same derived areas again
    /// and again (each repair matched for a moment, which reset the attempt count) and repaired hundreds of agents whose
    /// key links had only not reached the client yet.
    /// </summary>
    public sealed class RepairStormGuardTests
    {
        private static List<EntityHash> L(params (uint Id, ulong Hash)[] items)
        {
            var list = new List<EntityHash>();
            foreach ((uint id, ulong hash) in items) list.Add(new EntityHash(id, hash));
            return list;
        }

        /// <summary>
        /// The client's own system regenerates the value after every repair: the entity matches right after the re-send,
        /// then differs again. Before the fix this repeated forever; now the history bounds it.
        /// </summary>
        [Test]
        public void AnEntityThatKeepsComingBackIsBackedOffAndTolerated()
        {
            var t = new DivergenceTracker();
            var o = new DivergenceTracker.Outcome();
            double now = 0;
            int repairs = 0, tolerated = 0;
            var repairTimes = new List<double>();
            for (int i = 0; i < 2000 && tolerated == 0; i++)
            {
                // diverged: observe twice (confirmation), then a repair
                t.OnDetail(L((5, 1)), L((5, 2)), now, o);
                repairs += o.Repair.Count;
                if (o.Repair.Count > 0) repairTimes.Add(now);
                tolerated += o.NewlyTolerated.Count;
                now += 1;
                if (o.Repair.Count > 0)
                {
                    // the repair matched briefly ...
                    t.OnDetail(L((5, 1)), L((5, 1)), now, o);
                    now += 1;
                }
            }
            Assert.Equal(1, tolerated, "tolerated in the end");
            Assert.Equal(t.MaxRepairsInHistory, repairs, "repairs are bounded by the history");
            Assert.True(t.IsTolerated(5, now));
            // exponential backoff: each gap is at least the doubled backoff
            for (int i = 1; i < repairTimes.Count; i++)
            {
                double wait = t.BackoffSeconds * System.Math.Pow(2, i - 1);
                Assert.True(repairTimes[i] - repairTimes[i - 1] >= wait, "gap " + i + " honours the backoff");
            }
            Assert.True(t.BackedOff > 0);
            Assert.Equal(t.MaxRepairsInHistory, t.RepairsOf(5));
        }

        [Test]
        public void AFirstRepairIsNotDelayedByTheHistory()
        {
            var t = new DivergenceTracker();
            var o = new DivergenceTracker.Outcome();
            t.OnDetail(L((5, 1)), L((5, 2)), 0, o);
            t.OnDetail(L((5, 1)), L((5, 2)), 2, o);
            Assert.SequenceEqual(new uint[] { 5 }, o.Repair);
            Assert.Equal(0, o.Repeated.Count, "a first repair is not a repeat");
        }

        [Test]
        public void RepeatedRepairsAreReportedForExplanation()
        {
            var t = new DivergenceTracker { BackoffSeconds = 0 };
            var o = new DivergenceTracker.Outcome();
            t.OnDetail(L((5, 1)), L((5, 2)), 0, o);
            t.OnDetail(L((5, 1)), L((5, 2)), 2, o);
            Assert.Equal(1, o.Repair.Count);
            t.OnDetail(L((5, 1)), L((5, 1)), 6, o); // matched: resolved, history kept
            Assert.Equal(0, t.Tracked);
            Assert.Equal(1, t.Histories);
            t.OnDetail(L((5, 1)), L((5, 3)), 10, o);
            t.OnDetail(L((5, 1)), L((5, 3)), 12, o);
            Assert.SequenceEqual(new uint[] { 5 }, o.Repair);
            Assert.SequenceEqual(new uint[] { 5 }, o.Repeated);
        }

        [Test]
        public void TheGlobalBudgetBoundsTheRepairRate()
        {
            var t = new DivergenceTracker { MaxRepairsPerMinute = 5 };
            var o = new DivergenceTracker.Outcome();
            var host = new List<EntityHash>();
            var client = new List<EntityHash>();
            for (uint i = 1; i <= 50; i++)
            {
                host.Add(new EntityHash(i, 1));
                client.Add(new EntityHash(i, 2));
            }
            t.OnDetail(host, client, 0, o);
            t.OnDetail(host, client, 2, o);
            Assert.Equal(5, o.Repair.Count, "the burst");
            Assert.True(o.Recheck);
            Assert.True(t.BudgetDeferred > 0);
            t.OnDetail(host, client, 2.5, o);
            Assert.Equal(0, o.Repair.Count, "nothing left this instant");
            t.OnDetail(host, client, 2 + 24.5, o); // 24 s refill two tokens at 5 per minute
            Assert.Equal(2, o.Repair.Count);
        }

        [Test]
        public void APerEntityConfirmationWindowDelaysOnlyThatEntity()
        {
            var t = new DivergenceTracker { ConfirmSecondsFor = id => id == 7 ? 30 : 0 };
            var o = new DivergenceTracker.Outcome();
            t.OnDetail(L((5, 1), (7, 1)), L((5, 2), (7, 2)), 0, o);
            t.OnDetail(L((5, 1), (7, 1)), L((5, 2), (7, 2)), 2, o);
            Assert.SequenceEqual(new uint[] { 5 }, o.Repair, "the default window");
            Assert.True(o.Recheck);
            Assert.Near(3.0, o.RecheckAfter, 1e-9); // the repaired entity's verification comes first
            t.OnDetail(L((5, 1), (7, 1)), L((5, 1), (7, 2)), 20, o);
            Assert.Equal(0, o.Repair.Count, "7 is still inside its window");
            t.OnDetail(L((5, 1), (7, 1)), L((5, 1), (7, 2)), 31, o);
            Assert.SequenceEqual(new uint[] { 7 }, o.Repair);
        }

        [Test]
        public void AnAgentWhoseUpdateArrivesInsideItsWindowIsNeverRepaired()
        {
            var t = new DivergenceTracker { ConfirmSecondsFor = id => 45 };
            var o = new DivergenceTracker.Outcome();
            t.OnDetail(L((9, 1)), L((9, 2)), 0, o);
            t.OnDetail(L((9, 1)), L((9, 2)), 10, o);
            Assert.Equal(0, o.Repair.Count);
            t.OnDetail(L((9, 1)), L((9, 1)), 20, o); // the host's scan streamed the change
            Assert.Equal(0, t.Tracked);
            Assert.Equal(0L, t.RepairsRequested);
        }

        [Test]
        public void AnEntityToleratedTwiceIsGivenUpForTheSession()
        {
            var t = new DivergenceTracker { RepairsBeforeTolerance = 0, ToleranceSeconds = 10, MaxTolerances = 2 };
            var o = new DivergenceTracker.Outcome();
            t.OnDetail(L((5, 1)), L((5, 2)), 0, o);
            t.OnDetail(L((5, 1)), L((5, 2)), 2, o);
            Assert.SequenceEqual(new uint[] { 5 }, o.NewlyTolerated);
            Assert.Equal(0, o.GivenUp.Count);
            // tolerance expires, it still differs: tolerated again, which gives it up
            t.OnDetail(L((5, 1)), L((5, 2)), 13, o);
            t.OnDetail(L((5, 1)), L((5, 2)), 15, o);
            Assert.SequenceEqual(new uint[] { 5 }, o.GivenUp);
            Assert.Equal(1L, t.GivenUpCount);
            t.Sweep(1e9);
            Assert.True(t.IsTolerated(5, 1e9), "given up entities stay masked");
            t.OnDetail(L((5, 1)), L((5, 1)), 1e9, o);
            Assert.True(t.IsTolerated(5, 1e9), "a match does not resurrect a given-up entity");
        }

        [Test]
        public void ForgetDropsEntryAndHistory()
        {
            var t = new DivergenceTracker();
            var o = new DivergenceTracker.Outcome();
            t.OnDetail(L((5, 1)), L((5, 2)), 0, o);
            t.OnDetail(L((5, 1)), L((5, 2)), 2, o);
            Assert.Equal(1, t.Tracked);
            Assert.Equal(1, t.RepairsOf(5));
            Assert.True(t.HasEntriesIn(0));
            t.Forget(5);
            Assert.Equal(0, t.Tracked);
            Assert.Equal(0, t.RepairsOf(5));
            Assert.False(t.HasEntriesIn(0), "the bucket count follows");
        }

        [Test]
        public void BucketCountsFollowResolveAndSweep()
        {
            var t = new DivergenceTracker { ForgetSeconds = 5 };
            var o = new DivergenceTracker.Outcome();
            uint other = (uint)EntityDigest.BucketSize + 3;
            t.OnDetail(L((5, 1)), L((5, 2)), 0, o);
            t.OnDetail(L((other, 1)), L((other, 2)), 0, o);
            Assert.True(t.HasEntriesIn(0));
            Assert.True(t.HasEntriesIn(1));
            t.OnBucketMatched(0, 1);
            Assert.False(t.HasEntriesIn(0));
            Assert.True(t.HasEntriesIn(1));
            t.Sweep(100);
            Assert.False(t.HasEntriesIn(1));
            Assert.Equal(0, t.Tracked);
        }

        [Test]
        public void OldHistoriesAreSweptButTolerancesAreRemembered()
        {
            var t = new DivergenceTracker { HistorySeconds = 100, BackoffSeconds = 0 };
            var o = new DivergenceTracker.Outcome();
            t.OnDetail(L((5, 1)), L((5, 2)), 0, o);
            t.OnDetail(L((5, 1)), L((5, 2)), 2, o);
            t.OnDetail(L((5, 1)), L((5, 1)), 6, o);
            Assert.Equal(1, t.Histories);
            t.Sweep(200);
            Assert.Equal(0, t.Histories, "an old successful repair is forgotten");
        }

        [Test]
        public void SpatialGridRemovesFromCrowdedCellsAndStillQueriesExactly()
        {
            var g = new SpatialHashGrid(128f);
            // a tower: hundreds of households at one point (one cell)
            for (uint i = 1; i <= 500; i++) g.Set(i, 10f, 10f);
            for (uint i = 1; i <= 500; i += 2) Assert.True(g.Remove(i));
            var found = new List<uint>();
            g.Query(10f, 10f, 5f, found);
            Assert.Equal(250, found.Count);
            foreach (uint id in found) Assert.True(id % 2 == 0, "only the kept ids");
            // moving out of the cell and back keeps the bookkeeping consistent
            g.Set(2, 1000f, 1000f);
            g.Set(4, 1000f, 1000f);
            g.Set(2, 10f, 10f);
            found.Clear();
            g.Query(10f, 10f, 5f, found);
            Assert.Equal(249, found.Count);
            found.Clear();
            g.Query(1000f, 1000f, 1f, found);
            Assert.SequenceEqual(new uint[] { 4 }, found);
            // a huge radius walks the populated cells, not the entries, and finds everything
            found.Clear();
            g.Query(0f, 0f, 1e7f, found);
            Assert.Equal(250, found.Count);
            for (uint i = 2; i <= 500; i += 2) g.Remove(i);
            Assert.Equal(0, g.Count);
        }
    }
}
