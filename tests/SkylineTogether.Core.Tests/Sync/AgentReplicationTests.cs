using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Hashing;
using SkylineTogether.Core.Scheduling;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>w3/agents: the pure parts of agent replication (records, planner, scan order, closures, links, throttles).</summary>
    public sealed class AgentReplicationTests
    {
        private static readonly PeerId A = new PeerId(1);
        private static readonly PeerId B = new PeerId(2);

        private static List<SyncRecord> RoundTrip(SyncBatch.Writer w)
        {
            ArraySegment<byte> bytes = w.Finish();
            var copy = new byte[bytes.Count + 5];
            Buffer.BlockCopy(bytes.Array, 0, copy, 5, bytes.Count);
            return SyncBatch.Read(copy, 5, bytes.Count);
        }

        // ------------------------------------------------------------------ records

        [Test]
        public void MoverAgentLinksRoundTripInAMoverBatch()
        {
            var w = new SyncBatch.Writer();
            w.Mover(SyncRecordKind.MoverSpawn, 900, new MoverRecord { PrefabNetId = 3 });
            w.MoverAgents(900, new MoverAgentLinks { Agent = 12345 });
            w.MoverAgents(901, new MoverAgentLinks { Keeper = 7, Owner = 2_000_000 });
            w.MoverAgents(902, new MoverAgentLinks());
            ArraySegment<byte> bytes = w.Finish();
            Assert.True(SyncBatch.IsMoverBatch(bytes.Array, bytes.Offset, bytes.Count));
            List<SyncRecord> recs = SyncBatch.Read(bytes.Array, bytes.Offset, bytes.Count);
            Assert.Equal(4, recs.Count);
            Assert.Equal(SyncRecordKind.MoverAgents, recs[1].Kind);
            Assert.Equal(900u, recs[1].NetId);
            Assert.Equal(12345u, recs[1].AgentLinks.Agent);
            Assert.Equal(0u, recs[1].AgentLinks.Keeper);
            Assert.Equal(7u, recs[2].AgentLinks.Keeper);
            Assert.Equal(2_000_000u, recs[2].AgentLinks.Owner);
            Assert.True(recs[3].AgentLinks.IsEmpty);
            Assert.True(recs[2].AgentLinks.SameAs(new MoverAgentLinks { Keeper = 7, Owner = 2_000_000 }));
            Assert.False(recs[2].AgentLinks.SameAs(recs[1].AgentLinks));
            Assert.True(new MoverAgentLinks().SameAs(null));
        }

        [Test]
        public void ALinksOnlyBatchStillRoutesToTheMovers()
        {
            var w = new SyncBatch.Writer();
            w.MoverAgents(5, new MoverAgentLinks { Agent = 6 });
            ArraySegment<byte> bytes = w.Finish();
            Assert.True(SyncBatch.IsMoverBatch(bytes.Array, bytes.Offset, bytes.Count));
            Assert.False(SyncBatch.AcceptedFromClient(SyncRecordKind.MoverAgents));
        }

        [Test]
        public void MalformedLinksAreDecodeErrors()
        {
            var w = new ByteWriter();
            w.WriteByte(8);
            Assert.Throws<DecodeException>(() => MoverAgentLinksCodec.Read(new ByteReader(w.Buffer, 0, w.Length)));
            w.Reset();
            w.WriteByte(1);
            w.WriteVarUInt32(0);
            Assert.Throws<DecodeException>(() => MoverAgentLinksCodec.Read(new ByteReader(w.Buffer, 0, w.Length)));
            w.Reset();
            w.WriteByte(2);
            Assert.Throws<DecodeException>(() => MoverAgentLinksCodec.Read(new ByteReader(w.Buffer, 0, w.Length)));
        }

        [Test]
        public void RefreshMessageRoundTripsAndRejectsGarbage()
        {
            var w = new ByteWriter();
            AgentRefreshMessage.Write(w, new uint[] { 1, 0, 300000, 16_777_215 });
            List<uint> ids = AgentRefreshMessage.Read(w.Buffer, 0, w.Length);
            Assert.SequenceEqual(new uint[] { 1, 300000, 16_777_215 }, ids);
            Assert.Throws<DecodeException>(() => AgentRefreshMessage.Read(new byte[] { 2, 0 }, 0, 2));
            Assert.Throws<DecodeException>(() => AgentRefreshMessage.Read(new byte[] { 1, 5, 1 }, 0, 3));
            Assert.Throws<DecodeException>(() => AgentRefreshMessage.Read(new byte[] { 1, 1, 1, 9 }, 0, 4));
            var big = new uint[AgentRefreshMessage.MaxIds + 1];
            Assert.Throws<ArgumentException>(() => AgentRefreshMessage.Write(new ByteWriter(), big));
        }

        // ------------------------------------------------------------------ planner

        private static List<uint> DrainAll(AgentPlanner planner, PeerId peer, int budget = 1 << 20, int bytesPerAgent = 10)
        {
            var sent = new List<uint>();
            planner.Drain(peer, budget, (id, structural) =>
            {
                sent.Add(id);
                return bytesPerAgent;
            });
            return sent;
        }

        [Test]
        public void PlannerCoalescesChangesAndServesFocusThenViewThenOldest()
        {
            var p = new AgentPlanner();
            p.AddPeer(A, p.CurrentStamp);
            foreach (uint id in new uint[] { 50, 40, 30, 20, 10 }) p.MarkChanged(id);
            p.MarkChanged(50); // coalesced: still one entry
            p.SetView(A, new uint[] { 20, 99 });
            p.SetFocus(A, new uint[] { 30 });
            Assert.Equal(5, p.DirtyCount(A));
            Assert.Equal(1, p.FocusDirtyCount(A));
            Assert.Equal(1, p.ViewDirtyCount(A));
            List<uint> sent = DrainAll(p, A);
            Assert.SequenceEqual(new uint[] { 30, 20, 50, 40, 10 }, sent);
            Assert.Equal(0, p.DirtyCount(A));
            p.GetSent(A, out long agents, out long bytes);
            Assert.Equal(5L, agents);
            Assert.Equal(50L, bytes);
        }

        [Test]
        public void PlannerFocusWinsOverViewForTheSameAgent()
        {
            var p = new AgentPlanner();
            p.AddPeer(A, 0);
            p.SetView(A, new uint[] { 7 });
            p.SetFocus(A, new uint[] { 7 });
            p.MarkChanged(7);
            Assert.Equal(1, p.FocusDirtyCount(A));
            Assert.Equal(0, p.ViewDirtyCount(A));
            p.SetView(A, new uint[] { 7, 8 });
            Assert.Equal(0, p.ViewDirtyCount(A));
        }

        [Test]
        public void PlannerRespectsTheBudgetAndKeepsTheRestDirty()
        {
            var p = new AgentPlanner();
            p.AddPeer(A, 0);
            for (uint id = 1; id <= 10; id++) p.MarkChanged(id);
            int used = p.Drain(A, 35, (id, s) => 10);
            Assert.Equal(40, used); // the record that crosses the budget is sent whole
            Assert.Equal(6, p.DirtyCount(A));
            int none = p.Drain(A, 100, (id, s) => AgentPlanner.NoRoom);
            Assert.Equal(0, none);
            Assert.Equal(6, p.DirtyCount(A));
            Assert.SequenceEqual(new uint[] { 5, 6, 7, 8, 9, 10 }, DrainAll(p, A));
        }

        [Test]
        public void PlannerForgetsGoneAgentsAndReusedIdsStartClean()
        {
            var p = new AgentPlanner();
            p.AddPeer(A, 0);
            p.MarkChanged(3);
            p.MarkChanged(4);
            p.Forget(3);
            Assert.SequenceEqual(new uint[] { 4 }, DrainAll(p, A));
            // the emitter reports a vanished agent: nothing counted, no longer dirty
            p.MarkChanged(5);
            int used = p.Drain(A, 100, (id, s) => AgentPlanner.Gone);
            Assert.Equal(0, used);
            Assert.Equal(0, p.DirtyCount(A));
        }

        [Test]
        public void PlannerJoinerGetsOnlyChangesAfterItsSnapshot()
        {
            var p = new AgentPlanner();
            p.AddPeer(A, 0);
            p.MarkChanged(1);
            p.MarkChanged(2);
            long snapshot = p.CurrentStamp;
            p.MarkChanged(3);
            p.MarkChanged(1); // changed again after the snapshot
            p.AddPeer(B, snapshot);
            List<uint> sent = DrainAll(p, B);
            sent.Sort();
            Assert.SequenceEqual(new uint[] { 1, 3 }, sent);
            Assert.Equal(3, p.DirtyCount(A));
        }

        [Test]
        public void PlannerBroadcastRecordCleansEveryPeerButKeepsTheStamp()
        {
            var p = new AgentPlanner();
            p.AddPeer(A, 0);
            p.AddPeer(B, 0);
            p.MarkChanged(9);
            p.MarkSentToAll(9);
            Assert.Equal(0, p.DirtyCount(A));
            Assert.Equal(0, p.DirtyCount(B));
            Assert.Equal(1, p.TrackedCount);
        }

        [Test]
        public void PlannerRequestsAreServedFirstEvenWithoutAChange()
        {
            var p = new AgentPlanner();
            p.AddPeer(A, 0);
            p.MarkChanged(1);
            p.Request(A, 77);
            p.Request(B, 78); // unknown peer: ignored
            Assert.Equal(1, p.FocusDirtyCount(A));
            Assert.SequenceEqual(new uint[] { 77, 1 }, DrainAll(p, A));
            Assert.Equal(0, p.FocusDirtyCount(A));
        }

        [Test]
        public void StructuralChangesStickUntilSentAndReachJoiners()
        {
            var p = new AgentPlanner();
            p.AddPeer(A, 0);
            p.MarkChanged(1, structural: true);
            p.MarkChanged(1); // a later value change keeps the structural flag
            p.MarkChanged(2);
            long snapshot = p.CurrentStamp;
            p.MarkChanged(3, true);
            p.AddPeer(B, 0);
            Assert.True(p.IsStructural(A, 1));
            Assert.False(p.IsStructural(A, 2));
            Assert.Equal(2, p.StructuralCount(A));
            Assert.Equal(2, p.StructuralCount(B));
            var seen = new Dictionary<uint, bool>();
            p.Drain(A, 1000, (id, structural) =>
            {
                seen[id] = structural;
                return 5;
            });
            Assert.True(seen[1]);
            Assert.False(seen[2]);
            Assert.True(seen[3]);
            Assert.Equal(0, p.StructuralCount(A));
            p.Request(A, 2);
            Assert.True(p.IsStructural(A, 2));
            p.Forget(3);
            Assert.False(p.IsStructural(B, 3));
            var late = new AgentPlanner();
            late.AddPeer(A, 0);
            late.MarkChanged(9, true);
            long s2 = late.CurrentStamp;
            late.AddPeer(B, s2);
            Assert.Equal(0, late.StructuralCount(B));
            Assert.True(snapshot > 0);
        }

        // ------------------------------------------------------------------ scan order and closures

        [Test]
        public void RollingScanServesHotIdsOnceThenThePass()
        {
            var s = new RollingScan();
            Assert.True(s.PassDone);
            s.Restart(new uint[] { 1, 2, 0, 3 });
            Assert.Equal(3, s.PassLength);
            s.Touch(9);
            s.Touch(9);
            s.Touch(2);
            var order = new List<uint>();
            var hot = new List<bool>();
            while (s.TryNext(out uint id, out bool h))
            {
                order.Add(id);
                hot.Add(h);
            }
            Assert.SequenceEqual(new uint[] { 9, 2, 1, 2, 3 }, order);
            Assert.SequenceEqual(new[] { true, true, false, false, false }, hot);
            Assert.True(s.PassDone);
            s.Restart(new uint[] { 4 });
            Assert.Equal(1L, s.Passes);
            Assert.True(s.TryNext(out uint four, out _));
            Assert.Equal(4u, four);
        }

        [Test]
        public void ClosureStopsAtDepthAndCountAndListsEachIdOnce()
        {
            // building 1 -> households 10, 11 -> citizens 100.. ; household 10 also points back at the building
            var graph = new Dictionary<uint, uint[]>
            {
                { 1, new uint[] { 10, 11 } },
                { 10, new uint[] { 100, 101, 1 } },
                { 11, new uint[] { 110, 0 } },
                { 100, new uint[] { 1000 } },
            };
            Action<uint, List<uint>> expand = (id, list) =>
            {
                if (graph.TryGetValue(id, out uint[] n)) list.AddRange(n);
            };
            var output = new HashSet<uint>();
            ClosureBuilder.Build(new uint[] { 1 }, expand, 2, 100, output);
            var sorted = new List<uint>(output);
            sorted.Sort();
            Assert.SequenceEqual(new uint[] { 1, 10, 11, 100, 101, 110 }, sorted);
            output.Clear();
            ClosureBuilder.Build(new uint[] { 1 }, expand, 5, 4, output);
            Assert.Equal(4, output.Count);
            output.Clear();
            ClosureBuilder.Build(new uint[] { 0, 1, 1 }, expand, 0, 10, output);
            Assert.Equal(1, output.Count);
        }

        // ------------------------------------------------------------------ links, set hashes, throttles

        [Test]
        public void OwnersOfChangedReferencesIncludeOldAndNew()
        {
            var owners = new List<uint>();
            AgentRefs.CollectOwners(default, new AgentRefs { Household = 5, Property = 6 }, owners);
            Assert.SequenceEqual(new uint[] { 5, 6 }, owners);
            owners.Clear();
            var before = new AgentRefs { Household = 5, Property = 6, Workplace = 7 };
            var after = new AgentRefs { Household = 5, Property = 8, Workplace = 7, School = 9 };
            AgentRefs.CollectOwners(before, after, owners);
            Assert.SequenceEqual(new uint[] { 6, 8, 9 }, owners);
            owners.Clear();
            AgentRefs.CollectOwners(after, default, owners);
            Assert.SequenceEqual(new uint[] { 5, 8, 9, 7 }, owners);
            Assert.True(before.Equals(new AgentRefs { Household = 5, Property = 6, Workplace = 7 }));
            Assert.Equal(before.KeyHash(), new AgentRefs { Household = 5, Property = 6, School = 1 }.KeyHash());
            Assert.NotEqual(before.KeyHash(), after.KeyHash());
        }

        [Test]
        public void SetHashIgnoresOrderButCountsDuplicates()
        {
            ulong a = SetHash.Of(new uint[] { 1, 2, 3 });
            Assert.Equal(a, SetHash.Of(new uint[] { 3, 1, 2 }));
            Assert.NotEqual(a, SetHash.Of(new uint[] { 1, 2, 3, 3 }));
            Assert.NotEqual(a, SetHash.Of(new uint[] { 1, 2 }));
            Assert.NotEqual(SetHash.Of(new uint[0]), SetHash.Of(new uint[] { 0 }));
            var acc = new SetHash.Accumulator();
            acc.Add(2);
            acc.Add(3);
            acc.Add(1);
            Assert.Equal(a, acc.Value);
            Assert.Equal(3u, acc.Count);
        }

        [Test]
        public void RefreshThrottleBatchesAndLimitsEachId()
        {
            var t = new RefreshThrottle();
            var due = new List<uint>();
            Assert.True(t.Note(5, 0));
            Assert.True(t.Note(5, 0)); // already pending: no extra count
            Assert.True(t.Note(6, 0));
            Assert.True(t.TakeDue(0, 10, due));
            Assert.SequenceEqual(new uint[] { 5, 6 }, due);
            Assert.True(t.Note(5, 0.5));
            due.Clear();
            Assert.False(t.TakeDue(0.5, 10, due)); // interval
            Assert.True(t.TakeDue(1.0, 10, due));
            Assert.True(t.Note(5, 2));
            Assert.True(t.TakeDue(2, 10, due));
            Assert.False(t.Note(5, 3)); // third request in the window was the last
            Assert.Equal(1L, t.Refused);
            Assert.True(t.Note(5, 3 + RefreshThrottle.WindowSeconds)); // new window
            t.Forget(5);
            Assert.Equal(0, t.PendingCount);
            Assert.False(t.Note(0, 0));
            t.Note(9, 100);
            t.Sweep(100 + RefreshThrottle.WindowSeconds + 1);
            Assert.Equal(1, t.TrackedCount); // pending ids are kept
        }

        [Test]
        public void ChurnWithIdReuseNeverServesAStaleAgent()
        {
            // births, deaths and moves at random; a released id is reused for a new agent: the planner must offer each
            // live dirty id once and never an id that was forgotten after its last change
            var rng = new Random(12345);
            var p = new AgentPlanner();
            p.AddPeer(A, 0);
            var live = new HashSet<uint>();
            var dirtyExpected = new HashSet<uint>();
            for (int step = 0; step < 5000; step++)
            {
                uint id = (uint)rng.Next(1, 200);
                switch (rng.Next(3))
                {
                    case 0:
                        live.Add(id);
                        p.MarkChanged(id);
                        dirtyExpected.Add(id);
                        break;
                    case 1:
                        live.Remove(id);
                        p.Forget(id);
                        dirtyExpected.Remove(id);
                        break;
                    default:
                        if (step % 97 == 0)
                        {
                            foreach (uint sent in DrainAll(p, A))
                            {
                                Assert.True(dirtyExpected.Remove(sent), "offered " + sent + " that was not dirty");
                                Assert.True(live.Contains(sent));
                            }
                            Assert.Equal(0, dirtyExpected.Count);
                        }
                        break;
                }
            }
        }
    }
}
