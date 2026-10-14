using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SkylineTogether.Core.Testing;

namespace SkylineTogether.Core.Transport
{
    /// <summary>
    /// Network simulator (ARCHITECTURE.md#transport; tests and tools only): wraps another transport and impairs everything this side
    /// sends according to <see cref="Options"/> (see <see cref="NetSimOptions"/> for the model; wrap both ends of a
    /// <see cref="LoopbackTransport"/> pair to impair both directions). Received traffic passes through unchanged.
    /// <para>
    /// Time comes from the given <see cref="IClock"/> (a <see cref="ManualClock"/> makes tests deterministic together
    /// with <see cref="NetSimOptions.Seed"/>). Delayed messages are handed to the inner transport when they are due, at
    /// the next <see cref="Poll"/>, send or statistics call of this side, so both ends must be polled. A queued message
    /// counts as pending in <see cref="IConnection.GetStats"/>; a full simulated send buffer and
    /// <see cref="BusyRate"/> make sends answer <see cref="SendResult.Busy"/> like Steam's
    /// <c>k_EResultLimitExceeded</c>, which is what the outbox tests need.
    /// </para>
    /// <para>
    /// Closing a connection with linger hands its queued messages over first; without linger, and when the other end
    /// disconnects, they are lost, as on a real link. Disposing the simulator disposes the inner transport.
    /// Thread-safe; one thread at a time should poll.
    /// </para>
    /// </summary>
    public sealed class NetSimTransport : ITransport
    {
        private readonly ITransport _inner;
        private readonly IClock _clock;
        private readonly Dictionary<IConnection, NetSimConnection> _wrappers = new Dictionary<IConnection, NetSimConnection>(ReferenceComparer.Instance);
        private readonly List<NetSimConnection> _connections = new List<NetSimConnection>();
        private readonly Adapter _adapter;
        private NetSimOptions _options;
        private DeterministicRandom _random;
        private int _seed;
        private double _busyRate;
        private NetSimCounters _counters;
        private bool _polling;
        private bool _disposed;

        /// <summary>Guards the simulator and all of its connections.</summary>
        internal readonly object SyncRoot = new object();

        public NetSimTransport(ITransport inner, NetSimOptions options, IClock clock)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _seed = options.Seed;
            _random = new DeterministicRandom(SeedOf(options.Seed));
            _adapter = new Adapter(this);
        }

        public TransportKind Kind => TransportKind.NetSim;

        /// <summary>The wrapped transport.</summary>
        public ITransport Inner => _inner;

        /// <summary>
        /// The impairments in effect. The instance is read at every operation, so changing its fields (or assigning a
        /// new instance) affects messages sent from then on; a new <see cref="NetSimOptions.Seed"/> restarts the
        /// random sequence.
        /// </summary>
        public NetSimOptions Options
        {
            get { lock (SyncRoot) return _options; }
            set
            {
                if (value == null) throw new ArgumentNullException(nameof(value));
                lock (SyncRoot)
                {
                    _options = value;
                    if (value.Seed != _seed)
                    {
                        _seed = value.Seed;
                        _random = new DeterministicRandom(SeedOf(value.Seed));
                    }
                }
            }
        }

        /// <summary>
        /// Probability that a send answers <see cref="SendResult.Busy"/> without taking the message, independent of the
        /// simulated send buffer (simulates Steam's <c>k_EResultLimitExceeded</c> for the outbox tests). 0..1.
        /// </summary>
        public double BusyRate
        {
            get { lock (SyncRoot) return _busyRate; }
            set
            {
                if (double.IsNaN(value) || value < 0 || value > 1) throw new ArgumentOutOfRangeException(nameof(value), value, "expected 0..1");
                lock (SyncRoot) _busyRate = value;
            }
        }

        /// <summary>A snapshot of what the simulator did to the traffic so far.</summary>
        public NetSimCounters Counters
        {
            get { lock (SyncRoot) return _counters; }
        }

        [ThreadAffinity(ThreadKind.Main)]
        public void Listen(ListenOptions options)
        {
            ThrowIfDisposed();
            _inner.Listen(options);
        }

        [ThreadAffinity(ThreadKind.Main)]
        public IConnection Connect(Endpoint endpoint)
        {
            ThrowIfDisposed();
            IConnection inner = _inner.Connect(endpoint);
            lock (SyncRoot)
            {
                return WrapLocked(inner);
            }
        }

        /// <summary>Hands due messages of every connection to the inner transport, then polls it and forwards its events.</summary>
        [ThreadAffinity(ThreadKind.Network)]
        public void Poll(ITransportEventSink sink)
        {
            if (sink == null) throw new ArgumentNullException(nameof(sink));
            lock (SyncRoot)
            {
                if (_disposed) return;
                if (_polling) throw new InvalidOperationException("NetSimTransport.Poll is not reentrant");
                _polling = true;
                long now = NowTicks();
                for (int i = 0; i < _connections.Count; i++) _connections[i].PumpLocked(now);
            }
            try
            {
                _adapter.Target = sink;
                _inner.Poll(_adapter);
            }
            finally
            {
                _adapter.Target = null;
                lock (SyncRoot) _polling = false;
            }
        }

        /// <summary>Drops every queued message, forgets the connections and disposes the inner transport.</summary>
        public void Dispose()
        {
            lock (SyncRoot)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (NetSimConnection connection in _connections)
                {
                    connection.Closed = true;
                    connection.DropAllLocked();
                }
                _connections.Clear();
                _wrappers.Clear();
            }
            _inner.Dispose();
        }

        // ---- shared state for the connections (under SyncRoot) ----

        internal long NowTicks() => _clock.Ticks;

        /// <summary><see cref="Options"/> without taking the lock (callers hold <see cref="SyncRoot"/>).</summary>
        internal NetSimOptions CurrentOptions => _options;

        internal ref NetSimCounters CountersRef => ref _counters;

        internal double NextDouble() => _random.NextDouble();

        internal bool RefuseByBusyRate() => _busyRate > 0 && _random.NextDouble() < _busyRate;

        internal void ForgetLocked(NetSimConnection connection)
        {
            _connections.Remove(connection);
            _wrappers.Remove(connection.Inner);
        }

        private NetSimConnection WrapLocked(IConnection inner)
        {
            if (_wrappers.TryGetValue(inner, out NetSimConnection existing)) return existing;
            var wrapper = new NetSimConnection(this, inner, NowTicks());
            _wrappers.Add(inner, wrapper);
            _connections.Add(wrapper);
            if (_disposed) wrapper.Closed = true;
            return wrapper;
        }

        private void ThrowIfDisposed()
        {
            lock (SyncRoot)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(NetSimTransport));
            }
        }

        private static ulong SeedOf(int seed) => unchecked((ulong)(uint)seed * 0x9E3779B97F4A7C15UL + 0x632BE59BD9B4E019UL);

        /// <summary>Maps the inner transport's events to the simulator's connections.</summary>
        private sealed class Adapter : ITransportEventSink
        {
            private readonly NetSimTransport _owner;
            public ITransportEventSink Target;

            public Adapter(NetSimTransport owner)
            {
                _owner = owner;
            }

            public void OnConnected(IConnection connection)
            {
                NetSimConnection wrapper;
                lock (_owner.SyncRoot) wrapper = _owner.WrapLocked(connection);
                Target.OnConnected(wrapper);
            }

            public void OnDisconnected(IConnection connection, int reason, string text)
            {
                NetSimConnection wrapper;
                lock (_owner.SyncRoot)
                {
                    wrapper = _owner.WrapLocked(connection);
                    wrapper.OnRemoteClosedLocked();
                    _owner.ForgetLocked(wrapper);
                }
                Target.OnDisconnected(wrapper, reason, text);
            }

            public void OnMessage(IConnection connection, Lane lane, byte[] buffer, int offset, int count)
            {
                NetSimConnection wrapper;
                lock (_owner.SyncRoot)
                {
                    wrapper = _owner.WrapLocked(connection);
                    wrapper.CountIncomingLocked(_owner.NowTicks(), count);
                }
                Target.OnMessage(wrapper, lane, buffer, offset, count);
            }
        }

        /// <summary>Reference identity for connection keys (netstandard2.0 has no ReferenceEqualityComparer).</summary>
        private sealed class ReferenceComparer : IEqualityComparer<IConnection>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();

            public bool Equals(IConnection x, IConnection y) => ReferenceEquals(x, y);

            public int GetHashCode(IConnection obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
