using System;
using System.Collections.Generic;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>
    /// Model-based test of <see cref="NetIdAllocator"/>: random sequences of allocations, grants, claims, releases
    /// (valid and invalid), seals, acknowledgements with join barriers, flushes and restores are applied both to the
    /// allocator and to a plain reference model, and after every step both must agree on every id's state, on the
    /// counts and on which id is handed out next. The safety properties behind it: no id is ever handed out while it is
    /// allocated or quarantined, and a released id comes back only after its frame was sealed, acknowledged by every
    /// peer, below the join barrier and old enough.
    /// </summary>
    public sealed class NetIdAllocatorModelTests
    {
        private sealed class Model
        {
            public readonly HashSet<uint> Allocated = new HashSet<uint>();
            public readonly HashSet<uint> Quarantined = new HashSet<uint>();
            public readonly SortedSet<uint> Free = new SortedSet<uint>();
            public readonly List<uint> Open = new List<uint>();
            public readonly Queue<(uint Id, uint Frame, double At)> Sealed = new Queue<(uint, uint, double)>();
            public readonly Dictionary<uint, byte> Generation = new Dictionary<uint, byte>();
            public uint HighWater = 1;
            public uint Frame;
            public double MinQuarantine = NetIdAllocator.DefaultMinQuarantineSeconds;

            public byte Gen(uint id) => Generation.TryGetValue(id, out byte g) ? g : (byte)0;

            public uint Take()
            {
                if (Free.Count > 0)
                {
                    uint id = Free.Min;
                    Free.Remove(id);
                    Allocated.Add(id);
                    Generation[id] = unchecked((byte)(Gen(id) + 1));
                    return id;
                }
                uint fresh = HighWater++;
                Allocated.Add(fresh);
                return fresh;
            }
        }

        private static void AssertAgree(NetIdAllocator a, Model m, string step)
        {
            Assert.Equal(m.Allocated.Count, a.AllocatedCount, step + ": allocated count");
            Assert.Equal(m.Free.Count, a.FreeCount, step + ": free count");
            Assert.Equal(m.Quarantined.Count, a.QuarantinedCount, step + ": quarantined count");
            Assert.Equal(m.HighWater, a.Next, step + ": next fresh id");
            for (uint id = 1; id < m.HighWater + 3; id++)
            {
                bool allocated = m.Allocated.Contains(id), quarantined = m.Quarantined.Contains(id);
                bool free = m.Free.Contains(id) || id >= m.HighWater;
                Assert.Equal(allocated, a.IsAllocated(id), step + ": allocated #" + id);
                Assert.Equal(quarantined, a.IsQuarantined(id), step + ": quarantined #" + id);
                Assert.Equal(free, a.IsFree(id), step + ": free #" + id);
                Assert.True((allocated ? 1 : 0) + (quarantined ? 1 : 0) + (free ? 1 : 0) == 1, step + ": #" + id + " is in exactly one state");
                Assert.Equal(m.Gen(id), a.GenerationOf(id), step + ": generation #" + id);
            }
        }

        [Test(TimeoutSeconds = 120)]
        public void TheAllocatorAgreesWithAReferenceModelUnderRandomOperations()
        {
            Prop.ForAll("net id model", 1600, 70, g =>
            {
                var a = new NetIdAllocator();
                var m = new Model();
                double now = 0;
                for (int stepIndex = 0; stepIndex < 250; stepIndex++)
                {
                    now += g.Double() * 2;
                    string step = "step " + stepIndex;
                    switch (g.Int(10))
                    {
                        case 0:
                        case 1:
                        {
                            uint expected = m.Take();
                            Assert.False(m.Quarantined.Contains(expected));
                            uint id = a.Allocate();
                            Assert.Equal(expected, id, step + ": allocate");
                            break;
                        }
                        case 2:
                        {
                            uint count = (uint)g.Pick(1, 3, 10, 40);
                            var runs = new List<(uint Start, uint Count)>();
                            uint granted = a.Grant(count, runs);
                            Assert.Equal(count, granted, step + ": grant");
                            Assert.True(runs.Count <= NetIdAllocator.MaxGrantRuns + 1, "few runs");
                            uint total = 0;
                            foreach ((uint start, uint n) in runs)
                            {
                                for (uint id = start; id < start + n; id++)
                                {
                                    Assert.False(m.Allocated.Contains(id) || m.Quarantined.Contains(id), step + ": granted #" + id + " was in use");
                                    if (m.Free.Remove(id)) m.Generation[id] = unchecked((byte)(m.Gen(id) + 1));
                                    else
                                    {
                                        Assert.Equal(m.HighWater, id, step + ": fresh ids are contiguous from the high-water mark");
                                        m.HighWater++;
                                    }
                                    m.Allocated.Add(id);
                                }
                                total += n;
                            }
                            Assert.Equal(granted, total);
                            break;
                        }
                        case 3:
                        {
                            uint id = (uint)g.Int(0, (int)m.HighWater + 8);
                            bool expected = id != 0 && !m.Allocated.Contains(id) && !m.Quarantined.Contains(id);
                            Assert.Equal(expected, a.Claim(id), step + ": claim #" + id);
                            if (expected)
                            {
                                if (id >= m.HighWater)
                                {
                                    for (uint k = m.HighWater; k < id; k++) m.Free.Add(k);
                                    m.HighWater = id + 1;
                                }
                                else m.Free.Remove(id);
                                m.Allocated.Add(id);
                            }
                            break;
                        }
                        case 4:
                        case 5:
                        {
                            // mostly valid releases, sometimes double or unknown ones
                            uint id = m.Allocated.Count > 0 && !g.OneIn(5) ? Element(g, m.Allocated) : (uint)g.Int(0, (int)m.HighWater + 3);
                            bool expected = m.Allocated.Contains(id);
                            Assert.Equal(expected, a.Release(id), step + ": release #" + id);
                            if (expected)
                            {
                                m.Allocated.Remove(id);
                                m.Quarantined.Add(id);
                                m.Open.Add(id);
                            }
                            break;
                        }
                        case 6:
                        {
                            uint frame = g.OneIn(8) ? (uint)Math.Max(0, (int)m.Frame - 1) : m.Frame + (uint)g.Int(0, 3);
                            a.Seal(frame, now);
                            if (frame < m.Frame) frame = m.Frame;
                            m.Frame = frame;
                            foreach (uint id in m.Open) m.Sealed.Enqueue((id, frame, now));
                            m.Open.Clear();
                            break;
                        }
                        case 7:
                        case 8:
                        {
                            uint minAcked = (uint)g.Int(0, (int)m.Frame + 2);
                            uint barrier = g.OneIn(3) ? (uint)g.Int(1, (int)m.Frame + 2) : 0;
                            int expected = 0;
                            while (m.Sealed.Count > 0)
                            {
                                (uint id, uint frame, double at) = m.Sealed.Peek();
                                if (frame > minAcked || (barrier != 0 && frame >= barrier) || now - at < m.MinQuarantine) break;
                                m.Sealed.Dequeue();
                                m.Quarantined.Remove(id);
                                m.Free.Add(id);
                                expected++;
                            }
                            Assert.Equal(expected, a.Update(minAcked, barrier, now), step + ": update");
                            break;
                        }
                        default:
                            if (g.OneIn(4))
                            {
                                int n = m.Quarantined.Count;
                                foreach (uint id in m.Quarantined) m.Free.Add(id);
                                m.Quarantined.Clear();
                                m.Open.Clear();
                                m.Sealed.Clear();
                                Assert.Equal(n, a.FlushQuarantine(), step + ": flush");
                            }
                            else if (g.OneIn(3))
                            {
                                // a save/load round trip: bound ids and generations survive, the rest becomes free
                                var bound = new List<KeyValuePair<uint, byte>>();
                                foreach (uint id in m.Allocated) bound.Add(new KeyValuePair<uint, byte>(id, m.Gen(id)));
                                bound.Add(new KeyValuePair<uint, byte>(0, 1));                   // skipped: invalid
                                if (bound.Count > 1) bound.Add(bound[0]);                        // skipped: duplicate
                                int skipped = a.Restore(m.HighWater, bound);
                                Assert.Equal(bound.Count > 2 ? 2 : 1, skipped, step + ": restore skipped");
                                m.Quarantined.Clear();
                                m.Open.Clear();
                                m.Sealed.Clear();
                                m.Free.Clear();
                                m.Frame = 0;                       // a load starts the release frames again
                                for (uint id = 1; id < m.HighWater; id++)
                                {
                                    if (!m.Allocated.Contains(id)) m.Free.Add(id);
                                }
                                var kept = new Dictionary<uint, byte>();
                                foreach (uint id in m.Allocated) kept[id] = m.Gen(id);
                                m.Generation.Clear();
                                foreach (KeyValuePair<uint, byte> kv in kept) m.Generation[kv.Key] = kv.Value;
                            }
                            break;
                    }
                    AssertAgree(a, m, step);
                }
                Assert.Equal(0L, a.ExhaustedCount);
            });
        }

        private static uint Element(Gen g, HashSet<uint> set)
        {
            int k = g.Int(set.Count);
            foreach (uint id in set)
            {
                if (k-- == 0) return id;
            }
            throw new InvalidOperationException();
        }
    }
}
