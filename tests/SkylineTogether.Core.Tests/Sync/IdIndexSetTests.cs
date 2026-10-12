using System;
using System.Collections.Generic;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>w2/ids: the per-family index set that replaces scans over the whole id table.</summary>
    public sealed class IdIndexSetTests
    {
        [Test]
        public void AddRemoveContains()
        {
            var set = new IdIndexSet();
            Assert.True(set.Add(5));
            Assert.False(set.Add(5));
            Assert.True(set.Add(1000));
            Assert.True(set.Contains(5) && set.Contains(1000));
            Assert.False(set.Contains(6));
            Assert.False(set.Contains(1 << 27), "beyond the table: not a member, no growth");
            Assert.Equal(2, set.Count);
            Assert.True(set.Remove(5));
            Assert.False(set.Remove(5));
            Assert.Equal(1, set.Count);
            Assert.Equal(1000u, set[0]);
            Assert.Throws<ArgumentOutOfRangeException>(() => { uint _ = set[1]; });
            Assert.Throws<ArgumentOutOfRangeException>(() => set.Add(1u << 28));
        }

        [Test]
        public void MatchesAHashSetUnderRandomOperations()
        {
            var set = new IdIndexSet();
            var model = new HashSet<uint>();
            var random = new DeterministicRandom(99);
            for (int i = 0; i < 20_000; i++)
            {
                uint id = (uint)random.Next(1, 3000);
                if (random.Next(3) == 0) Assert.Equal(model.Remove(id), set.Remove(id));
                else Assert.Equal(model.Add(id), set.Add(id));
                Assert.Equal(model.Count, set.Count);
            }
            var copy = new List<uint>();
            set.CopyTo(copy);
            Assert.Equal(model.Count, copy.Count);
            foreach (uint id in copy) Assert.True(model.Contains(id));
            set.Clear();
            Assert.Equal(0, set.Count);
            foreach (uint id in model) Assert.False(set.Contains(id));
        }
    }
}
