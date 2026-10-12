using System;
using System.Collections.Generic;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Tests.Transport
{
    /// <summary>LoopbackTransport (ARCH §9.1): connection events, per-lane ordering, back-pressure, close semantics.</summary>
    public sealed class LoopbackTransportTests
    {
        private static (LoopbackTransport host, LoopbackTransport client, IConnection clientConn, IConnection hostConn, RecordingSink hostSink, RecordingSink clientSink) Connected()
        {
            var (host, client) = LoopbackTransport.CreatePair();
            host.Listen(new ListenOptions());
            IConnection clientConn = client.Connect(new Endpoint());
            var hostSink = new RecordingSink();
            var clientSink = new RecordingSink();
            host.Poll(hostSink);
            client.Poll(clientSink);
            Assert.Equal(1, hostSink.Connected.Count);
            Assert.Equal(1, clientSink.Connected.Count);
            Assert.Same(clientConn, clientSink.Connected[0]);
            return (host, client, clientConn, hostSink.Connected[0], hostSink, clientSink);
        }

        [Test]
        public void ConnectReportsBothEndsWithTheirPeerIdentities()
        {
            var (host, client, clientConn, hostConn, _, _) = Connected();
            Assert.Equal(TransportKind.Loopback, host.Kind);
            Assert.Equal(host.Identity, clientConn.Peer, "the client sees the host");
            Assert.Equal(client.Identity, hostConn.Peer, "the host sees the client");
            Assert.NotEqual(host.Identity, client.Identity);
            Assert.True(host.Identity.SteamId != 0 && host.Identity.Address.StartsWith("loopback:", StringComparison.Ordinal));
            Assert.NotEqual(clientConn.Id, hostConn.Id);
            Assert.Equal(1, host.ConnectionCount);
            Assert.Equal(1, client.ConnectionCount);
        }

        [Test]
        public void CustomIdentitiesAreReported()
        {
            var hostId = new PeerIdentity(111, "10.0.0.1:1", "Host");
            var clientId = new PeerIdentity(222, "10.0.0.2:2", "Anna");
            var (host, client) = LoopbackTransport.CreatePair(hostId, clientId);
            host.Listen(new ListenOptions());
            IConnection c = client.Connect(new Endpoint());
            var sink = new RecordingSink();
            host.Poll(sink);
            Assert.Equal(clientId, sink.Connected[0].Peer);
            Assert.Equal("Anna", sink.Connected[0].Peer.DisplayName);
            Assert.Equal(hostId, c.Peer);
        }

        [Test]
        public void ConnectedEventPrecedesTheFirstMessage()
        {
            var (host, client) = LoopbackTransport.CreatePair();
            host.Listen(new ListenOptions());
            IConnection c = client.Connect(new Endpoint());
            Assert.Equal(SendResult.Ok, c.Send(Lane.Reliable, Delivery.ReliableOrdered, new byte[] { 1, 2, 3 }, 0, 3));
            var sink = new RecordingSink();
            host.Poll(sink);
            Assert.SequenceEqual(new[] { "C", "M" }, sink.Events);
            Assert.Same(sink.Connected[0], sink.Messages[0].Connection);
        }

        [Test]
        public void MessagesArriveOnceInSendOrderPerLane()
        {
            var (host, _, clientConn, _, hostSink, _) = Connected();
            var lanes = new[] { Lane.Reliable, Lane.Unreliable, Lane.Bulk };
            var counters = new int[3];
            var random = new SkylineTogether.Core.Testing.DeterministicRandom(5);
            for (int i = 0; i < 3000; i++)
            {
                int l = random.Next(3);
                Delivery d = lanes[l] == Lane.Unreliable ? Delivery.Unreliable : Delivery.ReliableOrdered;
                byte[] m = TestMessages.Make((byte)l, counters[l]++, 5 + random.Next(300));
                Assert.Equal(SendResult.Ok, clientConn.Send(lanes[l], d, m, 0, m.Length));
            }
            hostSink.Clear();
            host.Poll(hostSink);
            Assert.Equal(3000, hostSink.Messages.Count);
            var expected = new int[3];
            foreach (ReceivedMessage m in hostSink.Messages)
            {
                int l = TestMessages.Tag(m.Data);
                Assert.Equal(lanes[l], m.Lane);
                Assert.Equal(expected[l]++, TestMessages.Sequence(m.Data), "per-lane order");
            }
            Assert.SequenceEqual(counters, expected);
        }

        [Test]
        public void ReplyDuringPollIsDeliveredOnTheNextPoll()
        {
            var (host, client, clientConn, hostConn, hostSink, clientSink) = Connected();
            clientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, new byte[] { 1 }, 0, 1);
            // the client echoes everything it receives; a ping-pong must not loop inside one poll
            clientSink.MessageHook = (c, lane, b, o, n) => c.Send(lane, Delivery.ReliableOrdered, b, o, n);
            hostSink.MessageHook = (c, lane, b, o, n) => c.Send(lane, Delivery.ReliableOrdered, b, o, n);
            host.Poll(hostSink);
            Assert.Equal(1, hostSink.Messages.Count);
            client.Poll(clientSink);
            Assert.Equal(1, clientSink.Messages.Count);
            host.Poll(hostSink);
            Assert.Equal(2, hostSink.Messages.Count);
            _ = hostConn;
        }

        [Test]
        public void SendBufferFullAnswersBusyUntilTheReceiverPolls()
        {
            var (host, client, clientConn, _, hostSink, _) = Connected();
            client.SendBufferBytes = 1000;
            var m = new byte[400];
            Assert.Equal(SendResult.Ok, clientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, m, 0, 400));
            Assert.Equal(SendResult.Ok, clientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, m, 0, 400));
            Assert.Equal(800, clientConn.GetStats().PendingReliableBytes);
            Assert.Equal(SendResult.Busy, clientConn.Send(Lane.Bulk, Delivery.ReliableOrdered, m, 0, 400), "1200 > 1000");
            Assert.Equal(SendResult.Busy, clientConn.Send(Lane.Unreliable, Delivery.Unreliable, m, 0, 400));
            host.Poll(hostSink);
            Assert.Equal(2, hostSink.Messages.Count, "a refused message was not taken");
            Assert.Equal(0, clientConn.GetStats().PendingReliableBytes);
            Assert.Equal(SendResult.Ok, clientConn.Send(Lane.Bulk, Delivery.ReliableOrdered, m, 0, 400));

            // a message into an empty buffer is always taken, even above the limit
            host.Poll(hostSink);
            var big = new byte[5000];
            Assert.Equal(SendResult.Ok, clientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, big, 0, big.Length));
            // 0 = unlimited
            client.SendBufferBytes = 0;
            for (int i = 0; i < 100; i++) Assert.Equal(SendResult.Ok, clientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, big, 0, big.Length));
            Assert.Equal(101 * 5000, clientConn.GetStats().PendingReliableBytes);
            Assert.Throws<ArgumentOutOfRangeException>(() => client.SendBufferBytes = -1);
        }

        [Test]
        public void MessagesAboveTheSteamCapAreRefused()
        {
            var (_, _, clientConn, _, _, _) = Connected();
            var big = new byte[Limits.MaxMessageBytes + 1];
            Assert.Equal(SendResult.TooLarge, clientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, big, 0, big.Length));
            Assert.Equal(SendResult.Ok, clientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, big, 0, Limits.MaxMessageBytes));
            Assert.Equal(SendResult.Failed, clientConn.Send((Lane)7, Delivery.ReliableOrdered, big, 0, 10), "undefined lane");
            Assert.Throws<ArgumentOutOfRangeException>(() => clientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, big, 10, big.Length));
            Assert.Throws<ArgumentNullException>(() => clientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, null, 0, 0));
        }

        [Test]
        public void CloseWithLingerDeliversPendingMessagesThenTheDisconnect()
        {
            var (host, _, clientConn, hostConn, hostSink, _) = Connected();
            for (int i = 0; i < 5; i++) clientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, TestMessages.Make(0, i, 8), 0, 8);
            clientConn.Close(1234, "bye", true);
            Assert.Equal(SendResult.Closed, clientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, new byte[1], 0, 1));
            hostSink.Clear();
            host.Poll(hostSink);
            Assert.SequenceEqual(new[] { "M", "M", "M", "M", "M", "D" }, hostSink.Events);
            Assert.Equal(5, hostSink.Messages.Count);
            Assert.Equal(1, hostSink.Disconnected.Count);
            Assert.Same(hostConn, hostSink.Disconnected[0].Connection);
            Assert.Equal(1234, hostSink.Disconnected[0].Reason);
            Assert.Equal("bye", hostSink.Disconnected[0].Text);
            Assert.Equal(0, host.ConnectionCount);
            Assert.Equal(SendResult.Closed, hostConn.Send(Lane.Reliable, Delivery.ReliableOrdered, new byte[1], 0, 1));
        }

        [Test]
        public void CloseWithoutLingerDiscardsUndeliveredMessages()
        {
            var (host, client, clientConn, hostConn, hostSink, clientSink) = Connected();
            for (int i = 0; i < 5; i++) clientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, new byte[100], 0, 100);
            hostConn.Send(Lane.Reliable, Delivery.ReliableOrdered, new byte[10], 0, 10);
            clientConn.Close(77, "gone", false);
            host.Poll(hostSink);
            Assert.Equal(0, hostSink.Messages.Count, "undelivered messages of a non-lingering close are lost");
            Assert.Equal(1, hostSink.Disconnected.Count);
            client.Poll(clientSink);
            Assert.Equal(0, clientSink.Messages.Count, "the closed end receives nothing more");
            Assert.Equal(0, clientSink.Disconnected.Count, "no event for a connection one closed oneself");
            Assert.Equal(0, clientConn.GetStats().PendingReliableBytes);
        }

        [Test]
        public void ConnectWithoutListenerOrToAFullListenerFails()
        {
            var (host, client) = LoopbackTransport.CreatePair();
            IConnection c = client.Connect(new Endpoint());
            var sink = new RecordingSink();
            client.Poll(sink);
            Assert.Equal(0, sink.Connected.Count);
            Assert.Equal(1, sink.Disconnected.Count);
            Assert.Same(c, sink.Disconnected[0].Connection);
            Assert.Equal(TransportCloseReason.NoListener, sink.Disconnected[0].Reason);
            Assert.Equal(SendResult.Closed, c.Send(Lane.Reliable, Delivery.ReliableOrdered, new byte[1], 0, 1));

            host.Listen(new ListenOptions { MaxConnections = 1 });
            client.Connect(new Endpoint());
            LoopbackTransport second = host.CreateClient();
            second.Connect(new Endpoint());
            var sink2 = new RecordingSink();
            second.Poll(sink2);
            Assert.Equal(1, sink2.Disconnected.Count);
            Assert.Equal(TransportCloseReason.ListenerFull, sink2.Disconnected[0].Reason);
            Assert.Throws<ArgumentOutOfRangeException>(() => host.Listen(new ListenOptions { MaxConnections = 0 }));
        }

        [Test]
        public void SeveralClientsConnectToOneHost()
        {
            var (host, client) = LoopbackTransport.CreatePair();
            host.Listen(new ListenOptions { MaxConnections = 8 });
            var clients = new List<LoopbackTransport> { client, host.CreateClient(), host.CreateClient(new PeerIdentity(99, null, "Third")) };
            var conns = new List<IConnection>();
            foreach (LoopbackTransport c in clients) conns.Add(c.Connect(new Endpoint()));
            for (int i = 0; i < conns.Count; i++) conns[i].Send(Lane.Reliable, Delivery.ReliableOrdered, TestMessages.Make((byte)i, i, 6), 0, 6);
            var sink = new RecordingSink();
            host.Poll(sink);
            Assert.Equal(3, sink.Connected.Count);
            Assert.Equal(3, sink.Messages.Count);
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(clients[i].Identity, sink.Messages[i].Connection.Peer);
                Assert.Equal(i, TestMessages.Sequence(sink.Messages[i].Data));
            }
            Assert.Equal(new PeerIdentity(99, null, "Third"), sink.Messages[2].Connection.Peer);
        }

        [Test]
        public void EndpointsSelectTheListenerBySteamIdOrAddress()
        {
            var (host, client) = LoopbackTransport.CreatePair();
            LoopbackTransport other = host.CreateClient();
            other.Listen(new ListenOptions());
            host.Listen(new ListenOptions());
            IConnection toOther = client.Connect(new Endpoint { SteamId = other.Identity.SteamId });
            Assert.Equal(other.Identity, toOther.Peer);
            IConnection byAddress = client.Connect(new Endpoint { Host = host.Identity.Address });
            Assert.Equal(host.Identity, byAddress.Peer);
            IConnection unknown = client.Connect(new Endpoint { SteamId = 12345 });
            var sink = new RecordingSink();
            client.Poll(sink);
            Assert.Equal(2, sink.Connected.Count);
            Assert.Equal(1, sink.Disconnected.Count);
            Assert.Same(unknown, sink.Disconnected[0].Connection);
        }

        [Test]
        public void DisposeClosesEveryConnection()
        {
            var (host, client, clientConn, _, _, clientSink) = Connected();
            host.Dispose();
            host.Dispose();
            client.Poll(clientSink);
            Assert.Equal(1, clientSink.Disconnected.Count);
            Assert.Equal(TransportCloseReason.TransportDisposed, clientSink.Disconnected[0].Reason);
            Assert.Equal(SendResult.Closed, clientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, new byte[1], 0, 1));
            Assert.Throws<ObjectDisposedException>(() => host.Connect(new Endpoint()));
            host.Poll(new RecordingSink());   // a no-op after dispose
            IConnection again = client.Connect(new Endpoint());
            client.Poll(clientSink);
            Assert.Equal(TransportCloseReason.NoListener, clientSink.Disconnected[1].Reason, "the disposed host no longer listens");
            _ = again;
        }

        [Test]
        public void SinkExceptionsPropagateAndKeepTheRemainingEvents()
        {
            var (host, _, clientConn, _, hostSink, _) = Connected();
            for (int i = 0; i < 3; i++) clientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, TestMessages.Make(0, i, 5), 0, 5);
            int calls = 0;
            hostSink.MessageHook = (c, l, b, o, n) =>
            {
                if (calls++ == 0) throw new InvalidOperationException("handler bug");
            };
            Assert.Throws<InvalidOperationException>(() => host.Poll(hostSink));
            Assert.Equal(2, host.PendingEvents);
            host.Poll(hostSink);
            Assert.Equal(3, hostSink.Messages.Count);
            Assert.Equal(2, TestMessages.Sequence(hostSink.Messages[2].Data));
        }

        [Test]
        public void StatsReportThePendingBacklog()
        {
            var (host, _, clientConn, _, hostSink, _) = Connected();
            clientConn.Send(Lane.Reliable, Delivery.ReliableOrdered, new byte[300], 0, 300);
            clientConn.Send(Lane.Unreliable, Delivery.Unreliable, new byte[50], 0, 50);
            ConnectionStats stats = clientConn.GetStats();
            Assert.Equal(300, stats.PendingReliableBytes);
            Assert.Equal(50, stats.PendingUnreliableBytes);
            Assert.Equal(1f, stats.Quality);
            host.Poll(hostSink);
            stats = clientConn.GetStats();
            Assert.Equal(0, stats.PendingReliableBytes);
            Assert.Equal(0, stats.PendingUnreliableBytes);
        }
    }
}
