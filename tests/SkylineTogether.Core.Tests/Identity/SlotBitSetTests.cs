using System;
using System.Collections.Generic;
using SkylineTogether.Core.Identity;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Identity
{
    /// <summary>The internal two-level bit set behind the bound, unspawned and free slot sets.</summary>
    public sealed class SlotBitSetTests
    {
        [Test]
        public void TrailingZeroCountMatchesANaiveLoop()
        {
            for (int i = 0; i < 64; i++)
            {
                Assert.Equal(i, SlotBitSet.TrailingZeroCount(1UL << i), "single bit " + i);
                Assert.Equal(i, SlotBitSet.TrailingZeroCount(ulong.MaxValue << i), "mask from bit " + i);
            }
            var random = new DeterministicRandom(7);
            for (int n = 0; n < 2000; n++)
            {
                ulong v = random.NextULong() | 1UL << random.Next(64);
                int expected = 0;
                while ((v & (1UL << expected)) == 0) expected++;
                Assert.Equal(expected, SlotBitSet.TrailingZeroCount(v));
            }
        }

        [Test]
        public void PopCountMatchesANaiveLoop()
        {
            Assert.Equal(0, SlotBitSet.PopCount(0));
            Assert.Equal(64, SlotBitSet.PopCount(ulong.MaxValue));
            var random = new DeterministicRandom(8);
            for (int n = 0; n < 2000; n++)
            {
                ulong v = random.NextULong();
                int expected = 0;
                for (int i = 0; i < 64; i++) expected += (int)((v >> i) & 1);
                Assert.Equal(expected, SlotBitSet.PopCount(v));
            }
        }

        [Test]
        public void SetGetClearAndCount()
        {
            var set = new SlotBitSet(1 << 24);
            Assert.False(set.Get(5));
            Assert.True(set.Set(5));
            Assert.False(set.Set(5), "setting twice reports no change");
            Assert.True(set.Get(5));
            Assert.Equal(1, set.Count);
            Assert.True(set.Clear(5));
            Assert.False(set.Clear(5));
            Assert.False(set.Get(5));
            Assert.Equal(0, set.Count);
            Assert.False(set.Get(-1));
            Assert.False(set.Clear(-1));
            Assert.False(set.Get(1 << 23), "beyond the allocated words");
            Assert.Throws<ArgumentOutOfRangeException>(() => set.Set(1 << 24));
            Assert.Throws<ArgumentOutOfRangeException>(() => set.Set(-1));
        }

        [Test]
        public void NextSetBitCrossesWordAndSummaryBoundaries()
        {
            var set = new SlotBitSet(1 << 24);
            int[] items = { 0, 63, 64, 127, 4095, 4096, 4097, 262143, 262144, (1 << 24) - 1 };
            foreach (int item in items) set.Set(item);
            var found = new List<int>();
            for (int s = set.NextSetBit(0); s >= 0; s = set.NextSetBit(s + 1)) found.Add(s);
            Assert.SequenceEqual(items, found);
            Assert.Equal(64, set.NextSetBit(1 << 6));
            Assert.Equal(4095, set.NextSetBit(128));
            Assert.Equal(-1, set.NextSetBit(1 << 24));
            Assert.Equal(0, set.NextSetBit(-5), "negative starts clamp to 0");
        }

        [Test]
        public void RandomOperationsMatchASortedSet()
        {
            var random = new DeterministicRandom(42);
            var set = new SlotBitSet(1 << 20);
            var reference = new SortedSet<int>();
            for (int step = 0; step < 20000; step++)
            {
                int item = random.Next(3) == 0 ? random.Next(1 << 20) : random.Next(5000);
                if (random.Next(3) == 0)
                {
                    Assert.Equal(reference.Remove(item), set.Clear(item));
                }
                else
                {
                    Assert.Equal(reference.Add(item), set.Set(item));
                }
                if (step % 997 == 0)
                {
                    int from = random.Next(1 << 20);
                    int expected = -1;
                    foreach (int r in reference.GetViewBetween(from, int.MaxValue))
                    {
                        expected = r;
                        break;
                    }
                    Assert.Equal(expected, set.NextSetBit(from), "next from " + from);
                }
            }
            Assert.Equal(reference.Count, set.Count);
            var all = new List<int>();
            for (int s = set.NextSetBit(0); s >= 0; s = set.NextSetBit(s + 1)) all.Add(s);
            Assert.SequenceEqual(reference, all);
        }

        [Test]
        public void SetRangeMatchesSingleSets()
        {
            var random = new DeterministicRandom(3);
            for (int round = 0; round < 50; round++)
            {
                var range = new SlotBitSet(1 << 16);
                var single = new SlotBitSet(1 << 16);
                for (int k = 0; k < 20; k++)
                {
                    int item = random.Next(1 << 16);
                    range.Set(item);
                    single.Set(item);
                }
                int from = random.Next(1 << 16);
                int to = from + random.Next((1 << 16) - from + 1);
                range.SetRange(from, to);
                for (int i = from; i < to; i++) single.Set(i);
                Assert.Equal(single.Count, range.Count, "count after SetRange(" + from + ", " + to + ")");
                for (int s = single.NextSetBit(0); s >= 0; s = single.NextSetBit(s + 1)) Assert.True(range.Get(s));
                for (int s = range.NextSetBit(0); s >= 0; s = range.NextSetBit(s + 1)) Assert.True(single.Get(s));
            }
            var empty = new SlotBitSet(100);
            empty.SetRange(10, 10);
            Assert.Equal(0, empty.Count);
            Assert.Throws<ArgumentOutOfRangeException>(() => empty.SetRange(5, 101));
            Assert.Throws<ArgumentOutOfRangeException>(() => empty.SetRange(6, 5));
        }

        [Test]
        public void MemoryFollowsTheHighestItemAndResetReleasesIt()
        {
            var set = new SlotBitSet(1 << 24);
            set.Set(10);
            Assert.True(set.MemoryBytes < 1024, "a low item needs a few words, got " + set.MemoryBytes);
            set.Set((1 << 24) - 1);
            Assert.True(set.MemoryBytes <= (2L << 20) + (64 << 10), "the whole slot space needs about 2 MiB, got " + set.MemoryBytes);
            set.Reset();
            Assert.Equal(0, set.Count);
            Assert.Equal(0L, set.MemoryBytes);
            Assert.Equal(-1, set.NextSetBit(0));
        }
    }
}
