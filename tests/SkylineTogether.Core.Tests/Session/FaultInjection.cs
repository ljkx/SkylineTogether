using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>What a <see cref="FaultInjectingTransport"/> does to the messages it carries (w8/robust).</summary>
    internal enum Fault
    {
        None,
        /// <summary>The message is lost (a reliable lane never does that: the receiver must cope with the gap).</summary>
        Drop,
        /// <summary>The message is delivered twice.</summary>
        Duplicate,
        /// <summary>The message is held back and delivered after the next one.</summary>
        Reorder,
        /// <summary>The message is cut short at a random length.</summary>
        Truncate,
        /// <summary>One random bit of the message is flipped.</summary>
        BitFlip,
        /// <summary>The message becomes the first fragment of a message that declares the kind's largest size.</summary>
        HugeLength,
        /// <summary>The message is replaced by random bytes.</summary>
        Garbage,
    }

    /// <summary>
    /// w8/robust: a transport wrapper for fault-injection tests. Messages sent on the wrapped connections (outbound) and
    /// messages delivered to the wrapped sink (inbound) are subjected to <see cref="Fault"/>s with the configured
    /// probabilities, once <see cref="Armed"/> is set. Deterministic for a given seed.
    /// </summary>
    internal sealed class FaultInjectingTransport : ITransport
    {
        private readonly ITransport _inner;
        private readonly Random _random;
        private readonly Dictionary<IConnection, FaultConnection> _wrapped = new Dictionary<IConnection, FaultConnection>();
        private readonly Dictionary<Fault, double> _outbound = new Dictionary<Fault, double>();
        private readonly Dictionary<Fault, double> _inbound = new Dictionary<Fault, double>();
        private byte[] _heldInbound;
        private IConnection _heldInboundConnection;

        public FaultInjectingTransport(ITransport inner, int seed)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _random = new Random(seed);
        }

        /// <summary>Faults apply only while armed (so the handshake and the join can complete first).</summary>
        public bool Armed { get; set; }

        /// <summary>Faults applied so far, per kind.</summary>
        public readonly Dictionary<Fault, int> Applied = new Dictionary<Fault, int>();

        public FaultInjectingTransport Outbound(Fault fault, double probability)
        {
            _outbound[fault] = probability;
            return this;
        }

        public FaultInjectingTransport Inbound(Fault fault, double probability)
        {
            _inbound[fault] = probability;
            return this;
        }

        public TransportKind Kind => _inner.Kind;

        public void Listen(ListenOptions options) => _inner.Listen(options);

        public IConnection Connect(Endpoint endpoint)
        {
            IConnection c = _inner.Connect(endpoint);
            return c == null ? null : Wrap(c);
        }

        public void Poll(ITransportEventSink sink) => _inner.Poll(new Sink(this, sink));

        public void Dispose() => _inner.Dispose();

        private FaultConnection Wrap(IConnection inner)
        {
            if (!_wrapped.TryGetValue(inner, out FaultConnection w)) _wrapped[inner] = w = new FaultConnection(this, inner);
            return w;
        }

        private Fault Pick(Dictionary<Fault, double> table)
        {
            if (!Armed || table.Count == 0) return Fault.None;
            double roll = _random.NextDouble();
            foreach (KeyValuePair<Fault, double> f in table)
            {
                if (roll < f.Value) return f.Key;
                roll -= f.Value;
            }
            return Fault.None;
        }

        private void Count(Fault f)
        {
            Applied.TryGetValue(f, out int n);
            Applied[f] = n + 1;
        }

        /// <summary>The message after <paramref name="fault"/>; null to drop it.</summary>
        private byte[] Mutate(Fault fault, byte[] data)
        {
            switch (fault)
            {
                case Fault.Truncate:
                    return data.Length <= 1 ? new byte[0] : Slice(data, _random.Next(0, data.Length));
                case Fault.BitFlip:
                {
                    var copy = (byte[])data.Clone();
                    if (copy.Length > 0) copy[_random.Next(copy.Length)] ^= (byte)(1 << _random.Next(8));
                    return copy;
                }
                case Fault.HugeLength:
                {
                    var w = new ByteWriter(64);
                    byte kind = data.Length > 0 && data[0] != 0 ? data[0] : (byte)MessageKind.Epoch;
                    w.WriteByte(kind);
                    w.WriteByte((byte)EnvelopeFlags.Fragment);
                    w.WriteVarUInt32((uint)_random.Next(1, 1000)); // message id
                    w.WriteVarUInt32(0); // index
                    w.WriteVarUInt32(2); // count
                    w.WriteVarUInt32((uint)Limits.MaxBodyBytes((MessageKind)kind) + 8); // declared total
                    w.WriteBytes(new byte[16], 0, 16);
                    return w.ToArray();
                }
                case Fault.Garbage:
                {
                    var g = new byte[_random.Next(0, 300)];
                    _random.NextBytes(g);
                    return g;
                }
                default:
                    return data;
            }
        }

        private static byte[] Slice(byte[] data, int length)
        {
            var r = new byte[length];
            Buffer.BlockCopy(data, 0, r, 0, length);
            return r;
        }

        private sealed class FaultConnection : IConnection
        {
            private readonly FaultInjectingTransport _owner;
            private byte[] _held;
            private Lane _heldLane;
            private Delivery _heldDelivery;

            public FaultConnection(FaultInjectingTransport owner, IConnection inner)
            {
                _owner = owner;
                Inner = inner;
            }

            public IConnection Inner { get; }

            public int Id => Inner.Id;

            public PeerIdentity Peer => Inner.Peer;

            public SendResult Send(Lane lane, Delivery delivery, byte[] buffer, int offset, int count)
            {
                var data = new byte[count];
                Buffer.BlockCopy(buffer, offset, data, 0, count);
                Fault f = _owner.Pick(_owner._outbound);
                if (f != Fault.None) _owner.Count(f);
                SendResult result;
                switch (f)
                {
                    case Fault.Drop:
                        return SendResult.Ok;
                    case Fault.Duplicate:
                        Inner.Send(lane, delivery, data, 0, data.Length);
                        result = Inner.Send(lane, delivery, data, 0, data.Length);
                        break;
                    case Fault.Reorder:
                        if (_held == null)
                        {
                            _held = data;
                            _heldLane = lane;
                            _heldDelivery = delivery;
                            return SendResult.Ok;
                        }
                        result = Inner.Send(lane, delivery, data, 0, data.Length);
                        break;
                    default:
                        byte[] m = _owner.Mutate(f, data);
                        result = Inner.Send(lane, delivery, m, 0, m.Length);
                        break;
                }
                if (_held != null && f != Fault.Reorder)
                {
                    byte[] h = _held;
                    _held = null;
                    Inner.Send(_heldLane, _heldDelivery, h, 0, h.Length);
                }
                return result;
            }

            public ConnectionStats GetStats() => Inner.GetStats();

            public void Close(int reason, string text, bool linger) => Inner.Close(reason, text, linger);

            public override string ToString() => "fault(" + Inner + ")";
        }

        private sealed class Sink : ITransportEventSink
        {
            private readonly FaultInjectingTransport _owner;
            private readonly ITransportEventSink _inner;

            public Sink(FaultInjectingTransport owner, ITransportEventSink inner)
            {
                _owner = owner;
                _inner = inner;
            }

            public void OnConnected(IConnection connection) => _inner.OnConnected(_owner.Wrap(connection));

            public void OnDisconnected(IConnection connection, int reason, string text) => _inner.OnDisconnected(_owner.Wrap(connection), reason, text);

            public void OnMessage(IConnection connection, Lane lane, byte[] buffer, int offset, int count)
            {
                IConnection wrapped = _owner.Wrap(connection);
                var data = new byte[count];
                Buffer.BlockCopy(buffer, offset, data, 0, count);
                Fault f = _owner.Pick(_owner._inbound);
                if (f != Fault.None) _owner.Count(f);
                switch (f)
                {
                    case Fault.Drop:
                        return;
                    case Fault.Duplicate:
                        _inner.OnMessage(wrapped, lane, data, 0, data.Length);
                        _inner.OnMessage(wrapped, lane, data, 0, data.Length);
                        break;
                    case Fault.Reorder:
                        if (_owner._heldInbound == null)
                        {
                            _owner._heldInbound = data;
                            _owner._heldInboundConnection = wrapped;
                            return;
                        }
                        _inner.OnMessage(wrapped, lane, data, 0, data.Length);
                        break;
                    default:
                        byte[] m = _owner.Mutate(f, data);
                        _inner.OnMessage(wrapped, lane, m, 0, m.Length);
                        break;
                }
                if (_owner._heldInbound != null && f != Fault.Reorder)
                {
                    byte[] h = _owner._heldInbound;
                    IConnection hc = _owner._heldInboundConnection;
                    _owner._heldInbound = null;
                    _inner.OnMessage(hc, lane, h, 0, h.Length);
                }
            }
        }
    }
}
