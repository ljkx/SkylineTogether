using System;
using System.Collections.Generic;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>
    /// w2/ids: the reuse protocol end to end with the real allocator, ack book and tracker: a host spawning and despawning
    /// at random, peers on links with random latency that apply late, send acknowledgements and proposals that name the
    /// ids they know, and peers joining from a snapshot taken after their admission. Invariants: a proposal never reaches
    /// a host binding of a different entity under the same id, a despawn never hits a reused binding, and every peer
    /// ends with exactly the host's bindings.
    /// </summary>
    public sealed class IdReuseSimulationTests
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
            public List<Op> Ops;
        }

        private sealed class SimPeer
        {
            public byte Id;
            public readonly Queue<Message> Inbox = new Queue<Message>();
            public readonly Dictionary<uint, int> Map = new Dictionary<uint, int>();
            public readonly ApplyAckTracker Tracker = new ApplyAckTracker();
            public readonly Queue<(int At, bool IsAck, long Count, uint Id, int Entity)> Upstream = new Queue<(int, bool, long, uint, int)>();
            public long Sent;
            public bool Live;
            public int SnapshotAt = -1;
            public int LiveAt = -1;
            public Dictionary<uint, int> Snapshot;
            public int Latency;
        }

        private sealed class Outcome
        {
            public int ProposalViolations;
            public int DespawnViolations;
            public int SpawnOverStale;
            public int Reused;
            public uint Next;
            public bool Converged;
        }

        private static Outcome Run(ulong seed, bool honourAcks, bool barrier, int frames = 3000)
        {
            var random = new DeterministicRandom(seed);
            var ids = new NetIdAllocator { MinQuarantineSeconds = 0 };
            var book = new ApplyAckBook();
            var host = new Dictionary<uint, int>();
            var peers = new List<SimPeer>();
            var outcome = new Outcome();
            int nextEntity = 1;
            uint barrierEpoch = 0;
            byte nextPeer = 1;
            var alive = new List<uint>();

            void AddPeer(int frame, bool fromStart)
            {
                var p = new SimPeer { Id = nextPeer++, Latency = 1 + random.Next(12) };
                book.AddPeer(p.Id);
                if (fromStart)
                {
                    p.Snapshot = new Dictionary<uint, int>(host);
                    p.SnapshotAt = frame;
                    p.LiveAt = frame;
                }
                else
                {
                    p.SnapshotAt = frame + 1 + random.Next(20); // the snapshot is taken after the admission
                    p.LiveAt = p.SnapshotAt + 5 + random.Next(60);
                }
                peers.Add(p);
            }

            AddPeer(0, true);
            AddPeer(0, true);
            for (int i = 0; i < 300; i++)
            {
                uint id = ids.Allocate();
                host[id] = nextEntity++;
                alive.Add(id);
            }
            foreach (SimPeer p in peers) p.Snapshot = new Dictionary<uint, int>(host);

            for (int f = 1; f <= frames + 400; f++)
            {
                bool quiet = f > frames;
                // frame start: close the previous frame's epoch, then the upstream messages that arrived
                ids.Seal(book.SealFrame(pid => peers.Find(x => x.Id == pid)?.Sent ?? 0), f);
                foreach (SimPeer p in peers)
                {
                    while (p.Upstream.Count > 0 && p.Upstream.Peek().At <= f)
                    {
                        var u = p.Upstream.Dequeue();
                        if (u.IsAck)
                        {
                            book.OnAck(p.Id, u.Count);
                        }
                        else if (host.TryGetValue(u.Id, out int bound) && bound != u.Entity)
                        {
                            outcome.ProposalViolations++;
                        }
                    }
                }
                bool joining = peers.Exists(x => !x.Live && x.LiveAt > 0);
                if (barrier && joining)
                {
                    if (barrierEpoch == 0) barrierEpoch = Math.Max(1u, book.CurrentFrame);
                }
                else
                {
                    barrierEpoch = 0;
                }
                long reusedBefore = ids.ReusedCount;
                ids.Update(honourAcks ? book.MinAckedFrame : book.CurrentFrame, barrierEpoch, f);

                // the host's edits of this frame
                var ops = new List<Op>();
                if (!quiet)
                {
                    int despawns = random.Next(6);
                    for (int k = 0; k < despawns && alive.Count > 50; k++)
                    {
                        int index = random.Next(alive.Count);
                        uint id = alive[index];
                        alive[index] = alive[alive.Count - 1];
                        alive.RemoveAt(alive.Count - 1);
                        ops.Add(new Op { Spawn = false, Id = id, Entity = host[id] });
                        host.Remove(id);
                        Assert.True(ids.Release(id));
                    }
                    int spawns = random.Next(6);
                    for (int k = 0; k < spawns; k++)
                    {
                        uint id = ids.Allocate();
                        int entity = nextEntity++;
                        host[id] = entity;
                        alive.Add(id);
                        ops.Add(new Op { Spawn = true, Id = id, Entity = entity });
                    }
                    if (f % 400 == 0 && peers.Count < 7) AddPeer(f, false);
                }
                outcome.Reused += (int)(ids.ReusedCount - reusedBefore);
                foreach (SimPeer p in peers)
                {
                    if (p.SnapshotAt == f) p.Snapshot = new Dictionary<uint, int>(host);
                    if (ops.Count == 0) continue;
                    p.Inbox.Enqueue(new Message { DeliverAt = f + p.Latency + random.Next(3), Ops = ops });
                    p.Sent++;
                }

                // peers
                foreach (SimPeer p in peers)
                {
                    if (!p.Live)
                    {
                        if (p.LiveAt > f || p.Snapshot == null) continue;
                        p.Live = true;
                        foreach (KeyValuePair<uint, int> pair in p.Snapshot) p.Map[pair.Key] = pair.Value;
                    }
                    int applied = 0;
                    while (p.Inbox.Count > 0 && p.Inbox.Peek().DeliverAt <= f && applied < 1 + random.Next(4))
                    {
                        Message m = p.Inbox.Dequeue();
                        long seq = p.Tracker.OnReceived();
                        foreach (Op op in m.Ops)
                        {
                            if (op.Spawn)
                            {
                                if (p.Map.TryGetValue(op.Id, out int had) && had != op.Entity) outcome.SpawnOverStale++;
                                p.Map[op.Id] = op.Entity;
                            }
                            else
                            {
                                if (p.Map.TryGetValue(op.Id, out int had) && had != op.Entity) outcome.DespawnViolations++;
                                p.Map.Remove(op.Id);
                            }
                        }
                        applied++;
                        _ = seq; // applied at once: never held
                    }
                    int up = f + p.Latency + random.Next(3);
                    // a proposal naming an id this peer knows (it was created from its current state)
                    if (p.Map.Count > 0 && random.Next(2) == 0)
                    {
                        int skip = random.Next(p.Map.Count);
                        foreach (KeyValuePair<uint, int> pair in p.Map)
                        {
                            if (skip-- > 0) continue;
                            p.Upstream.Enqueue((up, false, 0, pair.Key, pair.Value));
                            break;
                        }
                    }
                    if (f % 3 == 0) p.Upstream.Enqueue((up, true, p.Tracker.AppliedThrough, 0, 0));
                }
            }

            outcome.Next = ids.Next;
            outcome.Converged = true;
            foreach (SimPeer p in peers)
            {
                if (!p.Live || p.Map.Count != host.Count) outcome.Converged = false;
                else
                {
                    foreach (KeyValuePair<uint, int> pair in host)
                    {
                        if (!p.Map.TryGetValue(pair.Key, out int e) || e != pair.Value) outcome.Converged = false;
                    }
                }
            }
            return outcome;
        }

        [Test]
        public void ReuseNeverReachesAPeerThatStillHoldsTheOldBinding()
        {
            for (ulong seed = 1; seed <= 6; seed++)
            {
                Outcome o = Run(seed, honourAcks: true, barrier: true);
                Assert.Equal(0, o.ProposalViolations, "seed " + seed + ": a proposal reached a reused id");
                Assert.Equal(0, o.DespawnViolations, "seed " + seed + ": a despawn hit a reused binding");
                Assert.Equal(0, o.SpawnOverStale, "seed " + seed + ": a spawn landed on a stale binding");
                Assert.True(o.Converged, "seed " + seed + ": every peer ends with the host's bindings");
                Assert.True(o.Reused > 100, "seed " + seed + ": ids were actually reused (" + o.Reused + ")");
                Assert.True(o.Next < 2500, "seed " + seed + ": the id range stays bounded (" + o.Next + "; about 7,800 spawns)");
            }
        }

        [Test]
        public void AcknowledgementsAloneAreEnoughWhenSnapshotsFollowTheAdmission()
        {
            for (ulong seed = 11; seed <= 14; seed++)
            {
                Outcome o = Run(seed, honourAcks: true, barrier: false);
                Assert.Equal(0, o.ProposalViolations + o.DespawnViolations + o.SpawnOverStale, "seed " + seed);
                Assert.True(o.Converged, "seed " + seed);
            }
        }

        [Test]
        public void WithoutTheQuarantineTheSimulationFindsViolations()
        {
            // control: proves the invariants above can fail, i.e. the test would notice a broken quarantine
            int violations = 0;
            for (ulong seed = 21; seed <= 24; seed++)
            {
                Outcome o = Run(seed, honourAcks: false, barrier: false);
                violations += o.ProposalViolations + o.DespawnViolations + o.SpawnOverStale;
            }
            Assert.True(violations > 0, "reusing ids at once must break something");
        }
    }
}
