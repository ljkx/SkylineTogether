using System;
using System.Collections.Generic;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>
    /// The id reuse protocol under the failures the reuse rules have to survive, with the real allocator, ack book,
    /// tracker, staged releases and held value queue: host frames whose batch is lost before the broadcast (a capture
    /// that throws), and value messages (agent and attention values) that a client applies many frames after they
    /// arrived. Invariants: a spawn never lands on a stale binding (the cross-type write), and a value message never
    /// applies to an id that was reused for another entity in the meantime. Each rule is also switched off once, to
    /// prove the simulation would notice.
    /// </summary>
    public sealed class IdReuseFailureSimulationTests
    {
        private struct Op
        {
            public bool Spawn;
            public uint Id;
            public int Entity;
        }

        private sealed class Message
        {
            public int DeliverAt;
            public bool IsValues;
            public List<Op> Ops;
        }

        private sealed class SimPeer
        {
            public byte Id;
            public readonly Queue<Message> Inbox = new Queue<Message>();
            public readonly Dictionary<uint, int> Map = new Dictionary<uint, int>();
            public readonly ApplyAckTracker Tracker = new ApplyAckTracker();
            public readonly AckHeldQueue<(int ApplyAt, List<Op> Ops)> Values = new AckHeldQueue<(int, List<Op>)>();
            public readonly Queue<(int At, long Count)> Acks = new Queue<(int, long)>();
            public long Sent;
            public int Latency;
        }

        private sealed class Outcome
        {
            public int SpawnOverStale;
            public int ValueOnReusedId;
            public int Reused;
            public int LostBatches;
            public bool Converged;
        }

        private static Outcome Run(ulong seed, bool releaseAfterBroadcast, bool holdValues, double lossRate, int frames = 2500)
        {
            var random = new DeterministicRandom(seed);
            var ids = new NetIdAllocator { MinQuarantineSeconds = 0 };
            var book = new ApplyAckBook();
            var host = new Dictionary<uint, int>();
            var staged = new StagedReleases();
            var stagedEntity = new Dictionary<uint, int>();
            var repair = new HashSet<uint>();
            var peers = new List<SimPeer>();
            var outcome = new Outcome();
            var alive = new List<uint>();
            int nextEntity = 1;

            for (byte p = 1; p <= 3; p++)
            {
                var peer = new SimPeer { Id = p, Latency = 1 + random.Next(8) };
                if (holdValues) peer.Values.Attach(peer.Tracker);
                book.AddPeer(p);
                peers.Add(peer);
            }
            for (int i = 0; i < 200; i++)
            {
                uint id = ids.Allocate();
                host[id] = nextEntity++;
                alive.Add(id);
            }
            foreach (SimPeer p in peers)
            {
                foreach (KeyValuePair<uint, int> pair in host) p.Map[pair.Key] = pair.Value;
            }

            for (int f = 1; f <= frames + 300; f++)
            {
                bool quiet = f > frames;
                ids.Seal(book.SealFrame(pid => peers.Find(x => x.Id == pid).Sent), f);
                foreach (SimPeer p in peers)
                {
                    while (p.Acks.Count > 0 && p.Acks.Peek().At <= f) book.OnAck(p.Id, p.Acks.Dequeue().Count);
                }
                long reusedBefore = ids.ReusedCount;
                ids.Update(book.MinAckedFrame, 0, f);

                // the host's capture of this frame
                var ops = new List<Op>();
                foreach (uint id in staged.Count > 0 ? new List<uint>(stagedEntity.Keys) : new List<uint>())
                {
                    ops.Add(new Op { Spawn = false, Id = id, Entity = stagedEntity[id] }); // an unsent frame's despawns, again
                }
                foreach (uint id in repair)
                {
                    if (host.TryGetValue(id, out int entity)) ops.Add(new Op { Spawn = true, Id = id, Entity = entity }); // integrity repair
                }
                repair.Clear();
                if (!quiet)
                {
                    int despawns = random.Next(5);
                    for (int k = 0; k < despawns && alive.Count > 40; k++)
                    {
                        int index = random.Next(alive.Count);
                        uint id = alive[index];
                        alive[index] = alive[alive.Count - 1];
                        alive.RemoveAt(alive.Count - 1);
                        ops.Add(new Op { Spawn = false, Id = id, Entity = host[id] });
                        if (releaseAfterBroadcast)
                        {
                            staged.Stage(id);
                            stagedEntity[id] = host[id];
                        }
                        else
                        {
                            Assert.True(ids.Release(id)); // the old rule: released while the batch is being built
                        }
                        host.Remove(id);
                    }
                    int spawns = random.Next(5);
                    for (int k = 0; k < spawns; k++)
                    {
                        uint id = ids.Allocate();
                        int entity = nextEntity++;
                        host[id] = entity;
                        alive.Add(id);
                        ops.Add(new Op { Spawn = true, Id = id, Entity = entity });
                    }
                }
                outcome.Reused += (int)(ids.ReusedCount - reusedBefore);
                bool lost = !quiet && random.NextDouble() < lossRate;
                if (lost)
                {
                    // the capture threw after the despawns were written: the batch never goes out
                    outcome.LostBatches++;
                    foreach (Op op in ops)
                    {
                        if (op.Spawn) repair.Add(op.Id); // the integrity check asks for missing entities again
                    }
                }
                else if (ops.Count > 0)
                {
                    foreach (SimPeer p in peers)
                    {
                        p.Inbox.Enqueue(new Message { DeliverAt = f + p.Latency, Ops = ops });
                        p.Sent++;
                    }
                    if (releaseAfterBroadcast)
                    {
                        staged.Commit(ids);
                        stagedEntity.Clear();
                    }
                }
                // value messages name a few live entities (same ordered lane as the batches)
                if (!quiet && alive.Count > 0)
                {
                    foreach (SimPeer p in peers)
                    {
                        var values = new List<Op>();
                        for (int k = 0; k < 3; k++)
                        {
                            uint id = alive[random.Next(alive.Count)];
                            values.Add(new Op { Id = id, Entity = host[id] });
                        }
                        p.Inbox.Enqueue(new Message { DeliverAt = f + p.Latency, IsValues = true, Ops = values });
                    }
                }

                // peers: structure applies on delivery, values wait (a structure backlog, a catch-up)
                foreach (SimPeer p in peers)
                {
                    while (p.Inbox.Count > 0 && p.Inbox.Peek().DeliverAt <= f)
                    {
                        Message m = p.Inbox.Dequeue();
                        if (m.IsValues)
                        {
                            p.Values.Enqueue((f + random.Next(40), m.Ops));
                            continue;
                        }
                        p.Tracker.OnReceived();
                        foreach (Op op in m.Ops)
                        {
                            if (op.Spawn)
                            {
                                if (p.Map.TryGetValue(op.Id, out int had) && had != op.Entity) outcome.SpawnOverStale++;
                                p.Map[op.Id] = op.Entity;
                            }
                            else if (p.Map.TryGetValue(op.Id, out int had) && had == op.Entity)
                            {
                                p.Map.Remove(op.Id);
                            }
                        }
                    }
                    // the oldest waiting value message applies once its time came (FIFO)
                    while (p.Values.TryPeek(out (int ApplyAt, List<Op> Ops) next) && (next.ApplyAt <= f || quiet))
                    {
                        p.Values.TryDequeue(out (int ApplyAt, List<Op> Ops) v);
                        foreach (Op op in v.Ops)
                        {
                            if (p.Map.TryGetValue(op.Id, out int bound) && bound != op.Entity) outcome.ValueOnReusedId++;
                        }
                    }
                    if (f % 3 == 0) p.Acks.Enqueue((f + p.Latency, p.Tracker.AppliedThrough));
                }
            }

            outcome.Converged = true;
            foreach (SimPeer p in peers)
            {
                foreach (KeyValuePair<uint, int> pair in host)
                {
                    if (!p.Map.TryGetValue(pair.Key, out int e) || e != pair.Value) outcome.Converged = false;
                }
                if (p.Map.Count != host.Count) outcome.Converged = false;
            }
            return outcome;
        }

        [Test]
        public void LostBatchesNeverLeaveAStaleBindingWhenIdsAreReleasedAfterTheBroadcast()
        {
            for (ulong seed = 1; seed <= 4; seed++)
            {
                Outcome o = Run(seed, releaseAfterBroadcast: true, holdValues: true, lossRate: 0.05);
                Assert.True(o.LostBatches > 50, "seed " + seed + ": batches were lost (" + o.LostBatches + ")");
                Assert.True(o.Reused > 100, "seed " + seed + ": ids were reused (" + o.Reused + ")");
                Assert.Equal(0, o.SpawnOverStale, "seed " + seed + ": a spawn landed on a stale binding");
                Assert.Equal(0, o.ValueOnReusedId, "seed " + seed + ": a held value applied to a reused id");
                Assert.True(o.Converged, "seed " + seed + ": every peer ends with the host's bindings");
            }
        }

        [Test]
        public void ReleasingWhileTheBatchIsBuiltLosesDespawnsAndReusesTheirIds()
        {
            // control: the old rule (released at once, then the batch was lost) must be caught by the simulation
            int stale = 0;
            for (ulong seed = 11; seed <= 14; seed++) stale += Run(seed, releaseAfterBroadcast: false, holdValues: true, lossRate: 0.05).SpawnOverStale;
            Assert.True(stale > 0, "a lost despawn of a released id must show as a spawn over a stale binding");
        }

        [Test]
        public void UnheldValueMessagesApplyToReusedIds()
        {
            // control: value messages that wait without holding the acknowledgement name ids reused meanwhile
            int violations = 0, held = 0;
            for (ulong seed = 21; seed <= 24; seed++)
            {
                violations += Run(seed, releaseAfterBroadcast: true, holdValues: false, lossRate: 0).ValueOnReusedId;
                held += Run(seed, releaseAfterBroadcast: true, holdValues: true, lossRate: 0).ValueOnReusedId;
            }
            Assert.True(violations > 0, "late values without a hold must hit reused ids in the simulation");
            Assert.Equal(0, held);
        }
    }
}
