using System;
using System.Collections.Generic;
using SkylineTogether.Core.Identity;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Identity
{
    /// <summary>The host's <c>BuildOrder</c> allocator and the re-basing of proposal ranges (ARCH §5.4.3, §7.4).</summary>
    public sealed class BuildOrderAllocatorTests
    {
        private static BuildOrderRange B(uint start, uint end) => new BuildOrderRange(start, end);

        [Test]
        public void RangeFacts()
        {
            BuildOrderRange forward = B(10, 25);
            Assert.Equal(10u, forward.Min);
            Assert.Equal(25u, forward.Max);
            Assert.Equal(15u, forward.Width);
            Assert.False(forward.IsInverted);
            BuildOrderRange inverted = B(25, 10);
            Assert.Equal(10u, inverted.Min);
            Assert.Equal(25u, inverted.Max);
            Assert.Equal(15u, inverted.Width);
            Assert.True(inverted.IsInverted);
            Assert.Equal(0u, B(7, 7).Width);
            Assert.False(B(7, 7).IsInverted);
            Assert.Equal("[25, 10]", inverted.ToString());
        }

        [Test]
        public void ObserveKeepsTheMonotonicMaximumPlusOne()
        {
            var allocator = new BuildOrderAllocator();
            Assert.Equal(0u, allocator.Next, "nothing observed yet");
            allocator.Observe(0, 15);
            Assert.Equal(16u, allocator.Next, "as UpdateBuildOrderJob: max(start, end) + 1");
            allocator.Observe(100, 40);
            Assert.Equal(101u, allocator.Next, "inverted ranges count with their larger end");
            allocator.Observe(5, 9);
            Assert.Equal(101u, allocator.Next, "never decreases");
            allocator.Observe(B(300, 300));
            Assert.Equal(301u, allocator.Next);
            Assert.False(allocator.IsSaturated);
        }

        [Test]
        public void RebaseMovesClientRangesAboveEveryObservedRange()
        {
            // a client predicted three edges from its own counter (GenerateEdgesSystem: [counter + 16 i, counter + 16 i + 15])
            var allocator = new BuildOrderAllocator();
            allocator.Observe(0, 4999);
            var ranges = new List<BuildOrderRange> { B(1000, 1015), B(1016, 1031), B(1032, 1047) };
            allocator.Rebase(ranges);
            Assert.SequenceEqual(new[] { B(5000, 5015), B(5016, 5031), B(5032, 5047) }, ranges);
            Assert.Equal(5048u, allocator.Next, "the counter advances past the committed ranges");

            var later = new List<BuildOrderRange> { B(1048, 1063) };
            allocator.Rebase(later);
            Assert.SequenceEqual(new[] { B(5048, 5063) }, later, "a second proposal follows the first");
        }

        [Test]
        public void RebasePreservesOrderWidthsOrientationAndOverlapsAndRemovesGaps()
        {
            var allocator = new BuildOrderAllocator();
            allocator.Observe(0, 99);
            // input order is not sorted; two ranges overlap (one inverted); a gap separates the groups
            var ranges = new List<BuildOrderRange>
            {
                B(700, 715),        // group 2
                B(215, 200),        // group 1, inverted, width 15
                B(208, 230),        // group 1, overlaps the previous range
                B(240, 240),        // group 1b: separate group (240 > 230), width 0
                B(716, 731)         // group 3 (716 > 715: adjacent ranges form separate groups, as in ResetBuildOrderSystem)
            };
            allocator.Rebase(ranges);
            // group 1 (200..230) -> 100..130, group 1b (240) -> 131, group 2 (700..715) -> 132..147, group 3 (716..731) -> 148..163
            Assert.Equal(B(132, 147), ranges[0]);
            Assert.Equal(B(115, 100), ranges[1], "inverted stays inverted with the same width");
            Assert.Equal(B(108, 130), ranges[2], "keeps its offset to its group start");
            Assert.Equal(B(131, 131), ranges[3]);
            Assert.Equal(B(148, 163), ranges[4]);
            Assert.Equal(164u, allocator.Next);
        }

        [Test]
        public void RebaseOfDuplicateRangesKeepsThemEqual()
        {
            var allocator = new BuildOrderAllocator();
            allocator.Observe(10, 10);
            var ranges = new List<BuildOrderRange> { B(500, 515), B(500, 515), B(515, 500) };
            allocator.Rebase(ranges);
            Assert.Equal(B(11, 26), ranges[0]);
            Assert.Equal(B(11, 26), ranges[1]);
            Assert.Equal(B(26, 11), ranges[2]);
            Assert.Equal(27u, allocator.Next);
        }

        [Test]
        public void RebaseOnAFreshAllocatorStartsAtZero()
        {
            var allocator = new BuildOrderAllocator();
            var ranges = new List<BuildOrderRange> { B(900, 915) };
            allocator.Rebase(ranges);
            Assert.Equal(B(0, 15), ranges[0]);
            Assert.Equal(16u, allocator.Next);
        }

        [Test]
        public void EmptyAndNullInput()
        {
            var allocator = new BuildOrderAllocator();
            allocator.Observe(0, 9);
            allocator.Rebase(new List<BuildOrderRange>());
            Assert.Equal(10u, allocator.Next, "an empty proposal changes nothing");
            Assert.Throws<ArgumentNullException>(() => allocator.Rebase(null));
            var array = new[] { B(3, 4) };
            allocator.Rebase(array);
            Assert.Equal(B(10, 11), array[0], "arrays work as IList too");
        }

        [Test]
        public void SaturationAndOverflowAreRefusedWithoutChangingTheInput()
        {
            var allocator = new BuildOrderAllocator();
            allocator.Observe(0, uint.MaxValue - 10);
            Assert.Equal(uint.MaxValue - 9, allocator.Next);
            var tooWide = new List<BuildOrderRange> { B(0, 5), B(6, 20) };
            Assert.Throws<InvalidOperationException>(() => allocator.Rebase(tooWide));
            Assert.SequenceEqual(new[] { B(0, 5), B(6, 20) }, tooWide, "unchanged after a refused re-base");
            Assert.Equal(uint.MaxValue - 9, allocator.Next);

            var fits = new List<BuildOrderRange> { B(0, 8) };
            allocator.Rebase(fits);
            Assert.Equal(B(uint.MaxValue - 9, uint.MaxValue - 1), fits[0]);
            Assert.Equal(uint.MaxValue, allocator.Next);
            Assert.False(allocator.IsSaturated, "uint.MaxValue itself is still free");

            var last = new List<BuildOrderRange> { B(4, 4) };
            allocator.Rebase(last);
            Assert.Equal(B(uint.MaxValue, uint.MaxValue), last[0]);
            Assert.True(allocator.IsSaturated);
            Assert.Throws<InvalidOperationException>(() => allocator.Rebase(new List<BuildOrderRange> { B(1, 1) }));

            var observed = new BuildOrderAllocator();
            observed.Observe(uint.MaxValue, 0);
            Assert.True(observed.IsSaturated);
            Assert.Equal(uint.MaxValue, observed.Next);
            observed.Observe(5, 6);
            Assert.True(observed.IsSaturated, "saturation is permanent");
        }

        [Test]
        public void RandomRebasesPreserveOrderAndWidthsAndNeverOverlapObservedRanges()
        {
            var random = new DeterministicRandom(5431);
            for (int round = 0; round < 500; round++)
            {
                var allocator = new BuildOrderAllocator();
                uint observedMax = 0;
                bool any = false;
                int observedCount = random.Next(20);
                for (int i = 0; i < observedCount; i++)
                {
                    uint start = (uint)random.Next(1 << 20);
                    uint end = start + (uint)random.Next(64);
                    if (random.Next(2) == 0) allocator.Observe(start, end);
                    else allocator.Observe(end, start);
                    observedMax = any ? Math.Max(observedMax, end) : end;
                    any = true;
                }
                uint nextBefore = allocator.Next;
                Assert.Equal(any ? observedMax + 1 : 0u, nextBefore);

                int n = 1 + random.Next(40);
                var original = new List<BuildOrderRange>();
                for (int i = 0; i < n; i++)
                {
                    uint min = (uint)random.Next(5000);
                    uint width = random.Next(4) == 0 ? 0u : (uint)random.Next(40);
                    original.Add(random.Next(3) == 0 ? B(min + width, min) : B(min, min + width));
                }
                var rebased = new List<BuildOrderRange>(original);
                allocator.Rebase(rebased);

                uint maxRebased = 0;
                for (int i = 0; i < n; i++)
                {
                    BuildOrderRange o = original[i], r = rebased[i];
                    Assert.Equal(o.Width, r.Width, "width " + i);
                    Assert.Equal(o.IsInverted, r.IsInverted, "orientation " + i);
                    Assert.True(r.Min >= nextBefore, "never below the counter, so never overlapping an observed range");
                    maxRebased = Math.Max(maxRebased, r.Max);
                    for (int j = 0; j < n; j++)
                    {
                        BuildOrderRange oj = original[j], rj = rebased[j];
                        Assert.Equal(Math.Sign(o.Min.CompareTo(oj.Min)), Math.Sign(r.Min.CompareTo(rj.Min)), "order of minima " + i + "/" + j);
                        Assert.Equal(Math.Sign(o.Max.CompareTo(oj.Max)), Math.Sign(r.Max.CompareTo(rj.Max)), "order of maxima " + i + "/" + j);
                        bool overlapped = o.Min <= oj.Max && oj.Min <= o.Max;
                        bool overlaps = r.Min <= rj.Max && rj.Min <= r.Max;
                        Assert.Equal(overlapped, overlaps, "overlap " + i + "/" + j);
                    }
                }
                Assert.Equal(maxRebased + 1, allocator.Next, "the counter is the new maximum + 1");
                Assert.True(maxRebased - nextBefore < 5000 + 40, "gaps between groups are removed");
            }
        }

        [Test]
        public void RebaseMatchesTheCompactionOfResetBuildOrderSystem()
        {
            // ResetBuildOrderSystem compacts all build orders the same way, starting at 0 (ResetBuildOrderSystem.cs:62-148):
            // re-basing onto a fresh allocator therefore equals a vanilla compaction of the same ranges
            var ranges = new List<BuildOrderRange> { B(64, 79), B(80, 95), B(90, 70), B(200, 215), B(1000, 1000) };
            var allocator = new BuildOrderAllocator();
            allocator.Rebase(ranges);
            Assert.SequenceEqual(new[] { B(0, 15), B(16, 31), B(26, 6), B(32, 47), B(48, 48) }, ranges);
            Assert.Equal(49u, allocator.Next, "the vanilla counter after the compaction is the maximum + 1 as well");
        }
    }
}
