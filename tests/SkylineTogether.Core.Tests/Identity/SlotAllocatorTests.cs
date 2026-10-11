using System;
using System.Collections.Generic;
using SkylineTogether.Core.Identity;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Identity
{
    /// <summary>Host slot allocation with deferred reuse (ARCH §3.1).</summary>
    public sealed class SlotAllocatorTests
    {
        private static NetSlot S(uint value) => new NetSlot(value);

        private static EpochId Ep(uint value) => new EpochId(value);

        [Test]
        public void FreshSlotsAreDenseFromOneWithGenerationZero()
        {
            var allocator = new SlotAllocator();
            Assert.Equal(1, allocator.HighWaterMark);
            for (uint i = 1; i <= 5; i++)
            {
                NetSlot slot = allocator.Allocate(out byte generation);
                Assert.Equal(S(i), slot);
                Assert.Equal((byte)0, generation);
                Assert.True(allocator.IsAllocated(slot));
            }
            Assert.Equal(6, allocator.HighWaterMark);
            Assert.Equal(5, allocator.AllocatedCount);
            Assert.Equal(0, allocator.FreeCount);
        }

        [Test]
        public void ReuseWaitsForTheMinimumAckAndTheLogTail()
        {
            var allocator = new SlotAllocator();
            NetSlot a = allocator.Allocate(out _);
            allocator.Allocate(out _);
            allocator.Release(a, Ep(10));
            Assert.Equal(1, allocator.PendingReleaseCount);
            Assert.True(allocator.IsPendingRelease(a));
            Assert.False(allocator.IsAllocated(a));

            Assert.Equal(S(3), allocator.Allocate(out _), "a pending slot is not reused");

            allocator.OnMinAckedEpoch(Ep(10));
            Assert.Equal(1, allocator.PendingReleaseCount, "acknowledged, but the log tail has not passed epoch 10");
            allocator.OnLogTail(Ep(10));
            Assert.Equal(1, allocator.PendingReleaseCount, "the tail must have passed the despawn epoch, not reached it");
            allocator.OnLogTail(Ep(11));
            Assert.Equal(0, allocator.PendingReleaseCount);
            Assert.Equal(1, allocator.FreeCount);

            NetSlot reused = allocator.Allocate(out byte generation);
            Assert.Equal(a, reused, "the lowest free slot comes first");
            Assert.Equal((byte)1, generation, "a reuse increments the generation");
            Assert.Equal((byte)1, allocator.GenerationOf(reused));
        }

        [Test]
        public void TheTailAloneDoesNotFreeASlot()
        {
            var allocator = new SlotAllocator();
            NetSlot a = allocator.Allocate(out _);
            allocator.Release(a, Ep(5));
            allocator.OnLogTail(Ep(100));
            allocator.OnMinAckedEpoch(Ep(4));
            Assert.Equal(1, allocator.PendingReleaseCount, "a peer has not acknowledged the despawn epoch");
            allocator.OnMinAckedEpoch(Ep(5));
            Assert.Equal(0, allocator.PendingReleaseCount);
        }

        [Test]
        public void ALowerMinimumAckPausesFreeing()
        {
            var allocator = new SlotAllocator();
            NetSlot a = allocator.Allocate(out _);
            NetSlot b = allocator.Allocate(out _);
            allocator.OnLogTail(Ep(50));
            allocator.OnMinAckedEpoch(Ep(20));
            allocator.Release(a, Ep(21));
            allocator.OnMinAckedEpoch(Ep(3));   // a peer joined with an older acknowledgement
            allocator.Release(b, Ep(22));
            Assert.Equal(2, allocator.PendingReleaseCount);
            allocator.OnMinAckedEpoch(Ep(21));
            Assert.Equal(1, allocator.PendingReleaseCount);
            Assert.False(allocator.IsPendingRelease(a));
            Assert.True(allocator.IsPendingRelease(b));
        }

        [Test]
        public void LowestFreeSlotFirstThenFreshSlots()
        {
            var allocator = new SlotAllocator();
            for (int i = 0; i < 8; i++) allocator.Allocate(out _);
            allocator.Release(S(6), Ep(1));
            allocator.Release(S(2), Ep(1));
            allocator.Release(S(4), Ep(2));
            allocator.OnMinAckedEpoch(Ep(2));
            allocator.OnLogTail(Ep(3));
            Assert.Equal(S(2), allocator.Allocate(out _));
            Assert.Equal(S(4), allocator.Allocate(out _));
            Assert.Equal(S(6), allocator.Allocate(out _));
            Assert.Equal(S(9), allocator.Allocate(out _));
        }

        [Test]
        public void OutOfOrderReleaseEpochsOnlyDelayReuse()
        {
            var allocator = new SlotAllocator();
            NetSlot a = allocator.Allocate(out _);
            NetSlot b = allocator.Allocate(out _);
            allocator.Release(a, Ep(30));
            allocator.Release(b, Ep(10));   // older epoch after a newer one: treated as epoch 30
            allocator.OnMinAckedEpoch(Ep(10));
            allocator.OnLogTail(Ep(11));
            Assert.Equal(2, allocator.PendingReleaseCount);
            allocator.OnMinAckedEpoch(Ep(30));
            allocator.OnLogTail(Ep(31));
            Assert.Equal(0, allocator.PendingReleaseCount);
        }

        [Test]
        public void InvalidReleasesAreIgnoredAndLogged()
        {
            var log = new ListLog();
            var allocator = new SlotAllocator(16, log);
            NetSlot a = allocator.Allocate(out _);
            allocator.Release(a, Ep(1));
            allocator.Release(a, Ep(1));            // already pending
            allocator.Release(S(500), Ep(1));       // never allocated
            allocator.Release(NetSlot.None, Ep(1));
            Assert.Equal(3L, allocator.InvalidReleaseCount);
            Assert.Equal(1, allocator.PendingReleaseCount, "a double release must never hand a slot out twice");
            Assert.Equal(3, log.Count(LogLevel.Warn));
            allocator.OnMinAckedEpoch(Ep(1));
            allocator.OnLogTail(Ep(2));
            allocator.Release(a, Ep(3));            // free, not allocated
            Assert.Equal(4L, allocator.InvalidReleaseCount);
            Assert.Equal(1, allocator.FreeCount);
        }

        [Test]
        public void GenerationsWrapAfter256Reuses()
        {
            var allocator = new SlotAllocator();
            NetSlot slot = allocator.Allocate(out byte generation);
            for (int i = 1; i <= 256; i++)
            {
                allocator.Release(slot, Ep((uint)i));
                allocator.OnMinAckedEpoch(Ep((uint)i));
                allocator.OnLogTail(Ep((uint)i + 1));
                NetSlot again = allocator.Allocate(out generation);
                Assert.Equal(slot, again);
                Assert.Equal((byte)(i & 0xFF), generation);
            }
            Assert.Equal((byte)0, generation);
        }

        [Test]
        public void RestoreRoundTripsBindingsGenerationsAndGaps()
        {
            var host = new SlotAllocator();
            for (int i = 0; i < 10; i++) host.Allocate(out _);
            host.Release(S(3), Ep(1));
            host.Release(S(7), Ep(1));
            host.OnMinAckedEpoch(Ep(1));
            host.OnLogTail(Ep(2));
            host.Allocate(out byte reusedGeneration);   // slot 3 again, generation 1
            Assert.Equal((byte)1, reusedGeneration);
            host.Release(S(9), Ep(5));                   // pending at save time

            var bound = new List<KeyValuePair<NetSlot, byte>>();
            for (uint s = 1; s < (uint)host.HighWaterMark; s++)
            {
                if (host.IsAllocated(S(s))) bound.Add(new KeyValuePair<NetSlot, byte>(S(s), host.GenerationOf(S(s))));
            }

            var restored = new SlotAllocator();
            restored.Restore(host.HighWaterMark, bound);
            Assert.Equal(host.HighWaterMark, restored.HighWaterMark);
            Assert.Equal(bound.Count, restored.AllocatedCount);
            Assert.Equal(0, restored.PendingReleaseCount, "nothing is pending after a load");
            foreach (KeyValuePair<NetSlot, byte> pair in bound)
            {
                Assert.True(restored.IsAllocated(pair.Key));
                Assert.Equal(pair.Value, restored.GenerationOf(pair.Key));
            }
            Assert.Equal((byte)1, restored.GenerationOf(S(3)));
            Assert.Equal(2, restored.FreeCount, "slots 7 and 9 are free gaps");
            Assert.Equal(S(7), restored.Allocate(out byte generation));
            Assert.Equal((byte)1, generation, "a gap of unknown history is reused with an incremented generation");
            Assert.Equal(S(9), restored.Allocate(out _));
            Assert.Equal(S(11), restored.Allocate(out _));
        }

        [Test]
        public void RestoreRepairsInconsistentInput()
        {
            var log = new ListLog();
            var allocator = new SlotAllocator(0, log);
            allocator.Allocate(out _);
            var bound = new List<KeyValuePair<NetSlot, byte>>
            {
                new KeyValuePair<NetSlot, byte>(S(40), 7),
                new KeyValuePair<NetSlot, byte>(NetSlot.None, 1),
                new KeyValuePair<NetSlot, byte>(S(40), 9),
                new KeyValuePair<NetSlot, byte>(S(2), 0)
            };
            allocator.Restore(5, bound);   // the saved mark is below the highest bound slot
            Assert.Equal(41, allocator.HighWaterMark);
            Assert.Equal(2, allocator.AllocatedCount);
            Assert.Equal((byte)7, allocator.GenerationOf(S(40)), "the first of two duplicate entries wins");
            Assert.Equal(38, allocator.FreeCount);
            Assert.Equal(1, log.Count(LogLevel.Warn));
            Assert.Throws<ArgumentNullException>(() => allocator.Restore(1, null));

            allocator.Restore(0, new List<KeyValuePair<NetSlot, byte>>());
            Assert.Equal(1, allocator.HighWaterMark);
            Assert.Equal(S(1), allocator.Allocate(out _));
        }

        [Test(TimeoutSeconds = 120)]
        public void ExhaustionIsReportedNotWrapped()
        {
            var allocator = new SlotAllocator();
            const uint limit = NetSlot.MaxValue;
            allocator.Restore((int)limit - 1, Bound(1, limit - 2));
            Assert.Equal(S(limit - 1), allocator.Allocate(out _));
            Assert.Equal(S(limit), allocator.Allocate(out _));
            Assert.False(allocator.TryAllocate(out NetSlot none, out byte generation));
            Assert.True(none.IsNone);
            Assert.Equal((byte)0, generation);
            Assert.Throws<InvalidOperationException>(() => allocator.Allocate(out _));
            Assert.Equal((int)limit + 1, allocator.HighWaterMark);
        }

        [Test]
        public void ClearForgetsEverything()
        {
            var allocator = new SlotAllocator();
            allocator.Allocate(out _);
            allocator.Release(S(1), Ep(1));
            allocator.Clear();
            Assert.Equal(0, allocator.AllocatedCount);
            Assert.Equal(0, allocator.PendingReleaseCount);
            Assert.Equal(1, allocator.HighWaterMark);
            Assert.Equal(EpochId.None, allocator.MinAckedEpoch);
            Assert.Equal(S(1), allocator.Allocate(out byte generation));
            Assert.Equal((byte)0, generation);
        }

        [Test]
        public void AllocatorAndTableTogetherNeverServeAStaleBinding()
        {
            // the host path: allocate + bind, despawn = unbind + release, acks, reuse with a new generation
            var allocator = new SlotAllocator();
            var table = new SlotTable();
            NetSlot slot = allocator.Allocate(out byte generation);
            table.Bind(slot, generation, new EntityKey(5, 1), Family.Object, new PrefabRank(3), TypeSetId.None, spawnEmitted: false);
            ushort revisionBefore = table.RevisionOf(slot);
            table.Unbind(slot);
            allocator.Release(slot, Ep(7));
            allocator.OnMinAckedEpoch(Ep(7));
            allocator.OnLogTail(Ep(8));
            NetSlot again = allocator.Allocate(out byte generation2);
            table.Bind(again, generation2, new EntityKey(9, 1), Family.Mover, new PrefabRank(4), TypeSetId.None, spawnEmitted: false);
            Assert.Equal(slot, again);
            Assert.NotEqual(generation, generation2);
            Assert.NotEqual(revisionBefore, table.RevisionOf(again), "a proposal based on the old occupant is stale");
            Assert.False(table.TryGetSlot(new EntityKey(5, 1), out _));
        }

        [Test]
        public void RandomOperationsNeverHandOutAnAllocatedOrPendingSlot()
        {
            var random = new DeterministicRandom(99);
            var allocator = new SlotAllocator(64);
            var allocated = new HashSet<uint>();
            var pending = new Dictionary<uint, uint>();   // slot -> despawn epoch
            uint epoch = 1;
            uint minAcked = 0;
            uint tail = 0;
            for (int step = 0; step < 20000; step++)
            {
                switch (random.Next(4))
                {
                    case 0:
                    case 1:
                    {
                        NetSlot slot = allocator.Allocate(out _);
                        Assert.False(allocated.Contains(slot.Value), "slot " + slot + " handed out twice");
                        Assert.False(pending.ContainsKey(slot.Value), "slot " + slot + " reused before its release completed");
                        allocated.Add(slot.Value);
                        break;
                    }
                    case 2:
                        if (allocated.Count > 0)
                        {
                            uint victim = 0;
                            foreach (uint s in allocated)
                            {
                                victim = s;
                                if (random.Next(4) == 0) break;
                            }
                            allocated.Remove(victim);
                            pending[victim] = epoch;
                            allocator.Release(new NetSlot(victim), new EpochId(epoch));
                        }
                        break;
                    default:
                        epoch++;
                        uint ackLag = (uint)random.Next(5);
                        uint tailLag = (uint)random.Next(5);
                        if (random.NextBool()) minAcked = Math.Max(minAcked, epoch > ackLag ? epoch - ackLag : 0);
                        if (random.NextBool()) tail = Math.Max(tail, epoch > tailLag ? epoch - tailLag : 0);
                        allocator.OnMinAckedEpoch(new EpochId(minAcked));
                        allocator.OnLogTail(new EpochId(tail));
                        var freed = new List<uint>();
                        foreach (KeyValuePair<uint, uint> pair in pending)
                        {
                            if (pair.Value <= minAcked && pair.Value < tail) freed.Add(pair.Key);
                        }
                        foreach (uint s in freed) pending.Remove(s);
                        break;
                }
                Assert.Equal(allocated.Count, allocator.AllocatedCount);
                Assert.Equal(pending.Count, allocator.PendingReleaseCount);
            }
        }

        private static IEnumerable<KeyValuePair<NetSlot, byte>> Bound(uint first, uint last)
        {
            for (uint s = first; s <= last; s++) yield return new KeyValuePair<NetSlot, byte>(new NetSlot(s), 0);
        }
    }
}
