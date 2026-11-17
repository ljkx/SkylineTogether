using System;
using System.Collections.Generic;
using System.Reflection;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>
    /// A chain of <see cref="AgentReferrerIndex{TEntity}"/> that loops (a corrupt index) must make every walk throw, never
    /// spin the main thread forever (joined players saw minutes-long freezes in the reference audit sweep).
    /// </summary>
    public sealed class AgentReferrerIndexLoopGuardTests
    {
        private static AgentReferrerIndex<int> LoopedIndex()
        {
            var index = new AgentReferrerIndex<int>(16);
            index.Add(1, 10);
            index.Add(1, 11);
            index.Add(1, 12);
            // corrupt it: every node of the pool points at the chain's head, so the walk never reaches the end
            var next = (int[])typeof(AgentReferrerIndex<int>).GetField("_next", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(index);
            var heads = (Dictionary<int, int>)typeof(AgentReferrerIndex<int>).GetField("_heads", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(index);
            int head = heads[1];
            for (int i = 0; i < next.Length; i++) next[i] = head;
            return index;
        }

        [Test]
        public void EveryWalkOfALoopedChainThrowsInsteadOfSpinning()
        {
            Assert.Throws<InvalidOperationException>(() => LoopedIndex().Get(1, new List<int>()), "Get");
            Assert.Throws<InvalidOperationException>(() => LoopedIndex().Contains(1, 99), "Contains");
            Assert.Throws<InvalidOperationException>(() => LoopedIndex().Remove(1, 99), "Remove");
            Assert.Throws<InvalidOperationException>(() => LoopedIndex().Add(1, 99, _ => true), "Add");
            // RemoveTarget frees each node as it walks, which cuts this loop: it ends (guarded all the same)
            Assert.DoesNotThrow(() => LoopedIndex().RemoveTarget(1), "RemoveTarget");
            Assert.Throws<InvalidOperationException>(() => LoopedIndex().Sweep(_ => true, 16), "Sweep");
        }

        [Test]
        public void AHealthyIndexWalksAsBefore()
        {
            var index = new AgentReferrerIndex<int>(4);
            for (int r = 100; r < 200; r++) index.Add(1, r, _ => true); // grows past the pool and into a large chain
            var output = new List<int>();
            Assert.Equal(100, index.Get(1, output));
            Assert.True(index.Contains(1, 150));
            Assert.True(index.Remove(1, 150));
            Assert.False(index.Contains(1, 150));
            Assert.Equal(0, index.Sweep(_ => true, 16));
            Assert.Equal(99, index.RemoveTarget(1));
        }
    }
}
