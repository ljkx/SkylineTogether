using System.Collections.Generic;
using SkylineTogether.Core.Integrity;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Integrity
{
    public sealed class DivergenceTrackerTests
    {
        private static List<EntityHash> L(params (uint Id, ulong Hash)[] items)
        {
            var list = new List<EntityHash>();
            foreach ((uint id, ulong hash) in items) list.Add(new EntityHash(id, hash));
            return list;
        }

        [Test]
        public void DigestIsOrderIndependentAndSeesEveryKindOfDifference()
        {
            EntityDigest.Accumulator a = EntityDigest.Of(L((1, 10), (2, 20), (3, 30)));
            EntityDigest.Accumulator b = EntityDigest.Of(L((3, 30), (1, 10), (2, 20)));
            Assert.True(a.Matches(b.Count, b.Sum));
            Assert.False(a.Matches(3, EntityDigest.Of(L((1, 10), (2, 21), (3, 30))).Sum), "a differing hash");
            Assert.False(a.Matches(2, EntityDigest.Of(L((1, 10), (2, 20))).Sum), "a missing entity");
            Assert.NotEqual(EntityDigest.Of(L((1, 20), (2, 10))).Sum, EntityDigest.Of(L((1, 10), (2, 20))).Sum);
        }

        [Test]
        public void BucketsCoverIdsWithoutGaps()
        {
            Assert.Equal(1u, EntityDigest.FirstId(0));
            Assert.Equal((uint)EntityDigest.BucketSize - 1, EntityDigest.LastId(0));
            Assert.Equal((uint)EntityDigest.BucketSize, EntityDigest.FirstId(1));
            Assert.Equal(1u, EntityDigest.BucketOf((uint)EntityDigest.BucketSize));
            Assert.Equal(0u, EntityDigest.BucketOf(1));
        }

        [Test]
        public void CompareListsMissingExtraAndDiffering()
        {
            var host = L((1, 10), (2, 20), (4, 40));
            var client = L((1, 10), (2, 99), (3, 30));
            List<(uint NetId, EntityDifference Kind)> d = EntityDigest.Compare(host, client);
            Assert.Equal(3, d.Count);
            Assert.Equal((2u, EntityDifference.Differs), d[0]);
            Assert.Equal((3u, EntityDifference.ExtraOnClient), d[1]);
            Assert.Equal((4u, EntityDifference.MissingOnClient), d[2]);
        }

        [Test]
        public void OneMismatchIsNotConfirmedASecondStableOneIs()
        {
            var t = new DivergenceTracker();
            var o = new DivergenceTracker.Outcome();
            t.OnDetail(L((5, 1)), L((5, 2)), 10.0, o);
            Assert.Equal(0, o.Repair.Count);
            Assert.True(o.Recheck);
            Assert.Near(1.5, o.RecheckAfter, 1e-9);
            t.OnDetail(L((5, 1)), L((5, 2)), 10.5, o);
            Assert.Equal(0, o.Repair.Count, "too early");
            t.OnDetail(L((5, 1)), L((5, 2)), 12.0, o);
            Assert.SequenceEqual(new uint[] { 5 }, o.Repair);
            Assert.Equal(1L, t.RepairsRequested);
        }

        [Test]
        public void AClientThatKeepsChangingIsFollowingNotDiverged()
        {
            var t = new DivergenceTracker();
            var o = new DivergenceTracker.Outcome();
            for (int i = 0; i < 10; i++)
            {
                t.OnDetail(L((5, 1000UL + (ulong)i)), L((5, (ulong)i)), 10.0 + i * 2, o);
                Assert.Equal(0, o.Repair.Count, "round " + i);
            }
        }

        [Test]
        public void RepairThatWorksIsCountedAndForgotten()
        {
            var t = new DivergenceTracker();
            var o = new DivergenceTracker.Outcome();
            t.OnDetail(L((5, 1)), L((5, 2)), 0, o);
            t.OnDetail(L((5, 1)), L((5, 2)), 2, o);
            Assert.Equal(1, o.Repair.Count);
            t.OnDetail(L((5, 1)), L((5, 1)), 6, o);
            Assert.Equal(1, o.Matching);
            Assert.Equal(1L, t.RepairsSucceeded);
            Assert.Equal(0, t.Tracked);
        }

        [Test]
        public void AStubbornEntityIsToleratedAfterTwoRepairsAndExplainedOnce()
        {
            var t = new DivergenceTracker();
            var o = new DivergenceTracker.Outcome();
            double now = 0;
            int repairs = 0;
            var host = L((5, 1));
            var client = L((5, 2));
            bool tolerated = false;
            for (int i = 0; i < 40 && !tolerated; i++)
            {
                t.OnDetail(host, client, now, o);
                repairs += o.Repair.Count;
                if (o.NewlyTolerated.Count > 0)
                {
                    tolerated = true;
                    Assert.SequenceEqual(new uint[] { 5 }, o.Explain);
                }
                now += 2;
            }
            Assert.True(tolerated);
            Assert.Equal(2, repairs);
            Assert.True(t.IsTolerated(5, now));
            Assert.True(t.TryGetOverride(5, now, out bool present, out ulong hash));
            Assert.True(present);
            Assert.Equal(1UL, hash);

            // while tolerated: no further repairs or explanations; the override follows the host
            t.OnDetail(L((5, 77)), client, now, o);
            Assert.Equal(0, o.Repair.Count);
            Assert.Equal(0, o.Explain.Count);
            t.TryGetOverride(5, now, out _, out hash);
            Assert.Equal(77UL, hash);

            // tolerance expires: start over
            now += t.ToleranceSeconds + 1;
            Assert.False(t.IsTolerated(5, now));
            t.Sweep(now);
            Assert.Equal(0, t.Tracked);
        }

        [Test]
        public void MissingAndExtraEntitiesAreRepairedLikeDifferingOnes()
        {
            var t = new DivergenceTracker();
            var o = new DivergenceTracker.Outcome();
            t.OnDetail(L((5, 1)), L((6, 1)), 0, o);
            t.OnDetail(L((5, 1)), L((6, 1)), 2, o);
            o.Repair.Sort();
            Assert.SequenceEqual(new uint[] { 5, 6 }, o.Repair);
        }

        [Test]
        public void ToleratedExtraEntityOverrideSaysTheHostLacksIt()
        {
            var t = new DivergenceTracker { RepairsBeforeTolerance = 0 };
            var o = new DivergenceTracker.Outcome();
            t.OnDetail(L(), L((6, 1)), 0, o);
            t.OnDetail(L(), L((6, 1)), 2, o);
            Assert.SequenceEqual(new uint[] { 6 }, o.NewlyTolerated);
            Assert.True(t.TryGetOverride(6, 2, out bool present, out _));
            Assert.False(present);
        }

        [Test]
        public void RepairsPerDetailAreCapped()
        {
            var t = new DivergenceTracker { MaxRepairsPerDetail = 3 };
            var o = new DivergenceTracker.Outcome();
            var host = new List<EntityHash>();
            var client = new List<EntityHash>();
            for (uint i = 1; i <= 10; i++)
            {
                host.Add(new EntityHash(i, 1));
                client.Add(new EntityHash(i, 2));
            }
            t.OnDetail(host, client, 0, o);
            t.OnDetail(host, client, 2, o);
            Assert.Equal(3, o.Repair.Count);
            Assert.True(o.Recheck);
            t.OnDetail(host, client, 4, o);
            Assert.Equal(3, o.Repair.Count, "the next detail repairs the next three");
        }

        [Test]
        public void MatchedBucketResolvesItsEntriesButKeepsTolerated()
        {
            var t = new DivergenceTracker();
            var o = new DivergenceTracker.Outcome();
            t.OnDetail(L((5, 1)), L((5, 2)), 0, o);
            Assert.True(t.HasEntriesIn(0));
            Assert.False(t.HasEntriesIn(1));
            t.OnBucketMatched(0, 1);
            Assert.Equal(0, t.Tracked);
        }

        [Test]
        public void TrackingIsBounded()
        {
            var t = new DivergenceTracker { MaxTracked = 2 };
            var o = new DivergenceTracker.Outcome();
            t.OnDetail(L((1, 1), (2, 1), (3, 1)), L(), 0, o);
            Assert.Equal(2, t.Tracked);
            Assert.Equal(1L, t.Overflow);
        }

        [Test]
        public void SchedulerKeepsTheEarliestDueTimeAndWaitsForAnswers()
        {
            var s = new DetailScheduler { MaxPending = 2, AwaitTimeoutSeconds = 5 };
            Assert.True(s.Request(7, 3.0, 0));
            Assert.True(s.Request(7, 1.0, 0));
            Assert.True(s.Request(8, 2.0, 0));
            Assert.False(s.Request(9, 0.0, 0), "full");
            Assert.Equal(1L, s.Dropped);
            var due = new List<uint>();
            Assert.Equal(0, s.TakeDue(0.5, 10, due));
            Assert.Equal(2, s.TakeDue(2.5, 10, due));
            Assert.SequenceEqual(new uint[] { 7, 8 }, due);
            Assert.True(s.IsAwaiting(7, 3));
            Assert.False(s.Request(7, 3, 3), "awaited");
            s.OnArrived(7);
            Assert.True(s.Request(7, 3, 3));
            Assert.True(s.Request(8, 9, 9), "the await timed out");
            s.Cancel(7);
            Assert.False(s.IsPending(7));
        }

        [Test]
        public void RateGateRefillsOverTime()
        {
            var g = new RateGate(10, 5);
            Assert.True(g.TryTake(0, 5));
            Assert.False(g.TryTake(0, 1));
            Assert.Equal(2, g.Available(0.25));
            Assert.True(g.TryTake(10, 5));
            Assert.False(g.TryTake(10, 1));
        }
    }
}
