using System;
using System.Collections.Generic;
using SkylineTogether.Core.Scheduling;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Transport
{
    /// <summary>Per-peer deferred updates (ARCHITECTURE.md#deferred-values-and-link-pacing: congestion becomes staleness, not backlog).</summary>
    public sealed class CoalescingQueueTests
    {
        [Test]
        public void RepeatedMarksOfOneIdCoalesceAndKeepTheirPlace()
        {
            var q = new CoalescingQueue();
            q.Mark(7, DeferredClass.Values, 1.0);
            q.Mark(8, DeferredClass.Values, 2.0);
            for (int i = 0; i < 100; i++) q.Mark(7, DeferredClass.Values, 3.0 + i);
            Assert.Equal(2, q.Count, "bounded by distinct ids, not by updates");
            Assert.Equal(100L, q.CoalescedCount);
            Assert.True(q.TryTake(out uint id, out DeferredClass cls, out double since));
            Assert.Equal(7u, id, "oldest first: the id keeps the place of its first mark");
            Assert.Equal(DeferredClass.Values, cls);
            Assert.Equal(1.0, since);
            Assert.True(q.TryTake(out id, out _, out _));
            Assert.Equal(8u, id);
            Assert.False(q.TryTake(out _, out _, out _));
        }

        [Test]
        public void AStrongerClassAbsorbsAWeakerOne()
        {
            var q = new CoalescingQueue();
            q.Mark(1, DeferredClass.Values, 0);
            q.Mark(1, DeferredClass.Upsert, 0);
            q.Mark(1, DeferredClass.Values, 0);
            Assert.True(q.TryGetClass(1, out DeferredClass cls));
            Assert.Equal(DeferredClass.Upsert, cls);
            Assert.False(q.RemoveIfAtMost(1, DeferredClass.Values), "a waiting upsert is not dropped by a values record");
            Assert.True(q.RemoveIfAtMost(1, DeferredClass.Upsert));
            Assert.False(q.Contains(1));
        }

        [Test]
        public void RemovedIdsAreSkippedAndCanComeBackAtTheEnd()
        {
            var q = new CoalescingQueue();
            q.Mark(1, DeferredClass.Values, 0);
            q.Mark(2, DeferredClass.Values, 1);
            q.Mark(3, DeferredClass.Values, 2);
            Assert.True(q.Remove(1));
            Assert.False(q.Remove(1));
            Assert.Equal(1.0, q.OldestSince(-1));
            q.Mark(1, DeferredClass.Values, 3); // despawned and spawned again: a new place in line
            var order = new List<uint>();
            while (q.TryTake(out uint id, out _, out _)) order.Add(id);
            Assert.SequenceEqual(new uint[] { 2, 3, 1 }, order);
            Assert.Equal(-1.0, q.OldestSince(-1));
        }

        [Test]
        public void ChurnDoesNotGrowTheQueue()
        {
            var q = new CoalescingQueue();
            for (int i = 0; i < 200_000; i++)
            {
                q.Mark((uint)(i % 10), DeferredClass.Values, i);
                q.Remove((uint)(i % 10));
            }
            Assert.Equal(0, q.Count);
            Assert.False(q.TryTake(out _, out _, out _));
        }

        [Test]
        public void MatchesAReferenceModel()
        {
            var q = new CoalescingQueue();
            var model = new List<(uint Id, DeferredClass Class)>();
            var random = new DeterministicRandom(5);
            for (int step = 0; step < 30_000; step++)
            {
                uint id = (uint)random.Next(1, 200);
                int action = random.Next(10);
                if (action < 6)
                {
                    var cls = random.Next(4) == 0 ? DeferredClass.Upsert : DeferredClass.Values;
                    int at = model.FindIndex(e => e.Id == id);
                    if (at < 0) model.Add((id, cls));
                    else if (cls > model[at].Class) model[at] = (id, cls);
                    q.Mark(id, cls, step);
                }
                else if (action < 8)
                {
                    int at = model.FindIndex(e => e.Id == id);
                    Assert.Equal(at >= 0, q.Remove(id));
                    if (at >= 0) model.RemoveAt(at);
                }
                else
                {
                    bool any = q.TryTake(out uint got, out DeferredClass cls, out _);
                    Assert.Equal(model.Count > 0, any);
                    if (!any) continue;
                    Assert.Equal(model[0].Id, got);
                    Assert.Equal(model[0].Class, cls);
                    model.RemoveAt(0);
                }
                Assert.Equal(model.Count, q.Count);
            }
        }

        [Test]
        public void DeferredBytesPaceTheLinkAndStopAtTheBacklogLimit()
        {
            var config = new LinkBudgetConfig();
            // 300 KB/s link, idle: one frame's worth at that rate
            int idle = LinkBudget.DeferredBytes(0, 0, 300_000, 1 / 60.0, config);
            Assert.Equal(5000, idle);
            // 0.25 s of backlog at 300 KB/s is 75,000 bytes: nothing more once it is reached
            Assert.Equal(0, LinkBudget.DeferredBytes(50_000, 25_000, 300_000, 1 / 60.0, config));
            Assert.Equal(1000, LinkBudget.DeferredBytes(50_000, 24_000, 300_000, 1 / 60.0, config), "only up to the allowance");
            // unknown rate: the assumed rate; tiny rate: at least the minimum backlog may be in flight
            Assert.True(LinkBudget.DeferredBytes(0, 0, 0, 1 / 60.0, config) >= config.MinFrameBytes);
            Assert.Equal(config.MinBacklogBytes, LinkBudget.AllowedBacklog(1000, config));
            Assert.Equal(config.MaxBacklogBytes, LinkBudget.AllowedBacklog(100_000_000, config), "a fast link still keeps the outbox short");
            Assert.Throws<ArgumentNullException>(() => LinkBudget.DeferredBytes(0, 0, 0, 0.01, null));
        }

        [Test]
        public void CongestionIsBacklogInSeconds()
        {
            var config = new LinkBudgetConfig();
            Assert.False(LinkBudget.IsCongested(0, 100_000, 300_000, config), "a third of a second");
            Assert.True(LinkBudget.IsCongested(100_000, 100_000, 300_000, config), "two thirds of a second");
            Assert.False(LinkBudget.IsCongested(20_000, 0, 1000, config), "slow links may keep a small backlog");
            Assert.True(LinkBudget.IsCongested(40_000, 0, 1000, config));
        }
    }
}
