using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;

namespace SkylineTogether.Core.Transport
{
    /// <summary>
    /// In-process transport for tests (ARCHITECTURE.md#transport): transports created by <see cref="CreatePair()"/> (and further clients
    /// from <see cref="CreateClient()"/>) form a private network; nothing touches a socket.
    /// <para>
    /// Semantics follow Steam Networking Sockets where tests depend on them: a transport accepts connections after
    /// <see cref="Listen"/> (up to <see cref="ListenOptions.MaxConnections"/>); <see cref="Connect"/> returns at once
    /// and both ends see <see cref="ITransportEventSink.OnConnected"/> on their next <see cref="Poll"/>, before any
    /// message; a failed attempt reports <see cref="ITransportEventSink.OnDisconnected"/> with
    /// <see cref="TransportCloseReason.NoListener"/> or <see cref="TransportCloseReason.ListenerFull"/>. Messages
    /// arrive exactly once, in send order (so every lane is ordered, also the unreliable one), whole, on the next poll
    /// of the receiver. <see cref="IConnection.Send"/> copies the bytes, refuses messages above
    /// <see cref="Limits.MaxMessageBytes"/> (<see cref="SendResult.TooLarge"/>) and answers <see cref="SendResult.Busy"/>
    /// while the bytes the receiver has not polled yet exceed <see cref="SendBufferBytes"/> (Steam's
    /// <c>k_EResultLimitExceeded</c>); that backlog is reported as the pending bytes of
    /// <see cref="IConnection.GetStats"/>. Closing a connection notifies only the other end (Steam gives no callback
    /// for a connection one closes oneself); with <c>linger</c> the other end first receives every message sent
    /// before the close, without it those are discarded.
    /// </para>
    /// <para>Thread-safe: any thread may send; one thread at a time should poll a transport.</para>
    /// </summary>
    public sealed class LoopbackTransport : ITransport
    {
        /// <summary>Default <see cref="SendBufferBytes"/>, the size the game configures on Steam connections (ARCHITECTURE.md#blob-transfer).</summary>
        public const int DefaultSendBufferBytes = 16 << 20;

        private enum EventKind : byte { Connected, Disconnected, Message }

        private struct LoopbackEvent
        {
            public EventKind Kind;
            public LoopbackConnection Connection;   // the receiving end
            public Lane Lane;
            public Delivery Delivery;
            public byte[] Buffer;
            public int Count;
            public int Reason;
            public string Text;
        }

        private readonly LoopbackNetwork _network;
        private readonly PeerIdentity _identity;
        private readonly List<LoopbackConnection> _connections = new List<LoopbackConnection>();
        private readonly RingQueue<LoopbackEvent> _events = new RingQueue<LoopbackEvent>(64);
        private LoopbackTransport _defaultTarget;
        private ListenOptions _listen;
        private int _sendBufferBytes = DefaultSendBufferBytes;
        private bool _disposed;

        private LoopbackTransport(LoopbackNetwork network, PeerIdentity identity)
        {
            _network = network;
            _identity = identity;
        }

        /// <summary>A connected pair on a new private network: the client connects to the host by default.</summary>
        public static (LoopbackTransport host, LoopbackTransport client) CreatePair()
        {
            return CreatePair(default, default);
        }

        /// <summary>
        /// A pair with the given identities (what each end reports as <see cref="IConnection.Peer"/> of the other);
        /// a default identity is replaced by a synthetic one (SteamID, <c>loopback:n</c> address, display name).
        /// </summary>
        public static (LoopbackTransport host, LoopbackTransport client) CreatePair(PeerIdentity hostIdentity, PeerIdentity clientIdentity)
        {
            var network = new LoopbackNetwork();
            lock (network.Lock)
            {
                var host = new LoopbackTransport(network, IsDefault(hostIdentity) ? network.NextIdentity("host") : hostIdentity);
                var client = new LoopbackTransport(network, IsDefault(clientIdentity) ? network.NextIdentity("client") : clientIdentity);
                client._defaultTarget = host;
                network.Transports.Add(host);
                network.Transports.Add(client);
                return (host, client);
            }
        }

        /// <summary>Another client on this transport's network whose default connection target is this transport.</summary>
        public LoopbackTransport CreateClient()
        {
            return CreateClient(default);
        }

        /// <summary>Another client with the given identity (a default identity gets a synthetic one).</summary>
        public LoopbackTransport CreateClient(PeerIdentity identity)
        {
            lock (_network.Lock)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(LoopbackTransport));
                var client = new LoopbackTransport(_network, IsDefault(identity) ? _network.NextIdentity("client") : identity)
                {
                    _defaultTarget = this
                };
                _network.Transports.Add(client);
                return client;
            }
        }

        public TransportKind Kind => TransportKind.Loopback;

        /// <summary>This transport's identity, which the other end of each of its connections reports as its peer.</summary>
        public PeerIdentity Identity => _identity;

        /// <summary>
        /// Per connection of this transport: the most bytes it may have sent that the receiver has not polled yet;
        /// beyond it <see cref="IConnection.Send"/> answers <see cref="SendResult.Busy"/>. 0 = unlimited. A message
        /// into an empty buffer is always accepted.
        /// </summary>
        public int SendBufferBytes
        {
            get { lock (_network.Lock) return _sendBufferBytes; }
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                lock (_network.Lock) _sendBufferBytes = value;
            }
        }

        /// <summary>
        /// The send rate this transport's connections report in <see cref="IConnection.GetStats"/> (0: unknown, the
        /// default). Solo testing gives the recorder's link a realistic rate so the host paces it like a remote player.
        /// </summary>
        public int ReportedSendRateBytesPerSecond
        {
            get { lock (_network.Lock) return _reportedSendRate; }
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                lock (_network.Lock) _reportedSendRate = value;
            }
        }

        private int _reportedSendRate;

        public bool IsListening
        {
            get { lock (_network.Lock) return _listen != null; }
        }

        /// <summary>Open connections of this transport (incoming and outgoing).</summary>
        public int ConnectionCount
        {
            get { lock (_network.Lock) return _connections.Count; }
        }

        /// <summary>A snapshot of this transport's open connections (incoming and outgoing), for tests.</summary>
        public IConnection[] GetConnections()
        {
            lock (_network.Lock) return _connections.ToArray();
        }

        /// <summary>Events waiting for the next <see cref="Poll"/>.</summary>
        public int PendingEvents
        {
            get { lock (_network.Lock) return _events.Count; }
        }

        [ThreadAffinity(ThreadKind.Main)]
        public void Listen(ListenOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.MaxConnections <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxConnections must be positive");
            lock (_network.Lock)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(LoopbackTransport));
                _listen = new ListenOptions
                {
                    VirtualPort = options.VirtualPort,
                    IpPort = options.IpPort,
                    AllowIp = options.AllowIp,
                    MaxConnections = options.MaxConnections
                };
            }
        }

        /// <summary>
        /// Connects to the listening transport named by <paramref name="endpoint"/> (its SteamID, or its
        /// <c>loopback:n</c> address as host) or, when the endpoint names none (or is null), to this transport's
        /// default target. The result arrives as an event on the next <see cref="Poll"/>.
        /// </summary>
        [ThreadAffinity(ThreadKind.Main)]
        public IConnection Connect(Endpoint endpoint)
        {
            lock (_network.Lock)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(LoopbackTransport));
                LoopbackTransport target = FindTarget(endpoint);
                int refusal = 0;
                if (target == null || target._disposed || target._listen == null || target == this) refusal = TransportCloseReason.NoListener;
                else if (target._connections.Count >= target._listen.MaxConnections) refusal = TransportCloseReason.ListenerFull;

                if (refusal != 0)
                {
                    PeerIdentity who = target != null ? target._identity : new PeerIdentity(endpoint != null ? endpoint.SteamId : 0, endpoint?.Host, null);
                    var failed = new LoopbackConnection(this, _network.NextConnectionId(), who) { RemotelyClosed = true };
                    _connections.Add(failed);
                    _events.Enqueue(new LoopbackEvent
                    {
                        Kind = EventKind.Disconnected,
                        Connection = failed,
                        Reason = refusal,
                        Text = refusal == TransportCloseReason.NoListener ? "no listener at " + (endpoint != null ? endpoint.ToString() : "the default target") : "the listener is full"
                    });
                    return failed;
                }

                var local = new LoopbackConnection(this, _network.NextConnectionId(), target._identity);
                var remote = new LoopbackConnection(target, _network.NextConnectionId(), _identity);
                local.Remote = remote;
                remote.Remote = local;
                _connections.Add(local);
                target._connections.Add(remote);
                _events.Enqueue(new LoopbackEvent { Kind = EventKind.Connected, Connection = local });
                target._events.Enqueue(new LoopbackEvent { Kind = EventKind.Connected, Connection = remote });
                return local;
            }
        }

        /// <summary>
        /// Delivers the events that were pending when the call started, in order (events caused by the callbacks, such
        /// as replies, wait for the next poll). A message buffer is valid only during its callback. An exception from
        /// the sink propagates; the remaining events stay queued.
        /// </summary>
        [ThreadAffinity(ThreadKind.Network)]
        public void Poll(ITransportEventSink sink)
        {
            if (sink == null) throw new ArgumentNullException(nameof(sink));
            int budget;
            lock (_network.Lock)
            {
                if (_disposed) return;
                budget = _events.Count;
            }
            while (budget-- > 0)
            {
                LoopbackEvent e;
                lock (_network.Lock)
                {
                    if (_disposed || _events.Count == 0) return;
                    e = _events.Dequeue();
                    if (!Admit(ref e)) continue;
                }
                try
                {
                    switch (e.Kind)
                    {
                        case EventKind.Connected:
                            sink.OnConnected(e.Connection);
                            break;
                        case EventKind.Disconnected:
                            sink.OnDisconnected(e.Connection, e.Reason, e.Text);
                            break;
                        default:
                            sink.OnMessage(e.Connection, e.Lane, e.Buffer, 0, e.Count);
                            break;
                    }
                }
                finally
                {
                    if (e.Buffer != null) BufferPool.Return(e.Buffer);
                }
            }
        }

        /// <summary>Closes every connection (the other ends see <see cref="TransportCloseReason.TransportDisposed"/>) and stops listening.</summary>
        public void Dispose()
        {
            lock (_network.Lock)
            {
                if (_disposed) return;
                _disposed = true;
                _listen = null;
                LoopbackConnection[] open = _connections.ToArray();
                foreach (LoopbackConnection connection in open)
                {
                    CloseLocked(connection, TransportCloseReason.TransportDisposed, "the transport was disposed", false);
                }
                _connections.Clear();
                while (_events.Count > 0)
                {
                    LoopbackEvent e = _events.Dequeue();
                    if (e.Kind != EventKind.Message) continue;
                    Unpend(ref e);
                    Release(ref e);
                }
                _network.Transports.Remove(this);
            }
        }

        // ---- called by LoopbackConnection ----

        internal SendResult SendFrom(LoopbackConnection from, Lane lane, Delivery delivery, byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count), "the range lies outside the buffer");
            if ((byte)lane > (byte)Lane.Bulk || (byte)delivery > (byte)Delivery.Unreliable) return SendResult.Failed;
            if (count > Limits.MaxMessageBytes) return SendResult.TooLarge;
            lock (_network.Lock)
            {
                LoopbackConnection to = from.Remote;
                if (from.LocallyClosed || from.RemotelyClosed || to == null || to.LocallyClosed) return SendResult.Closed;
                long pending = from.PendingReliable + from.PendingUnreliable;
                if (_sendBufferBytes > 0 && pending > 0 && pending + count > _sendBufferBytes) return SendResult.Busy;
                byte[] copy = BufferPool.Rent(count);
                System.Buffer.BlockCopy(buffer, offset, copy, 0, count);
                to.Owner._events.Enqueue(new LoopbackEvent
                {
                    Kind = EventKind.Message,
                    Connection = to,
                    Lane = lane,
                    Delivery = delivery,
                    Buffer = copy,
                    Count = count
                });
                if (delivery == Delivery.ReliableOrdered) from.PendingReliable += count;
                else from.PendingUnreliable += count;
                from.BytesSent += count;
                from.MessagesSent++;
                return SendResult.Ok;
            }
        }

        internal ConnectionStats StatsOf(LoopbackConnection connection)
        {
            lock (_network.Lock)
            {
                bool open = !connection.LocallyClosed && !connection.RemotelyClosed && connection.Remote != null;
                return new ConnectionStats
                {
                    PingMs = 0,
                    Quality = open ? 1f : 0f,
                    SendRateBytesPerSecond = _reportedSendRate,
                    PendingReliableBytes = Clamp(connection.PendingReliable),
                    PendingUnreliableBytes = Clamp(connection.PendingUnreliable),
                    QueueTimeMicroseconds = 0,
                    OutBytesPerSecond = 0f,
                    InBytesPerSecond = 0f
                };
            }
        }

        /// <summary>
        /// Test hook: the link of <paramref name="connection"/> (one of this transport's) fails as on a network
        /// timeout. Messages still in flight in either direction are lost. Each end that is notified gets a
        /// <see cref="TransportCloseReason.Timeout"/> disconnect at its next poll; an end that is not notified keeps
        /// "sending" into the void (its messages are dropped) until a later call notifies it, the way one side of a real
        /// link often times out long after the other.
        /// </summary>
        public void Sever(IConnection connection, bool notifyThisSide = true, bool notifyOtherSide = true, string text = "timed out")
        {
            if (!(connection is LoopbackConnection local) || local.Owner != this) throw new ArgumentException("not a connection of this transport", nameof(connection));
            lock (_network.Lock)
            {
                SeverEnd(local, notifyThisSide, text);
                if (local.Remote != null) SeverEnd(local.Remote, notifyOtherSide, text);
            }
        }

        private static void SeverEnd(LoopbackConnection end, bool notify, string text)
        {
            end.DiscardUndelivered = true;
            if (!notify || end.LocallyClosed || end.RemotelyClosed) return;
            end.RemotelyClosed = true;
            end.Owner._events.Enqueue(new LoopbackEvent { Kind = EventKind.Disconnected, Connection = end, Reason = TransportCloseReason.Timeout, Text = text });
        }

        internal void CloseConnection(LoopbackConnection connection, int reason, string text, bool linger)
        {
            lock (_network.Lock)
            {
                CloseLocked(connection, reason, text, linger);
            }
        }

        // ---- internals (under the network lock) ----

        private void CloseLocked(LoopbackConnection connection, int reason, string text, bool linger)
        {
            if (connection.LocallyClosed) return;
            connection.LocallyClosed = true;
            if (!linger) connection.DiscardUndelivered = true;
            connection.Owner._connections.Remove(connection);
            LoopbackConnection remote = connection.Remote;
            if (remote != null && !remote.LocallyClosed && !remote.RemotelyClosed)
            {
                remote.RemotelyClosed = true;
                remote.Owner._events.Enqueue(new LoopbackEvent
                {
                    Kind = EventKind.Disconnected,
                    Connection = remote,
                    Reason = reason,
                    Text = text
                });
            }
        }

        /// <summary>Applies a dequeued event to the connection state; false when it must not be delivered.</summary>
        private bool Admit(ref LoopbackEvent e)
        {
            LoopbackConnection receiver = e.Connection;
            switch (e.Kind)
            {
                case EventKind.Message:
                {
                    LoopbackConnection sender = receiver.Remote;
                    Unpend(ref e);
                    if (receiver.LocallyClosed || (sender != null && sender.DiscardUndelivered))
                    {
                        Release(ref e);
                        return false;
                    }
                    receiver.BytesReceived += e.Count;
                    receiver.MessagesReceived++;
                    return true;
                }
                case EventKind.Connected:
                    // closed before the event was polled: the application never sees the connection
                    return !receiver.LocallyClosed;
                default:
                    _connections.Remove(receiver);
                    // closed locally first: the application already dropped it (Steam reports nothing either)
                    return !receiver.LocallyClosed;
            }
        }

        /// <summary>The message left the sender's "send buffer" (delivered or dropped).</summary>
        private static void Unpend(ref LoopbackEvent e)
        {
            LoopbackConnection sender = e.Connection.Remote;
            if (sender == null) return;
            if (e.Delivery == Delivery.ReliableOrdered) sender.PendingReliable -= e.Count;
            else sender.PendingUnreliable -= e.Count;
        }

        private static void Release(ref LoopbackEvent e)
        {
            if (e.Buffer != null)
            {
                BufferPool.Return(e.Buffer);
                e.Buffer = null;
            }
        }

        private LoopbackTransport FindTarget(Endpoint endpoint)
        {
            if (endpoint != null && endpoint.SteamId != 0)
            {
                foreach (LoopbackTransport t in _network.Transports)
                {
                    if (t._identity.SteamId == endpoint.SteamId && t._listen != null) return t;
                }
                return null;
            }
            if (endpoint != null && !string.IsNullOrEmpty(endpoint.Host))
            {
                foreach (LoopbackTransport t in _network.Transports)
                {
                    if (string.Equals(t._identity.Address, endpoint.Host, StringComparison.OrdinalIgnoreCase) && t._listen != null) return t;
                }
                return null;
            }
            return _defaultTarget;
        }

        private static bool IsDefault(PeerIdentity identity) => identity.SteamId == 0 && identity.Address == null && identity.DisplayName == null;

        private static int Clamp(long value) => value <= 0 ? 0 : value >= int.MaxValue ? int.MaxValue : (int)value;
    }
}
