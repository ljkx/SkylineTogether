using System;
using System.Collections.Generic;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>The host's net id allocator with quarantine, generations, grants and restore (ARCH §3.1).</summary>
    public sealed class NetIdAllocatorTests
    {
        private static NetIdAllocator Fresh(double minQuarantine = 0) => new NetIdAllocator { MinQuarantineSeconds = minQuarantine };

        [Test]
        public void FreshIdsAreDenseFromOneWithGenerationZero()
        {
            NetIdAllocator a = Fresh();
            Assert.Equal(1u, a.Next);
            for (uint i = 1; i <= 5; i++)
            {
                Assert.Equal(i, a.Allocate());
                Assert.True(a.IsAllocated(i));
                Assert.Equal((byte)0, a.GenerationOf(i));
            }
            Assert.Equal(6u, a.Next);
            Assert.Equal(5, a.AllocatedCount);
            Assert.Equal(0, a.FreeCount);
        }

        [Test]
        public void AReleasedIdIsNotReusedBeforeItsEpochIsSealedAndAcknowledged()
        {
            NetIdAllocator a = Fresh();
            uint x = a.Allocate();
            a.Allocate();
            Assert.True(a.Release(x));
            Assert.True(a.IsQuarantined(x));
            Assert.False(a.IsAllocated(x));
            Assert.Equal(3u, a.Allocate(), "an id in the open epoch is never reused");

            Assert.Equal(0, a.Update(100, 0, 1000), "not sealed yet: nothing can be freed whatever the acks");
            a.Seal(1, 10);
            Assert.Equal(0, a.Update(0, 0, 1000), "nobody acknowledged epoch 1 yet");
            Assert.Equal(1, a.Update(1, 0, 1000));
            Assert.False(a.IsQuarantined(x));
            Assert.True(a.IsFree(x));
            Assert.Equal(x, a.Allocate(), "the lowest free id comes first");
            Assert.Equal((byte)1, a.GenerationOf(x), "a reuse increments the generation");
            Assert.Equal(1L, a.ReusedCount);
        }

        [Test]
        public void TheMinimumQuarantineTimeHoldsAcknowledgedIds()
        {
            NetIdAllocator a = Fresh(3.0);
            uint x = a.Allocate();
            a.Release(x);
            a.Seal(1, 100.0);
            Assert.Equal(0, a.Update(1, 0, 101.0), "acknowledged but sealed only one second ago");
            Assert.Equal(0, a.Update(1, 0, 102.9));
            Assert.Equal(1, a.Update(1, 0, 103.0));
        }

        [Test]
        public void TheJoinBarrierHoldsIdsReleasedFromItsEpochOn()
        {
            NetIdAllocator a = Fresh();
            uint early = a.Allocate();
            uint late = a.Allocate();
            a.Release(early);
            a.Seal(4, 0);
            a.Release(late);
            a.Seal(5, 0);
            Assert.Equal(1, a.Update(9, 5, 1), "epoch 4 lies before the barrier");
            Assert.True(a.IsFree(early));
            Assert.True(a.IsQuarantined(late), "epoch 5 waits for the join to end");
            Assert.Equal(1, a.Update(9, 0, 1), "barrier lifted");
            Assert.True(a.IsFree(late));
        }

        [Test]
        public void QuarantineFreesInEpochOrderOnly()
        {
            NetIdAllocator a = Fresh();
            var ids = new List<uint>();
            for (int i = 0; i < 6; i++) ids.Add(a.Allocate());
            a.Release(ids[0]);
            a.Release(ids[1]);
            a.Seal(1, 0);
            a.Release(ids[2]);
            a.Seal(2, 0);
            a.Release(ids[3]);
            a.Seal(3, 0);
            Assert.Equal(2, a.Update(1, 0, 1));
            Assert.Equal(2, a.FreeCount);
            Assert.True(a.IsQuarantined(ids[2]) && a.IsQuarantined(ids[3]));
            Assert.Equal(2, a.Update(3, 0, 1));
            Assert.Equal(0, a.QuarantinedCount);
        }

        [Test]
        public void SealedEpochsNeverDecrease()
        {
            NetIdAllocator a = Fresh();
            uint x = a.Allocate();
            a.Seal(10, 0);
            a.Release(x);
            a.Seal(3, 0); // an older number is clamped up to the last sealed epoch
            Assert.Equal(10u, a.SealedFrame);
            Assert.Equal(0, a.Update(9, 0, 1), "the release counts as epoch 10, not 3");
            Assert.Equal(1, a.Update(10, 0, 1));
        }

        [Test]
        public void DoubleAndInvalidReleasesAreIgnoredAndCounted()
        {
            NetIdAllocator a = Fresh();
            uint x = a.Allocate();
            Assert.True(a.Release(x));
            Assert.False(a.Release(x), "already quarantined");
            Assert.False(a.Release(0));
            Assert.False(a.Release(77), "never allocated");
            Assert.False(a.Release(NetIdAllocator.MaxId + 1));
            Assert.Equal(4L, a.InvalidReleaseCount);
            a.Seal(1, 0);
            a.Update(1, 0, 1);
            Assert.Equal(x, a.Allocate());
            Assert.Equal(2u, a.Allocate(), "the id was handed out once only");
        }

        [Test]
        public void GrantsTakeRunsOfFreeIdsThenAFreshRun()
        {
            NetIdAllocator a = Fresh();
            for (int i = 0; i < 20; i++) a.Allocate();
            foreach (uint id in new uint[] { 3, 4, 5, 9, 15, 16 }) a.Release(id);
            a.Seal(1, 0);
            a.Update(1, 0, 1);
            var runs = new List<(uint Start, uint Count)>();
            uint granted = a.Grant(10, runs);
            Assert.Equal(10u, granted);
            Assert.Equal(4, runs.Count);
            Assert.Equal((3u, 3u), runs[0]);
            Assert.Equal((9u, 1u), runs[1]);
            Assert.Equal((15u, 2u), runs[2]);
            Assert.Equal((21u, 4u), runs[3], "the rest comes fresh and contiguous");
            Assert.Equal(25u, a.Next);
            foreach ((uint start, uint count) in runs)
            {
                for (uint i = 0; i < count; i++) Assert.True(a.IsAllocated(start + i), "granted ids are allocated");
            }
            Assert.Equal((byte)1, a.GenerationOf(3), "granted reused ids get a new generation");
            Assert.Equal(25u, a.Allocate(), "nothing granted is handed out again");
        }

        [Test]
        public void GrantsAreLimitedToAFewRuns()
        {
            NetIdAllocator a = Fresh();
            for (int i = 0; i < 200; i++) a.Allocate();
            for (uint id = 2; id <= 200; id += 2) a.Release(id); // 100 isolated free ids
            a.Seal(1, 0);
            a.Update(1, 0, 1);
            var runs = new List<(uint Start, uint Count)>();
            Assert.Equal(64u, a.Grant(64, runs));
            Assert.Equal(NetIdAllocator.MaxGrantRuns + 1, runs.Count, "at most MaxGrantRuns free runs, then one fresh run");
            Assert.Equal(201u, runs[runs.Count - 1].Start);
        }

        [Test]
        public void ClaimTakesFreeOrFreshIdsButNeverQuarantinedOnes()
        {
            NetIdAllocator a = Fresh();
            Assert.True(a.Claim(5));
            Assert.Equal(6u, a.Next);
            Assert.Equal(4, a.FreeCount, "1-4 were skipped and are free");
            Assert.False(a.Claim(5), "already allocated");
            Assert.True(a.Claim(2));
            Assert.Equal(1u, a.Allocate());
            a.Release(2);
            Assert.False(a.Claim(2), "quarantined");
            Assert.False(a.Claim(0));
            Assert.False(a.Claim(NetIdAllocator.MaxId + 1));
            Assert.True(a.Claim(NetIdAllocator.MaxId));
            Assert.Equal(NetIdAllocator.MaxId + 1, a.Next);
        }

        [Test]
        public void RestoreAllocatesTheBoundIdsAndFreesTheRest()
        {
            NetIdAllocator a = Fresh();
            int skipped = a.Restore(10, new[]
            {
                new KeyValuePair<uint, byte>(2, 7),
                new KeyValuePair<uint, byte>(5, 0),
                new KeyValuePair<uint, byte>(5, 1),
                new KeyValuePair<uint, byte>(0, 0),
                new KeyValuePair<uint, byte>(NetIdAllocator.MaxId + 5, 0),
            });
            Assert.Equal(3, skipped, "a duplicate, a zero and an out-of-range id");
            Assert.Equal(10u, a.Next);
            Assert.Equal(2, a.AllocatedCount);
            Assert.Equal(7, a.FreeCount);
            Assert.Equal((byte)7, a.GenerationOf(2));
            Assert.Equal(1u, a.Allocate());
            Assert.Equal(3u, a.Allocate());
            uint four = a.Allocate();
            Assert.Equal(4u, four);
            Assert.Equal((byte)1, a.GenerationOf(4), "a restored free id was used before: its reuse bumps the generation");
        }

        [Test]
        public void RestoreRaisesALowHighWaterMark()
        {
            NetIdAllocator a = Fresh();
            a.Restore(3, new[] { new KeyValuePair<uint, byte>(40, 0) });
            Assert.Equal(41u, a.Next);
            Assert.Equal(39, a.FreeCount);
        }

        [Test]
        public void FlushQuarantineFreesEverythingAtOnce()
        {
            NetIdAllocator a = Fresh(100);
            for (int i = 0; i < 4; i++) a.Allocate();
            a.Release(1);
            a.Seal(1, 0);
            a.Release(3);
            Assert.Equal(2, a.FlushQuarantine());
            Assert.Equal(0, a.QuarantinedCount);
            Assert.Equal(1u, a.Allocate());
            Assert.Equal(3u, a.Allocate());
        }

        [Test]
        public void ManyCyclesKeepTheIdRangeBounded()
        {
            // long session: 2,000 entities alive, 1,000 spawns and despawns per epoch for 500 epochs; acks lag 3 epochs
            NetIdAllocator a = Fresh();
            var random = new DeterministicRandom(42);
            var alive = new List<uint>();
            for (int i = 0; i < 2000; i++) alive.Add(a.Allocate());
            for (uint epoch = 1; epoch <= 500; epoch++)
            {
                for (int k = 0; k < 1000; k++)
                {
                    int index = random.Next(alive.Count);
                    Assert.True(a.Release(alive[index]));
                    alive[index] = alive[alive.Count - 1];
                    alive.RemoveAt(alive.Count - 1);
                    alive.Add(a.Allocate());
                }
                a.Seal(epoch, epoch);
                a.Update(epoch >= 3 ? epoch - 3 : 0, 0, epoch);
            }
            Assert.True(a.Next < 2000 + 5 * 1000 + 100, "the high-water mark stays near live + quarantine (" + a.Next + ")");
            Assert.Equal(2000, a.AllocatedCount);
            Assert.Equal(0L, a.InvalidReleaseCount);
        }

        [Test]
        public void IdsNeverExceed24Bits()
        {
            NetIdAllocator a = Fresh();
            Assert.True(a.Claim(NetIdAllocator.MaxId - 1));
            Assert.Equal(1u, a.Allocate(), "free ids below the high-water mark come first");
            var runs = new List<(uint Start, uint Count)>();
            uint granted = a.Grant(NetIdAllocator.MaxId, runs); // everything that is left
            Assert.Equal(NetIdAllocator.MaxId - 2, granted);
            Assert.Equal((NetIdAllocator.MaxId, 1u), runs[runs.Count - 1], "the last fresh id is the 24-bit maximum");
            Assert.False(a.TryAllocate(out uint none));
            Assert.Equal(0u, none);
            Assert.Equal(1L, a.ExhaustedCount);
            Assert.Throws<InvalidOperationException>(() => a.Allocate());
            a.Release(7);
            a.Seal(1, 0);
            a.Update(1, 0, 1);
            Assert.Equal(7u, a.Allocate(), "a released id makes room again");
        }

        [Test]
        public void GrantBookConsumesEachIdOnceAndReturnsTheUnused()
        {
            var book = new IdGrantBook();
            book.Add(1, 100, 10);
            book.Add(1, 500, 3);
            book.Add(2, 200, 5);
            Assert.True(book.IsGranted(1, 105));
            Assert.False(book.IsGranted(1, 110));
            Assert.True(book.IsUnused(1, 105));
            Assert.True(book.IsUnused(1, 105), "asking does not use it");
            Assert.True(book.TryConsume(1, 105));
            Assert.False(book.IsUnused(1, 105));
            Assert.False(book.TryConsume(1, 105), "a stale or replayed spawn cannot bind the id again");
            Assert.False(book.IsUnused(2, 105), "not peer 2's");
            Assert.False(book.TryConsume(1, 200), "another peer's id");
            Assert.False(book.TryConsume(3, 100), "unknown peer");
            Assert.True(book.TryConsume(1, 502));
            var unused = new List<uint>();
            book.Remove(1, unused);
            Assert.Equal(11, unused.Count);
            Assert.False(unused.Contains(105u));
            Assert.False(unused.Contains(502u));
            Assert.True(unused.Contains(500u) && unused.Contains(109u));
            Assert.False(book.IsGranted(1, 100));
            Assert.True(book.IsGranted(2, 204));
        }

        [Test]
        public void CompactionMapsSortedIdsToOneToN()
        {
            Assert.True(NetIdCompaction.BuildMap(new uint[] { 3, 9, 10, 4000 }, out uint[] map));
            Assert.SequenceEqual(new uint[] { 1, 2, 3, 4 }, map);
            Assert.False(NetIdCompaction.BuildMap(new uint[] { 3, 3 }, out _), "duplicates");
            Assert.False(NetIdCompaction.BuildMap(new uint[] { 5, 4 }, out _), "unsorted");
            Assert.False(NetIdCompaction.BuildMap(new uint[] { 0, 4 }, out _), "zero");
            Assert.False(NetIdCompaction.ShouldCompact(1000, 1001), "dense");
            Assert.False(NetIdCompaction.ShouldCompact(10_000, 13_000), "few holes");
            Assert.True(NetIdCompaction.ShouldCompact(10_000, 40_000));
            Assert.False(NetIdCompaction.ShouldCompact(10, 1000), "under 4096 holes it is not worth it");
        }
    }
}
