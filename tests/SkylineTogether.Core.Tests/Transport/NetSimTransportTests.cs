using System;
using System.Collections.Generic;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Tests.Transport
{
    /// <summary>NetSimTransport (ARCHITECTURE.md#transport): impairment statistics within tolerance, reliable lanes stay exact, bandwidth and buffers.</summary>
    public sealed class NetSimTransportTests
    {
        private sealed class Rig
        {
            public ManualClock Clock;
            public NetSimTransport Host;
            public NetSimTransport Client;
            public IConnection ClientConn;
            public IConnection HostConn;
            public RecordingSink HostSink = new RecordingSink();
            public RecordingSink ClientSink = new RecordingSink();

            /// <summary>Advances the clock in steps, polling both ends after each.</summary>
            public void Run(double seconds, double step = 0.001)
            {
                int steps = (int)Math.Round(seconds / step);
                for (int i = 0; i < steps; i++)
                {
                    Clock.Advance(step);
                    Client.Poll(ClientSink);
                    Host.Poll(HostSink);
                }
            }

            public void PollBoth()
            {
                Client.Poll(ClientSink);
                Host.Poll(HostSink);
            }
        }

        private static Rig Connect(NetSimOptions clientOptions, NetSimOptions hostOptions = null)
        {
            var rig = new Rig { Clock = new ManualClock(10) };
            var (h, c) = LoopbackTransport.CreatePair();
            h.SendBufferBytes = 0;
            c.SendBufferBytes = 0;
            rig.Host = new NetSimTransport(h, hostOptions ?? new NetSimOptions(), rig.Clock);
            rig.Client = new NetSimTransport(c, clientOptions, rig.Clock);
            rig.Host.Listen(new ListenOptions());
            rig.ClientConn = rig.Client.Connect(new Endpoint());
            rig.PollBoth();
            Assert.Equal(1, rig.HostSink.Connected.Count);
            Assert.Equal(1, rig.ClientSink.Connected.Count);
            rig.HostConn = rig.HostSink.Connected[0];
            rig.HostSink.Clear();
            rig.ClientSink.Clear();
            return rig;
        }

        [Test]
        public void WrapsTheInnerTransportsConnections()
        {
            Rig rig = Connect(new NetSimOptions());
            Assert.Equal(TransportKind.NetSim, rig.Client.Kind);
            Assert.True(rig.ClientConn.Peer.SteamId != 0);
            Assert.Equal(((LoopbackTransport)rig.Host.Inner).Identity, rig.ClientConn.Peer);
            rig.ClientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, new byte[] { 9 }, 0, 1);
            rig.PollBoth();
            Assert.Equal(1, rig.HostSink.Messages.Count);
            Assert.Same(rig.HostConn, rig.HostSink.Messages[0].Connection, "events map to the same wrapper");
        }

        [Test]
        public void LatencyDelaysDelivery()
        {
            Rig rig = Connect(new NetSimOptions { LatencyMs = 50 });
            rig.ClientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, new byte[] { 1 }, 0, 1);
            rig.PollBoth();
            Assert.Equal(0, rig.HostSink.Messages.Count);
            rig.Clock.Advance(0.049);
            rig.PollBoth();
            Assert.Equal(0, rig.HostSink.Messages.Count, "not before the latency");
            rig.Clock.Advance(0.001);
            rig.PollBoth();
            Assert.Equal(1, rig.HostSink.Messages.Count, "due at exactly 50 ms");
            Assert.Equal(100, rig.ClientConn.GetStats().PingMs, "ping = 2 × latency");
        }

        [Test]
        public void UnreliableLossRateIsWithinTolerance()
        {
            Rig rig = Connect(new NetSimOptions { LossRate = 0.1, Seed = 1 });
            const int count = 20000;
            for (int i = 0; i < count; i++)
            {
                rig.ClientConn.Send(Lane.Unreliable, Delivery.Unreliable, TestMessages.Make(1, i, 100), 0, 100);
                if (i % 500 == 0) rig.PollBoth();
            }
            rig.PollBoth();
            long received = rig.HostSink.Messages.Count;
            double lost = 1 - received / (double)count;
            Assert.Near(0.1, lost, 0.01, "loss fraction");
            NetSimCounters counters = rig.Client.Counters;
            Assert.Equal(count - received, counters.UnreliableLost);
            Assert.Equal((long)count, counters.UnreliableMessages);
            Assert.Equal(received, counters.DeliveredMessages);
        }

        [Test]
        public void MultiPacketUnreliableMessagesAreLostWhenAnyPacketIs()
        {
            Rig rig = Connect(new NetSimOptions { LossRate = 0.1, Seed = 2 });
            rig.HostSink.KeepMessages = false;
            const int count = 10000;
            var m = new byte[3000];   // three 1200-byte packets
            for (int i = 0; i < count; i++)
            {
                rig.ClientConn.Send(Lane.Unreliable, Delivery.Unreliable, m, 0, m.Length);
                if (i % 200 == 0) rig.PollBoth();
            }
            rig.PollBoth();
            double lost = 1 - rig.HostSink.MessageCount / (double)count;
            Assert.Near(1 - 0.9 * 0.9 * 0.9, lost, 0.02, "1 − (1 − p)^3");
        }

        [Test]
        public void DuplicationRateIsWithinTolerance()
        {
            Rig rig = Connect(new NetSimOptions { DuplicateRate = 0.2, Seed = 3 });
            const int count = 20000;
            var seen = new int[count];
            rig.HostSink.KeepMessages = false;
            rig.HostSink.MessageHook = (c, l, b, o, n) => seen[TestMessages.Sequence(b, o)]++;
            for (int i = 0; i < count; i++)
            {
                rig.ClientConn.Send(Lane.Unreliable, Delivery.Unreliable, TestMessages.Make(1, i, 20), 0, 20);
                rig.Clock.Advance(0.0001);
                if (i % 100 == 0) rig.PollBoth();
            }
            rig.Run(0.01);
            int twice = 0;
            for (int i = 0; i < count; i++)
            {
                Assert.True(seen[i] == 1 || seen[i] == 2, "each message arrives once or twice");
                if (seen[i] == 2) twice++;
            }
            Assert.Near(0.2, twice / (double)count, 0.01, "duplicate fraction");
            Assert.Equal((long)twice, rig.Client.Counters.UnreliableDuplicated);
        }

        [Test]
        public void ReorderRateIsWithinTolerance()
        {
            List<int> order = ReorderRun(seed: 4, out NetSimCounters counters);
            int late = 0, max = -1;
            foreach (int seq in order)
            {
                if (seq < max) late++;
                else max = seq;
            }
            Assert.Equal(5000 + 30, order.Count, "nothing lost");
            Assert.Near(0.1, late / 5000.0, 0.015, "fraction overtaken by later messages");
            Assert.Equal(counters.UnreliableReordered, (long)late, "every held-back message is overtaken");
        }

        [Test]
        public void TheSameSeedGivesTheSameRun()
        {
            List<int> a = ReorderRun(7, out _);
            List<int> b = ReorderRun(7, out _);
            List<int> c = ReorderRun(8, out _);
            Assert.SequenceEqual(a, b);
            Assert.False(a.Count == c.Count && SameSequence(a, c), "another seed reorders other messages");
        }

        private static List<int> ReorderRun(int seed, out NetSimCounters counters)
        {
            Rig rig = Connect(new NetSimOptions { ReorderRate = 0.1, Seed = seed });
            var order = new List<int>();
            rig.HostSink.KeepMessages = false;
            rig.HostSink.MessageHook = (c, l, b, o, n) => order.Add(TestMessages.Sequence(b, o));
            for (int i = 0; i < 5000 + 30; i++)
            {
                // 30 trailing messages without reordering overtake whatever the last ones held back
                if (i == 5000) rig.Client.Options.ReorderRate = 0;
                rig.ClientConn.Send(Lane.Unreliable, Delivery.Unreliable, TestMessages.Make(1, i, 30), 0, 30);
                rig.Clock.Advance(0.001);
                rig.PollBoth();
            }
            rig.Run(0.05);
            counters = rig.Client.Counters;
            return order;
        }

        private static bool SameSequence(List<int> a, List<int> b)
        {
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        [Test]
        public void BandwidthCapIsRespected()
        {
            Rig rig = Connect(new NetSimOptions { BandwidthBytesPerSecond = 100_000 });
            for (int i = 0; i < 100; i++)
            {
                Assert.Equal(SendResult.Ok, rig.ClientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, TestMessages.Make(0, i, 10_000), 0, 10_000));
            }
            ConnectionStats start = rig.ClientConn.GetStats();
            Assert.Equal(1_000_000, start.PendingReliableBytes);
            Assert.Equal(100_000, start.SendRateBytesPerSecond);
            Assert.Near(10_000_000, start.QueueTimeMicroseconds, 1, "1 MB at 100 kB/s");
            double lastArrival = 0;
            int received = 0;
            rig.HostSink.MessageHook = (c, l, b, o, n) =>
            {
                received++;
                lastArrival = rig.Clock.Seconds - 10;
            };
            rig.Run(5, 0.01);
            ConnectionStats mid = rig.ClientConn.GetStats();
            Assert.InRange(received, 49, 51);
            Assert.Near(500_000, mid.PendingReliableBytes, 10_000);
            Assert.Near(100_000, mid.OutBytesPerSecond, 15_000, "measured send rate");
            rig.Run(6, 0.01);
            Assert.Equal(100, received);
            Assert.Near(10.0, lastArrival, 0.02, "1 MB takes 10 s at the cap");
            Assert.Equal(0, rig.ClientConn.GetStats().PendingReliableBytes);
        }

        [Test]
        public void ReliableLanesStayExactlyOnceAndOrderedUnderEveryImpairment()
        {
            var options = new NetSimOptions
            {
                LossRate = 0.2, DuplicateRate = 0.5, ReorderRate = 0.5, LatencyMs = 20, JitterMs = 30,
                BandwidthBytesPerSecond = 2_000_000, Seed = 11
            };
            Rig rig = Connect(options);
            var random = new DeterministicRandom(12);
            var sent = new int[3];
            for (int i = 0; i < 2000; i++)
            {
                Lane lane = random.Next(2) == 0 ? Lane.Reliable : Lane.Bulk;
                int size = 5 + random.Next(20_000);
                byte[] m = TestMessages.Make((byte)lane, sent[(int)lane]++, size);
                SendResult r;
                while ((r = rig.ClientConn.Send(lane, Delivery.ReliableOrdered, m, 0, m.Length)) == SendResult.Busy) rig.Run(0.01);
                Assert.Equal(SendResult.Ok, r);
                if (i % 20 == 0) rig.Run(0.002);
            }
            rig.Run(30, 0.005);
            var expected = new int[3];
            foreach (ReceivedMessage m in rig.HostSink.Messages)
            {
                int lane = TestMessages.Tag(m.Data);
                Assert.Equal((Lane)lane, m.Lane);
                Assert.Equal(expected[lane]++, TestMessages.Sequence(m.Data), "reliable lanes are ordered, without gaps or duplicates");
            }
            Assert.SequenceEqual(sent, expected);
            Assert.True(rig.Client.Counters.RetransmittedPackets > 0, "losses were paid with retransmissions");
            Assert.Equal(0L, rig.Client.Counters.UnreliableDuplicated);
        }

        [Test]
        public void ReliableLossCostsTimeAndBandwidth()
        {
            double clean = TimeToDeliver(new NetSimOptions { BandwidthBytesPerSecond = 1_000_000, LatencyMs = 10, Seed = 5 });
            double lossy = TimeToDeliver(new NetSimOptions { BandwidthBytesPerSecond = 1_000_000, LatencyMs = 10, LossRate = 0.1, Seed = 5 });
            Assert.Near(1.0 + 0.01, clean, 0.02, "1 MB at 1 MB/s plus latency");
            Assert.True(lossy > clean * 1.08, "10 % packet loss costs bandwidth and retransmission rounds (" + lossy + " s vs " + clean + " s)");
        }

        private static double TimeToDeliver(NetSimOptions options)
        {
            Rig rig = Connect(options);
            for (int i = 0; i < 100; i++) rig.ClientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, new byte[10_000], 0, 10_000);
            double start = rig.Clock.Seconds;
            double end = 0;
            int received = 0;
            rig.HostSink.KeepMessages = false;
            rig.HostSink.MessageHook = (c, l, b, o, n) =>
            {
                if (++received == 100) end = rig.Clock.Seconds;
            };
            rig.Run(10, 0.002);
            Assert.Equal(100, received);
            return end - start;
        }

        [Test]
        public void LanePrioritiesLetLaneRBypassQueuedBulk()
        {
            Assert.True(ReliableLatencyBehindBulk(true) < 0.06, "R overtakes the queued bulk data");
            Assert.True(ReliableLatencyBehindBulk(false) > 19, "without lanes R waits behind 2 MB at 100 kB/s");
        }

        private static double ReliableLatencyBehindBulk(bool prioritize)
        {
            Rig rig = Connect(new NetSimOptions { BandwidthBytesPerSecond = 100_000, PrioritizeLanes = prioritize, LatencyMs = 5 });
            for (int i = 0; i < 20; i++) rig.ClientConn.Send(Lane.Bulk, Delivery.ReliableOrdered, TestMessages.Make(2, i, 100_000), 0, 100_000);
            rig.Run(0.5, 0.01);
            double sentAt = rig.Clock.Seconds;
            double arrival = -1;
            rig.HostSink.MessageHook = (c, l, b, o, n) =>
            {
                if (l == Lane.Reliable) arrival = rig.Clock.Seconds;
            };
            rig.ClientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, TestMessages.Make(0, 0, 1000), 0, 1000);
            for (int i = 0; i < 3000 && arrival < 0; i++) rig.Run(0.01, 0.01);
            Assert.True(arrival > 0, "the R message arrived");
            return arrival - sentAt;
        }

        [Test]
        public void AFullSendBufferAnswersBusy()
        {
            Rig rig = Connect(new NetSimOptions { BandwidthBytesPerSecond = 10_000, SendBufferBytes = 50_000 });
            var m = new byte[10_000];
            for (int i = 0; i < 5; i++) Assert.Equal(SendResult.Ok, rig.ClientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, m, 0, m.Length));
            Assert.Equal(SendResult.Busy, rig.ClientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, m, 0, m.Length));
            Assert.Equal(SendResult.Busy, rig.ClientConn.Send(Lane.Unreliable, Delivery.Unreliable, m, 0, 100), "the buffer is shared");
            Assert.Equal(50_000, rig.ClientConn.GetStats().PendingReliableBytes);
            Assert.Equal(2L, rig.Client.Counters.SendBufferRefusals);
            rig.Run(1.0, 0.01);
            Assert.Equal(40_000, rig.ClientConn.GetStats().PendingReliableBytes, "one message left at 10 kB/s");
            Assert.Equal(SendResult.Ok, rig.ClientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, m, 0, m.Length));
        }

        [Test]
        public void BusyRateRefusesWithoutTakingTheMessage()
        {
            Rig rig = Connect(new NetSimOptions { Seed = 9 });
            rig.Client.BusyRate = 1;
            Assert.Equal(SendResult.Busy, rig.ClientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, new byte[10], 0, 10));
            rig.Client.BusyRate = 0.5;
            int ok = 0;
            for (int i = 0; i < 4000; i++)
            {
                if (rig.ClientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, TestMessages.Make(0, ok, 10), 0, 10) == SendResult.Ok) ok++;
            }
            rig.PollBoth();
            Assert.Near(2000, ok, 150);
            Assert.Equal(ok, rig.HostSink.Messages.Count, "refused messages were not taken");
            for (int i = 0; i < ok; i++) Assert.Equal(i, TestMessages.Sequence(rig.HostSink.Messages[i].Data));
            Assert.Equal(4001L - ok, rig.Client.Counters.BusyRateRefusals);
            Assert.Throws<ArgumentOutOfRangeException>(() => rig.Client.BusyRate = 1.5);
        }

        [Test]
        public void UnreliableNoDelayMessagesAreDroppedOnASaturatedLink()
        {
            Rig rig = Connect(new NetSimOptions { BandwidthBytesPerSecond = 100_000 });
            rig.ClientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, new byte[50_000], 0, 50_000);   // 0.5 s of backlog
            rig.ClientConn.Send(Lane.Unreliable, Delivery.Unreliable, new byte[100], 0, 100);
            Assert.Equal(1L, rig.Client.Counters.UnreliableSaturationDrops);
            rig.Run(1, 0.01);
            rig.ClientConn.Send(Lane.Unreliable, Delivery.Unreliable, new byte[100], 0, 100);
            rig.Run(0.1, 0.01);
            Assert.Equal(2, rig.HostSink.Messages.Count, "the reliable message and the second datagram");
        }

        [Test]
        public void CloseWithoutLingerLosesQueuedMessagesAndLingerDeliversThem()
        {
            Rig rig = Connect(new NetSimOptions { BandwidthBytesPerSecond = 1000 });
            for (int i = 0; i < 5; i++) rig.ClientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, new byte[1000], 0, 1000);
            rig.Run(0.5, 0.01);
            rig.ClientConn.Close(42, "bye", false);
            rig.Run(0.1, 0.01);
            Assert.Equal(0, rig.HostSink.Messages.Count);
            Assert.Equal(1, rig.HostSink.Disconnected.Count);
            Assert.Equal(42, rig.HostSink.Disconnected[0].Reason);
            Assert.Equal(SendResult.Closed, rig.ClientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, new byte[1], 0, 1));

            Rig lingering = Connect(new NetSimOptions { BandwidthBytesPerSecond = 1000, LatencyMs = 100 });
            for (int i = 0; i < 5; i++) lingering.ClientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, TestMessages.Make(0, i, 1000), 0, 1000);
            lingering.ClientConn.Close(43, "bye", true);
            lingering.Run(0.1, 0.01);
            Assert.Equal(5, lingering.HostSink.Messages.Count, "linger hands everything over first");
            Assert.SequenceEqual(new[] { "M", "M", "M", "M", "M", "D" }, lingering.HostSink.Events);
        }

        [Test]
        public void ARemoteDisconnectDropsTheQueue()
        {
            Rig rig = Connect(new NetSimOptions(), new NetSimOptions { BandwidthBytesPerSecond = 1000 });
            for (int i = 0; i < 5; i++) rig.HostConn.Send(Lane.Reliable, Delivery.ReliableOrdered, new byte[1000], 0, 1000);
            Assert.Equal(5000, rig.HostConn.GetStats().PendingReliableBytes);
            rig.ClientConn.Close(1, "client left", false);
            rig.Run(0.01);
            Assert.Equal(1, rig.HostSink.Disconnected.Count);
            Assert.Same(rig.HostConn, rig.HostSink.Disconnected[0].Connection);
            Assert.Equal(0, rig.HostConn.GetStats().PendingReliableBytes);
            Assert.Equal(SendResult.Closed, rig.HostConn.Send(Lane.Reliable, Delivery.ReliableOrdered, new byte[1], 0, 1));
            Assert.Equal(0f, rig.HostConn.GetStats().Quality);
        }

        [Test]
        public void StatsDescribeTheSimulatedLink()
        {
            Rig rig = Connect(new NetSimOptions { LatencyMs = 40, JitterMs = 10, LossRate = 0.05, BandwidthBytesPerSecond = 250_000 });
            ConnectionStats stats = rig.ClientConn.GetStats();
            Assert.Equal(90, stats.PingMs);
            Assert.Near(0.95, stats.Quality, 1e-6);
            Assert.Equal(250_000, stats.SendRateBytesPerSecond);
            Assert.Equal(0, stats.PendingReliableBytes);
            rig.ClientConn.Send(Lane.Unreliable, Delivery.Unreliable, new byte[100], 0, 100);
            Assert.True(rig.ClientConn.GetStats().PendingUnreliableBytes <= 100);
        }

        [Test]
        public void OptionsChangesApplyToLaterMessages()
        {
            var options = new NetSimOptions { Seed = 1 };
            Rig rig = Connect(options);
            rig.ClientConn.Send(Lane.Unreliable, Delivery.Unreliable, new byte[10], 0, 10);
            rig.Client.Options.LossRate = 1;
            for (int i = 0; i < 10; i++) rig.ClientConn.Send(Lane.Unreliable, Delivery.Unreliable, new byte[10], 0, 10);
            rig.Client.Options = new NetSimOptions { Seed = 2, LatencyMs = 10 };
            rig.ClientConn.Send(Lane.Unreliable, Delivery.Unreliable, new byte[10], 0, 10);
            rig.Run(0.02);
            Assert.Equal(2, rig.HostSink.Messages.Count);
            Assert.Equal(10L, rig.Client.Counters.UnreliableLost);
            Assert.Throws<ArgumentNullException>(() => rig.Client.Options = null);
            Assert.Throws<ArgumentNullException>(() => new NetSimTransport(null, new NetSimOptions(), rig.Clock));
        }

        [Test]
        public void DisposeDisposesTheInnerTransport()
        {
            Rig rig = Connect(new NetSimOptions());
            rig.Host.Dispose();
            rig.Run(0.01);
            Assert.Equal(1, rig.ClientSink.Disconnected.Count);
            Assert.Equal(TransportCloseReason.TransportDisposed, rig.ClientSink.Disconnected[0].Reason);
            Assert.Throws<ObjectDisposedException>(() => rig.Host.Connect(new Endpoint()));
        }
    }
}
