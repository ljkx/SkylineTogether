using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Diagnostics;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// w2/ids: the session counts reliable <c>Epoch</c> messages on both ends (host per peer when queued, client when
    /// dispatched, joiners included), carries <c>EpochAck</c> to the host and reports joins and per-peer link state.
    /// </summary>
    public sealed class ApplyAckSessionTests
    {
        [Test]
        public void HostAndClientCountTheSameEpochMessagesEvenAcrossTheJoin()
        {
            var rig = new SessionRig();
            var (client, loader, _) = rig.AddClient("Anna", tweakLoader: l => l.Defer = true);
            rig.Run(5);
            var peers = new List<PeerId>();
            rig.Host.GetAdmittedPeers(peers);
            Assert.Equal(1, peers.Count);
            PeerId anna = peers[0];
            Assert.True(rig.Host.JoinInProgress, "a join is in progress until the joiner is live");
            for (byte i = 0; i < 12; i++) rig.Host.Broadcast(MessageKind.Epoch, new[] { i }, 0, 1);
            rig.Host.SendTo(anna, MessageKind.Epoch, new byte[] { 50 }, 0, 1);
            rig.Host.Broadcast(MessageKind.MotionControl, new byte[] { 1 }, 0, 1); // other kinds are not counted
            rig.Host.Broadcast(MessageKind.Water, new byte[] { 1 }, 0, 1, Lane.Bulk);
            Assert.Equal(13L, rig.Host.GetEpochSent(anna));
            rig.Run(200);
            Assert.Equal(0L, client.ApplyAcks.Received, "nothing is dispatched while the city loads");
            loader.Finish();
            rig.Run(20);
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
            Assert.False(rig.Host.JoinInProgress);
            Assert.Equal(13L, client.ApplyAcks.Received);
            Assert.Equal(13L, client.ApplyAcks.AppliedThrough, "nobody held them");
            Assert.Equal(0L, rig.Host.GetEpochSent(new PeerId(77)), "unknown peer");
        }

        [Test]
        public void EpochAcksReachTheHostAsReplicationMessages()
        {
            var rig = new SessionRig();
            var (client, _, _) = rig.AddClient("Anna");
            rig.Run(400);
            Assert.Equal(ClientState.Live, client.State, client.EndReason);
            long acked = -1;
            PeerId from = PeerId.Host;
            rig.Host.AppMessage += (peer, e) =>
            {
                if (e.Kind != MessageKind.ApplyAck) return;
                from = peer;
                acked = ApplyAckMessage.Read(e.Body, e.BodyOffset, e.BodyCount);
            };
            var w = new ByteWriter(16);
            ApplyAckMessage.Write(w, 41);
            client.SendToHost(MessageKind.ApplyAck, w.Buffer, 0, w.Length);
            rig.Run(5);
            Assert.Equal(41L, acked);
            Assert.NotEqual(PeerId.Host, from);
            Assert.True(rig.Host.TryGetLink(from, out PeerSendState link));
            Assert.True(link.Live);
            Assert.True(link.QueuedReliableBytes >= 0 && link.PendingReliableBytes >= 0);
            Assert.False(rig.Host.TryGetLink(new PeerId(99), out _));
        }

        [Test]
        public void PerfCountersAverageTimingsAndMeasureRates()
        {
            var perf = new PerfCounters();
            for (int f = 0; f < 100; f++)
            {
                perf.AddTime("apply", 1.0);
                perf.AddTime("apply", 1.0); // several calls in one frame add up
                perf.EndFrame(f / 60.0);
            }
            Assert.Near(2.0, perf.AverageMs("apply"), 1e-9);
            perf.AddTime("apply", 9.0);
            perf.EndFrame(2.0);
            Assert.Near(9.0, perf.PeakMs("apply"), 1e-9);
            Assert.True(perf.AverageMs("apply") > 2.0 && perf.AverageMs("apply") < 3.0, "smoothed");
            Assert.Equal(0.0, perf.AverageMs("unknown"));
            perf.AddTime("apply", double.NaN); // ignored
            perf.AddTime("apply", -1);

            for (int i = 0; i < 10; i++) perf.AddBytes("values", 1000, 10.0 + i * 0.1);
            Assert.Near(10_000, perf.Rate("values", 10.95), 1e-9);
            Assert.Near(0, perf.Rate("values", 30.0), 1e-9, "a second later nothing is left in the window");
            Assert.Equal(10_000L, perf.TotalBytes("values"));
            perf.Count("drops");
            perf.Count("drops", 2);
            Assert.Equal(3L, perf.GetCount("drops"));
            var lines = new List<string>();
            perf.Describe(10.95, lines);
            Assert.True(lines.Count >= 3);
            Assert.Contains("apply", perf.Summary(10.95));
            Assert.Contains("values", perf.Summary(10.95));
            string compact = perf.CompactSummary(10.95, "received");
            Assert.Contains("ms/frame (apply", compact);
            Assert.Contains("9.8 KB/s", compact);
            Assert.Equal("", new PerfCounters().CompactSummary(0));
        }

        [Test]
        public void FrameBudgetCarriesOverrunsIntoTheNextFrames()
        {
            var budget = new FrameBudget(4.0);
            Assert.Equal(4.0, budget.BeginFrame());
            budget.EndFrame(10.0); // 6 ms over
            Assert.Near(0.4, budget.BeginFrame(), 1e-9, "never below a tenth of the budget");
            budget.EndFrame(0.4); // repays 3.6
            Assert.Near(2.4, budget.DebtMs, 1e-9);
            Assert.Near(1.6, budget.BeginFrame(), 1e-9);
            budget.EndFrame(1.0);
            budget.EndFrame(1.0);
            Assert.Equal(0.0, budget.DebtMs, "unused time repays debt");
            budget.EndFrame(0.0);
            Assert.Equal(4.0, budget.BeginFrame(), "no credit is banked");
            budget.EndFrame(1000);
            Assert.Equal(16.0, budget.DebtMs, "debt is capped at four frames");
        }
    }
}
