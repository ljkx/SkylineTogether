using System;
using System.Collections.Generic;
using SkylineTogether.Core.Scheduling;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Scheduling
{
    /// <summary>w2/simvals: attention planner (priorities, budgets, joins), spatial grid, inspection cadence, peer budgets.</summary>
    public sealed class AttentionSchedulingTests
    {
        private static readonly PeerId A = new PeerId(1);
        private static readonly PeerId B = new PeerId(2);

        private static List<uint> DrainAll(AttentionPlanner p, PeerId peer, int budget, int bytesPerEntity = 10)
        {
            var sent = new List<uint>();
            p.Drain(peer, budget, id =>
            {
                sent.Add(id);
                return bytesPerEntity;
            });
            return sent;
        }

        [Test]
        public void InViewEntitiesGoFirstThenOldestChanges()
        {
            var p = new AttentionPlanner();
            p.AddPeer(A, p.CurrentStamp);
            p.MarkChanged(5);
            p.MarkChanged(3);
            p.MarkChanged(9);
            p.MarkChanged(7);
            p.SetView(A, new uint[] { 9, 7, 100 });
            List<uint> sent = DrainAll(p, A, 1000);
            Assert.SequenceEqual(new uint[] { 7, 9, 5, 3 }, sent);
            Assert.Equal(0, p.DirtyCount(A));
            Assert.Equal(0, DrainAll(p, A, 1000).Count);
        }

        [Test]
        public void BudgetStopsAndResumesWithoutLosingAnything()
        {
            var p = new AttentionPlanner();
            p.AddPeer(A, 0);
            for (uint id = 1; id <= 50; id++) p.MarkChanged(id);
            var all = new List<uint>();
            for (int round = 0; round < 20 && p.DirtyCount(A) > 0; round++) all.AddRange(DrainAll(p, A, 45));
            Assert.Equal(50, all.Count);
            for (uint id = 1; id <= 50; id++) Assert.Equal(id, all[(int)id - 1], "oldest first");
        }

        [Test]
        public void NoRoomKeepsTheEntityDirty()
        {
            var p = new AttentionPlanner();
            p.AddPeer(A, 0);
            p.MarkChanged(1);
            p.MarkChanged(2);
            int calls = 0;
            int used = p.Drain(A, 100, id => ++calls == 1 ? 10 : AttentionPlanner.NoRoom);
            Assert.Equal(10, used);
            Assert.False(p.IsDirty(A, 1));
            Assert.True(p.IsDirty(A, 2));
            Assert.SequenceEqual(new uint[] { 2 }, DrainAll(p, A, 100));
        }

        [Test]
        public void GoneEntitiesAreDroppedWithoutCost()
        {
            var p = new AttentionPlanner();
            p.AddPeer(A, 0);
            p.MarkChanged(1);
            p.MarkChanged(2);
            int used = p.Drain(A, 100, id => id == 1 ? AttentionPlanner.Gone : 10);
            Assert.Equal(10, used);
            Assert.Equal(0, p.DirtyCount(A));
        }

        [Test]
        public void SelectedEntityIsWithheldUntilDeselected()
        {
            var p = new AttentionPlanner();
            p.AddPeer(A, 0);
            p.SetView(A, new uint[] { 4 });
            p.MarkChanged(4);
            p.MarkChanged(8);
            p.SetSelected(A, 4);
            Assert.SequenceEqual(new uint[] { 8 }, DrainAll(p, A, 1000));
            Assert.True(p.IsDirty(A, 4), "the selected entity stays dirty");
            Assert.Equal(0, DrainAll(p, A, 1000).Count);
            p.SetSelected(A, 0);
            Assert.SequenceEqual(new uint[] { 4 }, DrainAll(p, A, 1000));
        }

        [Test]
        public void PeersAreIndependent()
        {
            var p = new AttentionPlanner();
            p.AddPeer(A, 0);
            p.AddPeer(B, 0);
            p.MarkChanged(1);
            Assert.SequenceEqual(new uint[] { 1 }, DrainAll(p, A, 100));
            Assert.True(p.IsDirty(B, 1));
            p.RemovePeer(B);
            Assert.False(p.HasPeer(B));
            Assert.Equal(0, DrainAll(p, B, 100).Count);
        }

        [Test]
        public void JoinerGetsOnlyWhatChangedAfterItsSnapshot()
        {
            var p = new AttentionPlanner();
            p.MarkChanged(1);
            p.MarkChanged(2);
            long snapshot = p.CurrentStamp;
            p.MarkChanged(3);
            p.MarkChanged(1);
            p.AddPeer(A, snapshot);
            List<uint> sent = DrainAll(p, A, 1000);
            sent.Sort();
            Assert.SequenceEqual(new uint[] { 1, 3 }, sent);
        }

        [Test]
        public void ForgetRemovesEverywhere()
        {
            var p = new AttentionPlanner();
            p.AddPeer(A, 0);
            p.SetView(A, new uint[] { 1 });
            p.MarkChanged(1);
            p.MarkChanged(2);
            p.Forget(1);
            Assert.Equal(0, p.ViewDirtyCount(A));
            Assert.SequenceEqual(new uint[] { 2 }, DrainAll(p, A, 100));
            Assert.Equal(1, p.TrackedCount);
        }

        [Test]
        public void RepeatedChangesAreSentOnce()
        {
            var p = new AttentionPlanner();
            p.AddPeer(A, 0);
            for (int i = 0; i < 10; i++) p.MarkChanged(6);
            Assert.SequenceEqual(new uint[] { 6 }, DrainAll(p, A, 1000));
        }

        [Test]
        public void RandomisedPlannerNeverLosesAChange()
        {
            var random = new DeterministicRandom(9101);
            var p = new AttentionPlanner();
            p.AddPeer(A, 0);
            var expected = new HashSet<uint>();
            for (int step = 0; step < 5000; step++)
            {
                int op = random.Next(10);
                if (op < 6)
                {
                    uint id = (uint)(1 + random.Next(200));
                    p.MarkChanged(id);
                    expected.Add(id);
                }
                else if (op < 7)
                {
                    var view = new List<uint>();
                    for (int i = 0; i < 20; i++) view.Add((uint)(1 + random.Next(200)));
                    p.SetView(A, view);
                }
                else
                {
                    foreach (uint id in DrainAll(p, A, random.Next(100))) expected.Remove(id);
                }
            }
            foreach (uint id in DrainAll(p, A, int.MaxValue)) expected.Remove(id);
            Assert.Equal(0, expected.Count);
        }

        // ------------------------------------------------------------------ grid

        [Test]
        public void GridQueriesByRadiusAndFollowsMoves()
        {
            var g = new SpatialHashGrid(100f);
            g.Set(1, 0f, 0f);
            g.Set(2, 150f, 0f);
            g.Set(3, -1000f, 500f);
            g.Set(4, 99.9f, 0.1f);
            var found = new List<uint>();
            g.Query(0f, 0f, 100f, found);
            found.Sort();
            Assert.SequenceEqual(new uint[] { 1, 4 }, found);
            g.Set(3, 10f, 10f);
            g.Remove(4);
            found.Clear();
            g.Query(0f, 0f, 100f, found);
            found.Sort();
            Assert.SequenceEqual(new uint[] { 1, 3 }, found);
            Assert.Equal(3, g.Count);
        }

        [Test]
        public void GridMatchesBruteForce()
        {
            var random = new DeterministicRandom(9102);
            var g = new SpatialHashGrid(64f);
            var points = new Dictionary<uint, (float X, float Z)>();
            for (int i = 0; i < 3000; i++)
            {
                uint id = (uint)(1 + random.Next(800));
                if (random.Next(10) == 0)
                {
                    g.Remove(id);
                    points.Remove(id);
                    continue;
                }
                float x = (float)(random.NextDouble() * 4000 - 2000), z = (float)(random.NextDouble() * 4000 - 2000);
                g.Set(id, x, z);
                points[id] = (x, z);
            }
            for (int q = 0; q < 50; q++)
            {
                float cx = (float)(random.NextDouble() * 4000 - 2000), cz = (float)(random.NextDouble() * 4000 - 2000);
                float r = (float)(random.NextDouble() * (q % 5 == 0 ? 20000 : 600));
                var found = new List<uint>();
                g.Query(cx, cz, r, found);
                found.Sort();
                var brute = new List<uint>();
                foreach (KeyValuePair<uint, (float X, float Z)> pair in points)
                {
                    double dx = pair.Value.X - cx, dz = pair.Value.Z - cz;
                    if (dx * dx + dz * dz <= (double)r * r) brute.Add(pair.Key);
                }
                brute.Sort();
                Assert.SequenceEqual(brute, found, "query " + q);
            }
        }

        [Test]
        public void GridIgnoresNonFiniteInput()
        {
            var g = new SpatialHashGrid(10f);
            g.Set(1, float.NaN, 0f);
            Assert.Equal(0, g.Count);
            g.Set(2, 1f, 1f);
            var found = new List<uint>();
            Assert.Equal(0, g.Query(float.NaN, 0f, 10f, found));
            Assert.Equal(1, g.Query(0f, 0f, float.PositiveInfinity, found));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SpatialHashGrid(0f));
        }

        // ------------------------------------------------------------------ inspection cadence

        [Test]
        public void CadenceCrossesEveryMultipleOf256()
        {
            Assert.Equal(512u, InspectionCadence.SampleTick(767));
            Assert.True(InspectionCadence.Crossed(255, 256, out uint t));
            Assert.Equal(256u, t);
            Assert.False(InspectionCadence.Crossed(256, 511, out _));
            Assert.True(InspectionCadence.Crossed(511, 1100, out t));
            Assert.Equal(1024u, t);
            Assert.False(InspectionCadence.Crossed(700, 700, out _));
            Assert.True(InspectionCadence.Crossed(0xFFFFFFF0u, 0x10u, out t), "wraps");
            Assert.Equal(0u, t);
            Assert.True(InspectionCadence.Crossed(5000, 100, out t), "a clock that went back resynchronises");
            Assert.Equal(0u, t);
        }

        [Test]
        public void SamplesWaitForTheLocalClock()
        {
            var q = new InspectionSampleQueue<string>();
            q.Enqueue(1024, 10.0, "a");
            q.Enqueue(1280, 10.1, "b");
            Assert.False(q.TryDequeueReady(1000, 10.2, out _, out _));
            Assert.True(q.TryDequeueReady(1024, 10.3, out string s, out uint tick));
            Assert.Equal("a", s);
            Assert.Equal(1024u, tick);
            Assert.False(q.TryDequeueReady(1100, 10.4, out _, out _));
            Assert.True(q.TryDequeueReady(1100, 13.2, out s, out _), "held at most MaxHoldSeconds");
            Assert.Equal("b", s);
            q.Enqueue(100000, 20.0, "far");
            Assert.True(q.TryDequeueReady(1000, 20.0, out s, out _), "far ahead: a clock discontinuity");
            q.Enqueue(0xFFFFFFF8u, 30.0, "wrap");
            Assert.True(q.TryDequeueReady(0x10u, 30.0, out s, out _), "a tick just before the wrap is in the past");
            Assert.Equal("wrap", s);
            Assert.Equal(0, q.Count);
        }

        [Test]
        public void SampleQueueIsBounded()
        {
            var q = new InspectionSampleQueue<int>();
            for (int i = 0; i < InspectionSampleQueue<int>.MaxPending + 5; i++) q.Enqueue((uint)(i * 256), 0, i);
            Assert.Equal(InspectionSampleQueue<int>.MaxPending, q.Count);
            Assert.Equal(5L, q.Dropped);
            Assert.True(q.TryDequeueReady(uint.MaxValue / 2, 0, out int first, out _));
            Assert.Equal(5, first);
        }

        // ------------------------------------------------------------------ peer budget

        [Test]
        public void BudgetRefillsAndRespectsBacklog()
        {
            var b = new PeerByteBudget(1000, 2000, 5000);
            Assert.Equal(2000, b.Allowance(0, 0), "starts full");
            b.Spend(2000);
            Assert.Equal(0, b.Allowance(0, 0));
            Assert.Equal(500, b.Allowance(0.5, 0));
            Assert.Equal(0, b.Allowance(0.5, 6000), "backlog above the limit");
            Assert.Equal(1000, b.Allowance(0, 0), "the refill during the backlog is kept");
            b.Spend(1500);
            Assert.Equal(0, b.Allowance(0.2, 0), "debt delays the next send");
            b.Boost = 2f;
            Assert.Equal(700, b.Allowance(0.5, 0), "a debt of 300 plus 1000 at the doubled rate");
            b.Boost = float.NaN;
            Assert.Equal(1f, b.Boost);
        }
    }
}
