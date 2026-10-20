using System.Collections.Generic;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>The referrer index stays bounded: despawned movers are removed, a background sweep prunes gone referrers.</summary>
    public sealed class ReferrerIndexGrowthTests
    {
        [Test]
        public void APairCanBeRemovedFromSmallAndLargeChains()
        {
            var index = new AgentReferrerIndex<int>();
            index.Add(1, 10);
            index.Add(1, 11);
            Assert.True(index.Remove(1, 10));
            Assert.False(index.Remove(1, 10));
            Assert.False(index.Contains(1, 10));
            Assert.True(index.Contains(1, 11));
            Assert.True(index.Remove(1, 11));
            Assert.Equal(0, index.Targets);
            for (int r = 100; r < 100 + AgentReferrerIndex<int>.LargeChain + 5; r++) index.Add(2, r);
            Assert.Equal(1, index.LargeTargets);
            Assert.True(index.Remove(2, 101));
            Assert.False(index.Contains(2, 101), "the large chain's set forgets it too");
            Assert.True(index.Add(2, 101), "and it can be added again");
        }

        [Test]
        public void TheSweepPrunesGoneReferrersAndTargetsInSlices()
        {
            var index = new AgentReferrerIndex<int>();
            var gone = new HashSet<int>();
            // a depot (target 1) and the vehicles it ever owned
            for (int v = 1000; v < 1200; v++) index.Add(1, v);
            for (int t = 2; t < 12; t++) index.Add(t, 5000 + t);
            for (int v = 1000; v < 1190; v++) gone.Add(v); // most of them are long gone
            gone.Add(5); // a gone target
            index.Add(5, 6000);
            bool Alive(int e) => !gone.Contains(e);
            int removed = 0;
            for (int i = 0; i < 12; i++) removed += index.Sweep(Alive, 1);
            Assert.Equal(192, removed, "190 gone vehicles and the gone target's two entries");
            Assert.Equal(10 + 9, index.Count);
            var left = new List<int>();
            index.Get(1, left);
            Assert.Equal(10, left.Count);
            Assert.Equal(0, index.Get(5, left = new List<int>()));
        }
    }
}
