using SkylineTogether.Core.Integrity;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Integrity
{
    /// <summary>
    /// The escalation above object repairs: bucket rebaselines when repairs do not converge, a resync offer
    /// for massive divergence, and the guards that keep both from looping.
    /// </summary>
    public sealed class RepairEscalationTests
    {
        [Test]
        public void ABucketThatStaysDifferentAfterRepairsIsRebaselinedOnce()
        {
            var e = new RepairEscalation();
            double now = 0;
            Assert.Equal(RepairEscalation.Step.None, e.OnDetail(7, 2, 0, now), "first sighting: nothing repaired yet");
            Assert.Equal(RepairEscalation.Step.None, e.OnDetail(7, 2, 2, now += 2), "repairs requested");
            Assert.Equal(RepairEscalation.Step.None, e.OnDetail(7, 2, 2, now += 5), "still different after one repair round");
            Assert.Equal(RepairEscalation.Step.RebaselineBucket, e.OnDetail(7, 2, 0, now += 5), "after two rounds: the whole bucket");
            Assert.Equal(1L, e.Rebaselines);
            for (int i = 0; i < 20; i++) Assert.Equal(RepairEscalation.Step.None, e.OnDetail(7, 2, 1, now += 5), "the bucket cooldown holds");
            now += e.BucketCooldownSeconds;
            Assert.Equal(RepairEscalation.Step.RebaselineBucket, e.OnDetail(7, 2, 1, now), "the bucket stayed stuck: again once the cooldown is over");
            Assert.Equal(2L, e.Rebaselines);
        }

        [Test]
        public void AMatchResetsTheBucket()
        {
            var e = new RepairEscalation();
            e.OnDetail(3, 1, 1, 0);
            e.OnDetail(3, 1, 1, 5);
            e.OnBucketMatched(3);
            Assert.Equal(RepairEscalation.Step.None, e.OnDetail(3, 1, 1, 10), "converged in between: counting starts over");
            Assert.Equal(RepairEscalation.Step.None, e.OnDetail(3, 0, 0, 12));
            Assert.Equal(RepairEscalation.Step.None, e.OnDetail(3, 1, 1, 14), "an empty detail also resets");
        }

        [Test]
        public void TooManyDifferencesForObjectRepairsRebaselineAtOnceWithinTheRateCap()
        {
            var e = new RepairEscalation();
            int over = e.Thresholds.EntityRepairLimit + 1;
            int rebaselined = 0;
            for (uint b = 0; b < 10; b++)
            {
                if (e.OnDetail(b, over, 64, 1.0) == RepairEscalation.Step.RebaselineBucket) rebaselined++;
            }
            Assert.Equal(e.MaxRebaselinesPerMinute, rebaselined, "at most a few buckets a minute");
            Assert.Equal(RepairEscalation.Step.RebaselineBucket, e.OnDetail(20, over, 64, 62.0), "the next minute");
            Assert.Equal(RepairEscalation.Step.None, e.OnDetail(21, e.Thresholds.EntityRepairLimit, 64, 62.0), "at the limit object repair still handles it");
        }

        [Test]
        public void MassiveDivergenceOffersOneResyncPerCooldown()
        {
            var e = new RepairEscalation();
            e.OnCycle(200, 30, 0, 100000, 10);
            Assert.Null(e.PendingOffer, "one bad cycle is not enough");
            e.OnCycle(200, 10, 0, 100000, 40);
            e.OnCycle(200, 30, 0, 100000, 70);
            Assert.Null(e.PendingOffer, "the cycles must be consecutive");
            e.OnCycle(200, 30, 0, 100000, 100);
            Assert.NotNull(e.PendingOffer);
            Assert.Contains("30 of 200", e.PendingOffer);
            Assert.Equal(1L, e.ResyncOffers);
            e.Decline(110);
            Assert.Null(e.PendingOffer);
            for (int i = 0; i < 10; i++) e.OnCycle(200, 100, 0, 100000, 120 + i * 30);
            Assert.Null(e.PendingOffer, "declined: not asked again within the cooldown");
            e.OnCycle(200, 100, 0, 100000, 110 + e.ResyncCooldownSeconds + 1);
            e.OnCycle(200, 100, 0, 100000, 110 + e.ResyncCooldownSeconds + 31);
            Assert.NotNull(e.PendingOffer, "asked again after the cooldown");

            var small = new RepairEscalation();
            for (int i = 0; i < 5; i++) small.OnCycle(10, 10, 0, 500, i * 30);
            Assert.Null(small.PendingOffer, "a tiny city never escalates on bucket fractions");
        }

        [Test]
        public void GivenUpObjectsAndRepeatedRebaselinesAlsoOffer()
        {
            var e = new RepairEscalation();
            e.OnCycle(200, 0, 49, 1000, 10);
            Assert.Null(e.PendingOffer);
            e.OnCycle(200, 0, 60, 1000000, 20);
            Assert.Null(e.PendingOffer, "60 of a million objects is not massive");
            e.OnCycle(200, 0, 60, 1000, 30);
            Assert.Contains("60 objects", e.PendingOffer);

            var r = new RepairEscalation { MaxRebaselinesPerMinute = 100 };
            int over = r.Thresholds.EntityRepairLimit + 1;
            for (uint b = 0; b < (uint)r.RebaselinesForResync; b++) r.OnDetail(b, over, 0, b * 10.0);
            Assert.NotNull(r.PendingOffer, "many rebaselines within 10 minutes");
        }

        [Test]
        public void AcceptedResyncsAreCappedAcrossRejoins()
        {
            var e = new RepairEscalation { ResyncCooldownSeconds = 60 };
            double now = 0;
            for (int i = 0; i < e.MaxResyncsPerHour; i++)
            {
                e.OnCycle(200, 100, 0, 1000, now += 30);
                e.OnCycle(200, 100, 0, 1000, now += 30);
                Assert.NotNull(e.PendingOffer, "offer " + (i + 1));
                e.Accept(now);
                e.Reset(); // the rejoin starts a new session
                now += 61;
            }
            for (int i = 0; i < 20; i++) e.OnCycle(200, 100, 0, 1000, now += 30);
            Assert.Null(e.PendingOffer, "no third resync within an hour: a deterministic fault would loop");
            Assert.Equal((long)e.MaxResyncsPerHour, e.ResyncOffers, "Reset keeps the counters");
        }
    }
}
