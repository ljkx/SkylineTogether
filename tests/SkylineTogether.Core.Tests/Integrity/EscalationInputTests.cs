using System.Collections.Generic;
using SkylineTogether.Core.Integrity;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Integrity
{
    /// <summary>
    /// What feeds the repair escalation: only confirmed differences (a host bulk edit in flight differs everywhere for a
    /// moment and must not re-send whole buckets), and the given-up count of the current session only.
    /// </summary>
    public sealed class EscalationInputTests
    {
        private static List<EntityHash> Range(int count, ulong hash)
        {
            var list = new List<EntityHash>();
            for (int i = 1; i <= count; i++) list.Add(new EntityHash((uint)i, hash + (ulong)i));
            return list;
        }

        [Test]
        public void ABulkEditInFlightIsMismatchingButNotConfirmed()
        {
            var t = new DivergenceTracker();
            var o = new DivergenceTracker.Outcome();
            var e = new RepairEscalation();
            int over = e.Thresholds.EntityRepairLimit + 10;
            // the host changed many objects; the client has not applied them yet
            t.OnDetail(Range(over, 1000), Range(over, 0), 10.0, o);
            Assert.Equal(over, o.Mismatching);
            Assert.Equal(0, o.Confirmed, "seen once: nothing confirmed");
            Assert.Equal(RepairEscalation.Step.None, e.OnDetail(0, o.Mismatching, o.Confirmed, o.Repair.Count, 10.0), "no re-send of the whole bucket");
            Assert.Equal(RepairEscalation.Step.RebaselineBucket, e.OnDetail(1, o.Mismatching, o.Repair.Count, 10.0), "the raw count would have re-sent it");
            // a moment later the client caught up: it matches
            t.OnDetail(Range(over, 1000), Range(over, 1000), 11.0, o);
            Assert.Equal(0, o.Mismatching);
            Assert.Equal(0, o.Confirmed);
        }

        [Test]
        public void UnconfirmedDifferencesStillCountAsUnconvergedRounds()
        {
            var e = new RepairEscalation();
            double now = 0;
            Assert.Equal(RepairEscalation.Step.None, e.OnDetail(7, 2, 2, 2, now));
            Assert.Equal(RepairEscalation.Step.None, e.OnDetail(7, 2, 0, 0, now += 5), "waiting for verification: differs, not confirmed");
            Assert.Equal(RepairEscalation.Step.RebaselineBucket, e.OnDetail(7, 2, 0, 0, now += 5), "two rounds without converging");
        }

        [Test]
        public void AStableDifferenceIsConfirmed()
        {
            var t = new DivergenceTracker();
            var o = new DivergenceTracker.Outcome();
            t.OnDetail(Range(3, 1000), Range(3, 0), 0, o);
            Assert.Equal(0, o.Confirmed);
            t.OnDetail(Range(3, 1000), Range(3, 0), 2, o);
            Assert.Equal(3, o.Confirmed);
            Assert.Equal(3, o.Mismatching);
        }

        [Test]
        public void TheGivenUpCountOfASessionStartsAtZero()
        {
            var t = new DivergenceTracker { RepairsBeforeTolerance = 0, ToleranceSeconds = 10, MaxTolerances = 1 };
            var o = new DivergenceTracker.Outcome();
            t.OnDetail(Range(1, 1), Range(1, 2), 0, o);
            t.OnDetail(Range(1, 1), Range(1, 2), 2, o);
            Assert.Equal(1, o.GivenUp.Count);
            Assert.Equal(1L, t.GivenUpThisSession);
            t.Clear(); // the session ended
            Assert.Equal(0L, t.GivenUpThisSession, "the next session's resync offer does not count the last session's");
            Assert.Equal(1L, t.GivenUpCount, "the statistics keep it");
        }
    }
}
