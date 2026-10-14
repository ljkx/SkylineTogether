using System;
using System.Collections.Generic;
using System.Threading;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Tests.Transport
{
    /// <summary>PeerOutbox (ARCHITECTURE.md#outbox-lanes-and-rate-control): nothing lost under Busy, per-lane order, windows, limits, threading.</summary>
    public sealed class PeerOutboxTests
    {
        private static PeerOutbox Outbox(FakeConnection connection, ManualClock clock, OutboxConfig config = null, ILog log = null)
        {
            return new PeerOutbox(connection, new PeerId(3), config ?? new OutboxConfig(), clock, log ?? new ListLog());
        }

        private static SendResult Send(PeerOutbox outbox, Lane lane, Delivery delivery, byte tag, int sequence, int size)
        {
            byte[] m = TestMessages.Make(tag, sequence, size);
            return outbox.Send(lane, delivery, m, 0, m.Length);
        }

        /// <summary>Busy for random stretches of 1-19 calls, then Ok for random stretches.</summary>
        private static Func<Lane, Delivery, int, SendResult> RandomBusyStretches(ulong seed)
        {
            var random = new DeterministicRandom(seed);
            int left = 0;
            bool busy = false;
            return (lane, delivery, count) =>
            {
                if (left-- <= 0)
                {
                    busy = !busy;
                    left = random.Next(1, 20);
                }
                return busy ? SendResult.Busy : SendResult.Ok;
            };
        }

        [Test]
        public void ReliableMessagesWaitForThePump()
        {
            var connection = new FakeConnection();
            var clock = new ManualClock();
            PeerOutbox outbox = Outbox(connection, clock);
            for (int i = 0; i < 3; i++) Assert.Equal(SendResult.Ok, Send(outbox, Lane.Reliable, Delivery.ReliableOrdered, 0, i, 100));
            Assert.Equal(0, connection.Sent.Count);
            Assert.Equal(3, outbox.QueuedMessages);
            Assert.Equal(300L, outbox.QueuedBytes);
            Assert.Equal(300L, outbox.QueuedReliableBytes);
            clock.Advance(2);
            Assert.Near(2, outbox.OldestQueuedSeconds, 1e-9);
            outbox.Pump();
            Assert.Equal(3, connection.Sent.Count);
            for (int i = 0; i < 3; i++) Assert.Equal(i, TestMessages.Sequence(connection.Sent[i].Data));
            Assert.Equal(3L, outbox.SentReliableMessages);
            Assert.Equal(300L, outbox.SentReliableBytes);
            Assert.Equal(0L, outbox.QueuedBytes);
            Assert.Equal(0, outbox.OldestQueuedSeconds);
            Assert.Equal(new PeerId(3), outbox.Peer);
            Assert.Same(connection, outbox.Connection);
        }

        [Test]
        public void UnreliableMessagesGoStraightToTheConnectionAndAreDroppedWhenRefused()
        {
            var connection = new FakeConnection { Decide = (l, d, n) => SendResult.Busy };
            PeerOutbox outbox = Outbox(connection, new ManualClock());
            Assert.Equal(SendResult.Busy, Send(outbox, Lane.Unreliable, Delivery.Unreliable, 1, 0, 50));
            Assert.Equal(1L, outbox.DroppedUnreliableMessages);
            Assert.Equal(0, outbox.QueuedMessages, "never queued");
            connection.Decide = null;
            Assert.Equal(SendResult.Ok, Send(outbox, Lane.Unreliable, Delivery.Unreliable, 1, 1, 50));
            Assert.Equal(1, connection.Sent.Count, "sent without a pump");
            Assert.Equal(1L, outbox.SentUnreliableMessages);
            Assert.Equal(SendResult.TooLarge, outbox.Send(Lane.Unreliable, Delivery.Unreliable, new byte[Limits.MaxMessageBytes + 1], 0, Limits.MaxMessageBytes + 1));
        }

        [Test]
        public void RandomBusyStretchesLoseNothingAndKeepPerLaneOrder()
        {
            var connection = new FakeConnection { Decide = RandomBusyStretches(42) };
            var clock = new ManualClock();
            PeerOutbox outbox = Outbox(connection, clock);
            var random = new DeterministicRandom(43);
            var lanes = new[] { Lane.Reliable, Lane.Unreliable, Lane.Bulk };
            var sent = new int[3];
            int unreliable = 0;
            for (int i = 0; i < 10000; i++)
            {
                int l = random.Next(3);
                Assert.Equal(SendResult.Ok, Send(outbox, lanes[l], Delivery.ReliableOrdered, (byte)l, sent[l]++, 5 + random.Next(2000)));
                if (random.Next(4) == 0) Send(outbox, Lane.Unreliable, Delivery.Unreliable, 9, unreliable++, 30);
                if (random.Next(3) == 0) outbox.Pump();
                clock.Advance(0.001);
            }
            for (int i = 0; i < 100000 && outbox.QueuedMessages > 0; i++) outbox.Pump();
            Assert.Equal(0, outbox.QueuedMessages);
            var expected = new int[3];
            foreach (var m in connection.Sent)
            {
                if (m.Delivery == Delivery.Unreliable) continue;
                int l = TestMessages.Tag(m.Data);
                Assert.Equal(lanes[l], m.Lane);
                Assert.Equal(expected[l]++, TestMessages.Sequence(m.Data), "per-lane order without gaps or duplicates");
            }
            Assert.SequenceEqual(sent, expected);
            Assert.True(outbox.BusyRetries > 100, "the connection refused often");
            Assert.Equal(10000L, outbox.SentReliableMessages);
            Assert.True(outbox.DroppedUnreliableMessages > 0 && outbox.SentUnreliableMessages > 0);
        }

        [Test]
        public void BulkWaitsUntilNoReliableMessageIsQueued()
        {
            var connection = new FakeConnection();
            PeerOutbox outbox = Outbox(connection, new ManualClock());
            Send(outbox, Lane.Bulk, Delivery.ReliableOrdered, 2, 0, 100);
            Send(outbox, Lane.Reliable, Delivery.ReliableOrdered, 0, 0, 100);
            Send(outbox, Lane.Unreliable, Delivery.ReliableOrdered, 1, 0, 100);
            Send(outbox, Lane.Reliable, Delivery.ReliableOrdered, 0, 1, 100);
            int calls = 0;
            connection.Decide = (l, d, n) => ++calls == 3 ? SendResult.Busy : SendResult.Ok;
            outbox.Pump();
            Assert.Equal(2, connection.Sent.Count, "R0 and R1; the reliable lane-U message got Busy");
            Assert.Equal(Lane.Reliable, connection.Sent[0].Lane);
            Assert.Equal(Lane.Reliable, connection.Sent[1].Lane);
            Assert.Equal(200L, outbox.QueuedBytes, "B waits behind the refused U message");
            outbox.Pump();
            Assert.Equal(4, connection.Sent.Count);
            Assert.Equal(Lane.Unreliable, connection.Sent[2].Lane);
            Assert.Equal(Delivery.ReliableOrdered, connection.Sent[2].Delivery);
            Assert.Equal(Lane.Bulk, connection.Sent[3].Lane);
            Assert.Equal(1L, outbox.SentBulkMessages);
            Assert.Equal(1L, outbox.BusyRetries);
        }

        [Test]
        public void TheReliableWindowIsTheSendBufferMinusTheHeadroom()
        {
            var config = new OutboxConfig { SendBufferBytes = 1000, ReliableHeadroomBytes = 100, BulkWindowMinBytes = 300, BulkWindowMaxBytes = 300 };
            var connection = new FakeConnection { TrackPending = true };
            PeerOutbox outbox = Outbox(connection, new ManualClock(), config);
            for (int i = 0; i < 10; i++) Send(outbox, Lane.Reliable, Delivery.ReliableOrdered, 0, i, 200);
            outbox.Pump();
            Assert.Equal(4, connection.Sent.Count, "0, 200, 400, 600 pending: the fifth would reach 1000 > 900");
            Assert.Equal(800, connection.PendingReliable);
            outbox.Pump();
            Assert.Equal(4, connection.Sent.Count, "nothing until the transport drains");
            connection.Drain(400);
            outbox.Pump();
            Assert.Equal(6, connection.Sent.Count);
            connection.Drain(10_000);
            outbox.Pump();
            Assert.Equal(10, connection.Sent.Count);
            Assert.Equal(0L, outbox.BusyRetries, "the window kept the transport from refusing");
        }

        [Test]
        public void AnEmptyTransportAlwaysTakesOneMessage()
        {
            var config = new OutboxConfig { SendBufferBytes = 1000, ReliableHeadroomBytes = 900, BulkWindowMinBytes = 100, BulkWindowMaxBytes = 100 };
            var connection = new FakeConnection { TrackPending = true };
            PeerOutbox outbox = Outbox(connection, new ManualClock(), config);
            Send(outbox, Lane.Reliable, Delivery.ReliableOrdered, 0, 0, 5000);
            Send(outbox, Lane.Reliable, Delivery.ReliableOrdered, 0, 1, 5000);
            outbox.Pump();
            Assert.Equal(1, connection.Sent.Count, "a message larger than the window still moves when nothing is pending");
            connection.Drain(5000);
            outbox.Pump();
            Assert.Equal(2, connection.Sent.Count);
        }

        [Test]
        public void BulkUsesTheAdaptiveWindowAndLeavesHeadroomForR()
        {
            var config = new OutboxConfig { SendBufferBytes = 10_000, ReliableHeadroomBytes = 1_000, BulkWindowMinBytes = 2_000, BulkWindowMaxBytes = 6_000 };
            var connection = new FakeConnection { TrackPending = true };
            PeerOutbox outbox = Outbox(connection, new ManualClock(), config);
            for (int i = 0; i < 20; i++) Send(outbox, Lane.Bulk, Delivery.ReliableOrdered, 2, i, 500);
            outbox.Pump();
            Assert.Equal(4, connection.Sent.Count, "an unknown send rate gives the minimum window");
            Assert.Equal(2_000, outbox.CurrentBulkWindowBytes);
            Assert.Equal(16 * 500L, outbox.QueuedBulkBytes);

            connection.SendRate = 4_000;   // one second of data = 4000 bytes
            connection.Drain(10_000);
            outbox.Pump();
            Assert.Equal(4_000, outbox.CurrentBulkWindowBytes);
            Assert.Equal(12, connection.Sent.Count);

            connection.SendRate = 1_000_000;
            connection.Drain(10_000);
            outbox.Pump();
            Assert.Equal(6_000, outbox.CurrentBulkWindowBytes, "clamped to the maximum");
            Assert.Equal(20, connection.Sent.Count);

            // R still fits beside a full bulk window
            Send(outbox, Lane.Reliable, Delivery.ReliableOrdered, 0, 0, 2000);
            Assert.True(outbox.CanAcceptReliable(1000));
            outbox.Pump();
            Assert.Equal(21, connection.Sent.Count);
        }

        [Test]
        public void CanAcceptReliableTurnsFalseAtTheWindow()
        {
            var config = new OutboxConfig { SendBufferBytes = 1000, ReliableHeadroomBytes = 100, BulkWindowMinBytes = 500, BulkWindowMaxBytes = 500 };
            var connection = new FakeConnection();
            PeerOutbox outbox = Outbox(connection, new ManualClock(), config);
            Assert.True(outbox.CanAcceptReliable(900));
            Assert.False(outbox.CanAcceptReliable(901));
            Send(outbox, Lane.Reliable, Delivery.ReliableOrdered, 0, 0, 500);
            Assert.True(outbox.CanAcceptReliable(400));
            Assert.False(outbox.CanAcceptReliable(401));
            connection.PendingReliable = 300;
            Assert.True(outbox.CanAcceptReliable(100));
            Assert.False(outbox.CanAcceptReliable(101));
            Send(outbox, Lane.Bulk, Delivery.ReliableOrdered, 2, 0, 5000);
            Assert.True(outbox.CanAcceptReliable(100), "queued bulk does not block R");
            Assert.Throws<ArgumentOutOfRangeException>(() => outbox.CanAcceptReliable(-1));
            outbox.Dispose();
            Assert.False(outbox.CanAcceptReliable(0));
        }

        [Test]
        public void OverLimitTripsAtTheQueuedBytesThreshold()
        {
            var log = new ListLog();
            var config = new OutboxConfig { DisconnectQueuedBytes = 10_000 };
            var connection = new FakeConnection { Decide = (l, d, n) => SendResult.Busy };
            PeerOutbox outbox = Outbox(connection, new ManualClock(), config, log);
            for (int i = 0; i < 10; i++) Send(outbox, Lane.Reliable, Delivery.ReliableOrdered, 0, i, 1000);
            outbox.Pump();
            Assert.False(outbox.OverLimit, "exactly at the limit");
            Send(outbox, Lane.Bulk, Delivery.ReliableOrdered, 2, 0, 1);
            Assert.True(outbox.OverLimit, "bulk counts as well");
            outbox.Pump();
            Assert.True(log.Contains("over the disconnect limit"));
            connection.Decide = null;
            outbox.Pump();
            Assert.False(outbox.OverLimit);
            Assert.True(log.Contains("back under the disconnect limit"));
            Assert.Equal(11L, outbox.SentReliableMessages);
        }

        [Test]
        public void OverLimitTripsAtTheAgeThreshold()
        {
            var clock = new ManualClock();
            var connection = new FakeConnection { Decide = (l, d, n) => SendResult.Busy };
            PeerOutbox outbox = Outbox(connection, clock);
            Send(outbox, Lane.Reliable, Delivery.ReliableOrdered, 0, 0, 10);
            clock.Advance(59.9);
            outbox.Pump();
            Assert.False(outbox.OverLimit);
            clock.Advance(0.2);
            Assert.Near(60.1, outbox.OldestQueuedSeconds, 1e-6);
            Assert.True(outbox.OverLimit, "older than 60 s");
        }

        [Test]
        public void ALongBusyStreakIsLogged()
        {
            var log = new ListLog();
            var clock = new ManualClock();
            var connection = new FakeConnection { Decide = (l, d, n) => SendResult.Busy };
            PeerOutbox outbox = Outbox(connection, clock, null, log);
            Send(outbox, Lane.Reliable, Delivery.ReliableOrdered, 0, 0, 10);
            for (int i = 0; i < 70; i++)
            {
                outbox.Pump();
                clock.Advance(0.1);
            }
            Assert.True(log.Contains("refused sends for"));
            Assert.Equal(70L, outbox.BusyRetries);
        }

        [Test]
        public void TooLargeReliableMessagesAreRefusedAtOnce()
        {
            var log = new ListLog();
            var connection = new FakeConnection();
            PeerOutbox outbox = Outbox(connection, new ManualClock(), null, log);
            var big = new byte[Limits.MaxMessageBytes + 1];
            Assert.Equal(SendResult.TooLarge, outbox.Send(Lane.Reliable, Delivery.ReliableOrdered, big, 0, big.Length));
            Assert.Equal(0, outbox.QueuedMessages);
            Assert.True(log.Count(LogLevel.Error) == 1);
            Assert.Equal(SendResult.Ok, outbox.Send(Lane.Reliable, Delivery.ReliableOrdered, big, 0, Limits.MaxMessageBytes));
            Assert.Equal(SendResult.Ok, outbox.Send(Lane.Reliable, Delivery.ReliableOrdered, null, 0, 0));
            outbox.Pump();
            Assert.Equal(2, connection.Sent.Count);
            Assert.Equal(0, connection.Sent[1].Data.Length);
        }

        [Test]
        public void AClosedConnectionDropsTheQueueAndRefusesSends()
        {
            var connection = new FakeConnection();
            PeerOutbox outbox = Outbox(connection, new ManualClock());
            for (int i = 0; i < 3; i++) Send(outbox, Lane.Reliable, Delivery.ReliableOrdered, 0, i, 100);
            connection.Decide = (l, d, n) => SendResult.Closed;
            outbox.Pump();
            Assert.True(outbox.IsClosed);
            Assert.Equal(0L, outbox.QueuedBytes);
            Assert.Equal(0, outbox.QueuedMessages);
            Assert.Equal(SendResult.Closed, Send(outbox, Lane.Reliable, Delivery.ReliableOrdered, 0, 3, 100));
            Assert.Equal(SendResult.Closed, Send(outbox, Lane.Unreliable, Delivery.Unreliable, 0, 0, 100));
            Assert.False(outbox.CanAcceptReliable(1));
            Assert.False(outbox.OverLimit, "a closed connection is handled by the disconnect, not by the limit");

            // an unreliable send that finds the connection closed closes the outbox as well
            var second = new FakeConnection();
            PeerOutbox other = Outbox(second, new ManualClock());
            Send(other, Lane.Reliable, Delivery.ReliableOrdered, 0, 0, 100);
            second.Decide = (l, d, n) => SendResult.Closed;
            Assert.Equal(SendResult.Closed, Send(other, Lane.Unreliable, Delivery.Unreliable, 0, 0, 10));
            Assert.True(other.IsClosed);
            other.Pump();
            Assert.Equal(0, other.QueuedMessages, "the next pump releases the queue");
        }

        [Test]
        public void ATransportThatCannotTakeAQueuedMessageFaultsTheOutbox()
        {
            var log = new ListLog();
            var connection = new FakeConnection { Decide = (l, d, n) => SendResult.TooLarge };
            PeerOutbox outbox = Outbox(connection, new ManualClock(), null, log);
            Send(outbox, Lane.Reliable, Delivery.ReliableOrdered, 0, 0, 100);
            outbox.Pump();
            Assert.True(outbox.IsFaulted);
            Assert.True(outbox.OverLimit, "the session disconnects a faulted peer like a slow one");
            Assert.Contains("too large", outbox.FaultReason);
            Assert.Equal(SendResult.Closed, Send(outbox, Lane.Reliable, Delivery.ReliableOrdered, 0, 1, 100));
            Assert.Equal(1, log.Count(LogLevel.Error));
        }

        [Test]
        public void FailedSendsStayQueuedAndAreRetried()
        {
            int calls = 0;
            var connection = new FakeConnection { Decide = (l, d, n) => ++calls <= 3 ? SendResult.Failed : SendResult.Ok };
            PeerOutbox outbox = Outbox(connection, new ManualClock());
            Send(outbox, Lane.Reliable, Delivery.ReliableOrdered, 0, 0, 100);
            Send(outbox, Lane.Reliable, Delivery.ReliableOrdered, 0, 1, 100);
            for (int i = 0; i < 3; i++)
            {
                outbox.Pump();
                Assert.Equal(0, connection.Sent.Count);
            }
            outbox.Pump();
            Assert.Equal(2, connection.Sent.Count);
            Assert.Equal(3L, outbox.SendFailures);
            Assert.Equal(0, TestMessages.Sequence(connection.Sent[0].Data));
        }

        [Test]
        public void TransportExceptionsNeverEscapeThePump()
        {
            var log = new ListLog();
            var connection = new FakeConnection();
            PeerOutbox outbox = Outbox(connection, new ManualClock(), null, log);
            connection.PendingReliable = 77;
            Assert.Equal(77, outbox.Stats.PendingReliableBytes);
            Send(outbox, Lane.Reliable, Delivery.ReliableOrdered, 0, 0, 100);
            connection.Decide = (l, d, n) => throw new InvalidOperationException("native error");
            connection.ThrowOnStats = true;
            outbox.Pump();
            Assert.Equal(1, outbox.QueuedMessages, "the message stays queued");
            Assert.Equal(1L, outbox.SendFailures);
            Assert.Equal(77, outbox.Stats.PendingReliableBytes, "the last known statistics");
            Assert.True(log.Count(LogLevel.Warn) >= 1);
            Assert.Equal(SendResult.Failed, Send(outbox, Lane.Unreliable, Delivery.Unreliable, 0, 0, 10));
            connection.Decide = null;
            connection.ThrowOnStats = false;
            outbox.Pump();
            Assert.Equal(1, connection.Sent.Count);
        }

        [Test(TimeoutSeconds = 120)]
        public void ProducersOnManyThreadsKeepTheirOrderWhileAPumpThreadDrains()
        {
            const int threads = 4;
            const int perThread = 5000;
            var connection = new FakeConnection { Decide = RandomBusyStretches(7) };
            PeerOutbox outbox = new PeerOutbox(connection, new PeerId(1), new OutboxConfig(), new StopwatchClock(), new ListLog());
            int producersDone = 0;
            Exception failure = null;
            var pump = new Thread(() =>
            {
                try
                {
                    while (Volatile.Read(ref producersDone) < threads || outbox.QueuedMessages > 0) outbox.Pump();
                }
                catch (Exception e)
                {
                    failure = e;
                }
            });
            var producers = new Thread[threads];
            for (int t = 0; t < threads; t++)
            {
                byte tag = (byte)t;
                producers[t] = new Thread(() =>
                {
                    try
                    {
                        for (int i = 0; i < perThread; i++)
                        {
                            byte[] m = TestMessages.Make(tag, i, 8 + (i % 64));
                            Lane lane = i % 5 == 0 ? Lane.Bulk : Lane.Reliable;
                            if (outbox.Send(lane, Delivery.ReliableOrdered, m, 0, m.Length) != SendResult.Ok) throw new InvalidOperationException("refused");
                            if (i % 7 == 0) outbox.Send(Lane.Unreliable, Delivery.Unreliable, m, 0, m.Length);
                            outbox.CanAcceptReliable(100);
                            _ = outbox.OldestQueuedSeconds;
                        }
                    }
                    catch (Exception e)
                    {
                        failure = e;
                    }
                    finally
                    {
                        Interlocked.Increment(ref producersDone);
                    }
                });
            }
            pump.Start();
            foreach (Thread p in producers) p.Start();
            foreach (Thread p in producers) p.Join();
            pump.Join();
            Assert.Null(failure);
            // per producer and lane, the messages left in the order they were accepted
            var next = new Dictionary<(int, Lane), int>();
            int reliable = 0;
            foreach (var m in connection.SentSnapshot())
            {
                if (m.Delivery == Delivery.Unreliable) continue;
                reliable++;
                int tag = TestMessages.Tag(m.Data);
                int seq = TestMessages.Sequence(m.Data);
                next.TryGetValue((tag, m.Lane), out int expectedAtLeast);
                Assert.True(seq >= expectedAtLeast, "order within a producer and lane");
                Assert.Equal(m.Lane, seq % 5 == 0 ? Lane.Bulk : Lane.Reliable);
                next[(tag, m.Lane)] = seq + 1;
            }
            Assert.Equal(threads * perThread, reliable, "every reliable message exactly once");
            Assert.Equal(0L, outbox.QueuedBytes);
        }

        [Test]
        public void DisposeReleasesTheQueue()
        {
            var connection = new FakeConnection { Decide = (l, d, n) => SendResult.Busy };
            PeerOutbox outbox = Outbox(connection, new ManualClock());
            for (int i = 0; i < 5; i++) Send(outbox, Lane.Bulk, Delivery.ReliableOrdered, 2, i, 1000);
            outbox.Dispose();
            Assert.Equal(0L, outbox.QueuedBytes);
            Assert.True(outbox.IsClosed);
            Assert.Equal(SendResult.Closed, Send(outbox, Lane.Reliable, Delivery.ReliableOrdered, 0, 0, 10));
            outbox.Pump();
            outbox.Dispose();
            Assert.False(connection.Closed, "the outbox never closes the connection");
        }

        [Test]
        public void ArgumentsAndConfigurationAreValidated()
        {
            var clock = new ManualClock();
            Assert.Throws<ArgumentNullException>(() => new PeerOutbox(null, new PeerId(1), new OutboxConfig(), clock, null));
            Assert.Throws<ArgumentNullException>(() => new PeerOutbox(new FakeConnection(), new PeerId(1), new OutboxConfig(), null, null));
            Assert.Throws<ArgumentException>(() => new PeerOutbox(new FakeConnection(), new PeerId(1), new OutboxConfig { ReliableHeadroomBytes = 16 << 20 }, clock, null));
            Assert.Throws<ArgumentException>(() => new PeerOutbox(new FakeConnection(), new PeerId(1), new OutboxConfig { BulkWindowMinBytes = 9 << 20 }, clock, null));
            Assert.Throws<ArgumentException>(() => new OutboxConfig { DisconnectQueuedSeconds = double.NaN }.Validate());
            Assert.Throws<ArgumentException>(() => new OutboxConfig { SendBufferBytes = 0 }.Validate());
            var outbox = new PeerOutbox(new FakeConnection(), new PeerId(1), null, clock, null);   // null config = defaults, null log tolerated
            Assert.Equal((16 << 20) - (1 << 20), outbox.Config.ReliableWindowBytes);
            Assert.Throws<ArgumentOutOfRangeException>(() => outbox.Send((Lane)3, Delivery.ReliableOrdered, new byte[1], 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => outbox.Send(Lane.Reliable, (Delivery)5, new byte[1], 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => outbox.Send(Lane.Reliable, Delivery.ReliableOrdered, new byte[1], 1, 1));
            Assert.Throws<ArgumentNullException>(() => outbox.Send(Lane.Reliable, Delivery.ReliableOrdered, null, 0, 1));
        }

        [Test]
        public void TheConfigurationIsCopied()
        {
            var config = new OutboxConfig { SendBufferBytes = 4000, ReliableHeadroomBytes = 1000, BulkWindowMinBytes = 1000, BulkWindowMaxBytes = 2000 };
            var connection = new FakeConnection();
            PeerOutbox outbox = Outbox(connection, new ManualClock(), config);
            config.SendBufferBytes = 1;
            Assert.True(outbox.CanAcceptReliable(3000), "later changes of the caller's object have no effect");
            Assert.Contains("send buffer 4000", outbox.Config.ToString());
        }

        [Test(TimeoutSeconds = 120)]
        public void OverNetSimWithBusyRateEveryMessageArrivesOnceInOrder()
        {
            var clock = new ManualClock(1);
            var (h, c) = LoopbackTransport.CreatePair();
            var options = new NetSimOptions { LatencyMs = 15, JitterMs = 10, LossRate = 0.05, BandwidthBytesPerSecond = 3_000_000, Seed = 21 };
            var host = new NetSimTransport(h, new NetSimOptions(), clock);
            var client = new NetSimTransport(c, options, clock) { BusyRate = 0.3 };
            host.Listen(new ListenOptions());
            IConnection connection = client.Connect(new Endpoint());
            var hostSink = new RecordingSink();
            var clientSink = new RecordingSink();
            var outbox = new PeerOutbox(connection, PeerId.Host, new OutboxConfig(), clock, new ListLog());
            var random = new DeterministicRandom(22);
            var lanes = new[] { Lane.Reliable, Lane.Unreliable, Lane.Bulk };
            var sent = new int[3];
            for (int step = 0; step < 6000; step++)
            {
                if (step < 3000)
                {
                    int l = random.Next(3);
                    byte[] m = TestMessages.Make((byte)l, sent[l]++, 5 + random.Next(30_000));
                    Assert.Equal(SendResult.Ok, outbox.Send(lanes[l], Delivery.ReliableOrdered, m, 0, m.Length));
                }
                outbox.Pump();
                clock.Advance(0.001);
                client.Poll(clientSink);
                host.Poll(hostSink);
            }
            for (int i = 0; i < 20000 && hostSink.Messages.Count < 3000; i++)
            {
                outbox.Pump();
                clock.Advance(0.001);
                client.Poll(clientSink);
                host.Poll(hostSink);
            }
            var expected = new int[3];
            foreach (ReceivedMessage m in hostSink.Messages)
            {
                int l = TestMessages.Tag(m.Data);
                Assert.Equal(expected[l]++, TestMessages.Sequence(m.Data));
            }
            Assert.SequenceEqual(sent, expected);
            Assert.True(outbox.BusyRetries > 0 && client.Counters.BusyRateRefusals > 0);
            Assert.Equal(0, outbox.QueuedMessages);
        }
    }
}
