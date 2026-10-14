using System.Collections.Generic;
using SkylineTogether.Core.Identity;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>The pure helpers behind the sync-coverage fixes (presence watch, channel pacing, set diffs).</summary>
    public sealed class SyncCoverageTests
    {
        [Test]
        public void SignatureIgnoresOrderAndDuplicates()
        {
            ulong a = TypeSetSignature.Of(new ulong[] { 11, 22, 33 });
            ulong b = TypeSetSignature.Of(new ulong[] { 33, 11, 22, 22 });
            Assert.Equal(a, b);
            ulong c = TypeSetSignature.Add(TypeSetSignature.Add(TypeSetSignature.Add(TypeSetSignature.Empty, 22), 33), 11);
            Assert.Equal(a, c);
        }

        [Test]
        public void SignatureChangesWhenATypeIsAddedOrRemoved()
        {
            ulong building = TypeSetSignature.Of(new ulong[] { 1, 2, 3 });
            ulong onFire = TypeSetSignature.Of(new ulong[] { 1, 2, 3, 4 });
            ulong damaged = TypeSetSignature.Of(new ulong[] { 1, 2, 3, 5 });
            Assert.NotEqual(building, onFire);
            Assert.NotEqual(onFire, damaged);
            Assert.NotEqual(TypeSetSignature.Empty, building);
            // swapping one type for another never cancels out
            Assert.NotEqual(TypeSetSignature.Of(new ulong[] { 1, 4 }), TypeSetSignature.Of(new ulong[] { 2, 3 }));
        }

        [Test]
        public void TrackerReportsChangesAgainstTheLastSentSet()
        {
            var t = new PresenceTracker();
            t.Record(7, 100);
            Assert.False(t.Changed(7, 100));
            Assert.True(t.Changed(7, 101));
            // a check is not a send: the change stays reported until the new set is recorded
            Assert.True(t.Changed(7, 101));
            t.Record(7, 101);
            Assert.False(t.Changed(7, 101));
            // back to the old set (the fire went out) is a change again
            Assert.True(t.Changed(7, 100));
        }

        [Test]
        public void TrackerTakesTheFirstSightingAsBaseline()
        {
            var t = new PresenceTracker();
            Assert.False(t.Changed(9, 5));
            Assert.Equal(1, t.Count);
            Assert.True(t.Changed(9, 6));
            t.Forget(9);
            Assert.False(t.Changed(9, 6));
            Assert.False(t.Changed(0, 6)); // id 0 is never tracked
            Assert.Equal(1, t.Count);
            t.Clear();
            Assert.Equal(0, t.Count);
        }

        [Test]
        public void RollingPassHandsOutEachIdOnce()
        {
            var pass = new RollingIdPass();
            Assert.False(pass.InProgress);
            pass.Refill(new uint[] { 3, 1, 2 });
            var seen = new List<uint>();
            while (pass.Next(out uint id)) seen.Add(id);
            Assert.Equal(3, seen.Count);
            Assert.Equal(3u, seen[0]);
            Assert.False(pass.InProgress);
            Assert.False(pass.Next(out _));
            pass.Refill(null);
            Assert.Equal(0, pass.Count);
        }

        [Test]
        public void PacerHoldsAChannelForItsInterval()
        {
            var p = new ChannelPacer();
            Assert.True(p.Due(1, 5.0, 0.0, false));
            Assert.False(p.Due(1, 5.0, 1.0, false));
            Assert.True(p.Due(2, 5.0, 1.0, false)); // channels are independent
            Assert.True(p.Due(1, 5.0, 1.5, true)); // dirty: always due
            Assert.False(p.Due(1, 5.0, 6.0, false)); // the dirty offer restarted the interval
            Assert.True(p.Due(1, 5.0, 6.5, false));
            Assert.True(p.Due(1, 0.0, 6.6, false)); // no interval: every pass
            p.Clear();
            Assert.True(p.Due(1, 5.0, 6.7, false));
        }

        [Test]
        public void SetDiffListsAddsAndRemovesAscending()
        {
            var add = new List<uint> { 99 };
            var remove = new List<uint>();
            IdSetDiff.Compute(new uint[] { 5, 1, 9 }, new uint[] { 9, 2, 7, 1 }, add, remove);
            Assert.Equal(2, add.Count);
            Assert.Equal(2u, add[0]);
            Assert.Equal(7u, add[1]);
            Assert.Equal(1, remove.Count);
            Assert.Equal(5u, remove[0]);
            IdSetDiff.Compute(null, null, add, remove);
            Assert.Equal(0, add.Count + remove.Count);
        }

        [Test]
        public void EventsChirpsAndJournalEntriesAreOneFamily()
        {
            Assert.Equal(Family.Event, FamilyRules.Classify(FamilyMarker.EventEntity, default, out _));
            Assert.Equal(Family.Event, FamilyRules.Classify(FamilyMarker.JournalOrChirpOrLifePath, default, out _));
            // a meeting is an event entity of the agents (service requests and meetings come first)
            Assert.Equal(Family.TransientAgent, FamilyRules.Classify(FamilyMarker.EventEntity | FamilyMarker.TransientAgent, default, out _));
        }
    }
}
