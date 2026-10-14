using System;
using System.Collections.Generic;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>The client's deferred edit baselines (<see cref="PendingBaselines"/>).</summary>
    public sealed class PendingBaselinesTests
    {
        private static List<uint> TakeAll(PendingBaselines p)
        {
            var taken = new List<uint>();
            while (p.TryTake(out uint id)) taken.Add(id);
            return taken;
        }

        [Test]
        public void IdsWrittenEveryFrameAreHashedOnceInFirstMarkedOrder()
        {
            var p = new PendingBaselines();
            // an applier writing the same 3 entities for 60 frames before the slice gets to them
            for (int frame = 0; frame < 60; frame++)
            {
                p.Mark(7);
                p.Mark(3);
                p.Mark(9);
            }
            Assert.Equal(3, p.Count);
            Assert.SequenceEqual(new uint[] { 7, 3, 9 }, TakeAll(p), "one hash per entity instead of 180");
            Assert.Equal(0, p.Count);
        }

        [Test]
        public void RemovedIdsAreSkippedAndCanBeMarkedAgain()
        {
            var p = new PendingBaselines();
            p.Mark(1);
            p.Mark(2);
            Assert.True(p.Remove(1), "a baseline set another way (a proposal) ends the wait");
            p.Mark(1);
            Assert.SequenceEqual(new uint[] { 1, 2 }, TakeAll(p));
            Assert.False(p.TryTake(out _));
            p.Mark(0);
            Assert.Equal(0, p.Count, "id 0 is never an entity");
        }

        [Test]
        public void ChurnDoesNotGrowTheQueueWithoutBound()
        {
            var p = new PendingBaselines();
            for (uint round = 0; round < 20000; round++)
            {
                p.Mark(round % 50 + 1);
                p.Remove(round % 50 + 1);
            }
            p.Mark(5);
            Assert.SequenceEqual(new uint[] { 5 }, TakeAll(p));
        }

        [Test]
        public void SteadyStateMarkingAndTakingAllocatesNothing()
        {
            var p = new PendingBaselines();
            for (int warm = 0; warm < 3; warm++)
            {
                for (uint id = 1; id <= 500; id++) p.Mark(id);
                while (p.TryTake(out _)) { }
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int round = 0; round < 20; round++)
            {
                for (uint id = 1; id <= 500; id++) p.Mark(id);
                while (p.TryTake(out _)) { }
            }
            Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        }
    }
}
