using System;
using System.Globalization;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;

namespace SkylineTogether.Core.Transport
{
    /// <summary>
    /// One connection of <see cref="NetSimTransport"/>: a simulated link in front of the inner connection.
    /// <para>
    /// Model (lazily advanced to the clock at every send, poll and statistics call): messages wait in a send queue
    /// (two with lane priorities: R and U before B) and leave it at the link bandwidth; a message has departed when its
    /// last byte is on the wire. Its arrival is departure + latency + jitter (+ retransmission rounds for reliable
    /// messages, + a hold-back for reordered unreliable ones); reliable messages never arrive before an earlier one of
    /// their lane. Due messages are handed to the inner connection in arrival order. The bytes still in the send queue
    /// are the connection's pending bytes and fill the simulated send buffer.
    /// </para>
    /// <para>All state is guarded by the owner's <see cref="NetSimTransport.SyncRoot"/>.</para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    internal sealed class NetSimConnection : IConnection
    {
        /// <summary>Bytes per simulated packet (the MTU-sized datagrams of Steam and of the UDP transport).</summary>
        private const int PacketBytes = Limits.MaxDatagramBytes;

        /// <summary>Retransmission rounds per packet at most (bounds the delay at extreme loss rates).</summary>
        private const int MaxRetransmissions = 20;

        /// <summary>An unreliable "no delay" message is dropped when the priority queue holds more than this much sending time.</summary>
        private const double NoDelayWindowSeconds = 0.010;

        private const double MinRetransmitMs = 25;

        private struct LinkItem
        {
            public byte[] Buffer;
            public int Count;
            public Lane Lane;
            public Delivery Delivery;
            public double Remaining;          // bytes still to transmit, retransmissions included
            public long ExtraDelayTicks;      // retransmission rounds of reliable messages
        }

        private struct InFlight
        {
            public long Arrival;
            public long Sequence;
            public byte[] Buffer;
            public int Count;
            public Lane Lane;
            public Delivery Delivery;
        }

        internal readonly NetSimTransport Owner;
        internal readonly IConnection Inner;

        private readonly RingQueue<LinkItem> _high = new RingQueue<LinkItem>(64);
        private readonly RingQueue<LinkItem> _low = new RingQueue<LinkItem>(64);
        private readonly long[] _lastReliableArrival = new long[3];
        private readonly RateMeter _out = new RateMeter();
        private readonly RateMeter _in = new RateMeter();
        private InFlight[] _heap = new InFlight[64];
        private int _heapCount;
        private long _nextSequence;
        private long _queuedReliableBytes;
        private long _queuedUnreliableBytes;
        private long _highQueuedBytes;
        private long _lastAdvanceTicks;

        /// <summary>Closed on this side.</summary>
        internal bool Closed;

        /// <summary>The inner connection reported a disconnect or refused as closed.</summary>
        internal bool RemoteClosed;

        internal NetSimConnection(NetSimTransport owner, IConnection inner, long nowTicks)
        {
            Owner = owner;
            Inner = inner;
            _lastAdvanceTicks = nowTicks;
        }

        public int Id => Inner.Id;

        public PeerIdentity Peer => Inner.Peer;

        public SendResult Send(Lane lane, Delivery delivery, byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count), "the range lies outside the buffer");
            if ((byte)lane > (byte)Lane.Bulk || (byte)delivery > (byte)Delivery.Unreliable) return SendResult.Failed;
            if (count > Limits.MaxMessageBytes) return SendResult.TooLarge;
            lock (Owner.SyncRoot)
            {
                if (Closed || RemoteClosed) return SendResult.Closed;
                long now = Owner.NowTicks();
                NetSimOptions options = Owner.CurrentOptions;
                AdvanceLocked(now, options);

                if (Owner.RefuseByBusyRate())
                {
                    Owner.CountersRef.BusyRateRefusals++;
                    return SendResult.Busy;
                }
                long queued = _queuedReliableBytes + _queuedUnreliableBytes;
                int sendBuffer = Math.Max(0, options.SendBufferBytes);
                if (sendBuffer > 0 && queued > 0 && queued + count > sendBuffer)
                {
                    Owner.CountersRef.SendBufferRefusals++;
                    return SendResult.Busy;
                }

                double bandwidth = Bandwidth(options);
                int packets = Math.Max(1, (count + PacketBytes - 1) / PacketBytes);
                double loss = Rate(options.LossRate);
                var item = new LinkItem { Count = count, Lane = lane, Delivery = delivery, Remaining = count };
                if (delivery == Delivery.Unreliable)
                {
                    Owner.CountersRef.UnreliableMessages++;
                    if (bandwidth > 0 && _highQueuedBytes / bandwidth > NoDelayWindowSeconds)
                    {
                        Owner.CountersRef.UnreliableSaturationDrops++;
                        return SendResult.Ok;
                    }
                    if (loss > 0 && Owner.NextDouble() < 1 - Math.Pow(1 - loss, packets))
                    {
                        Owner.CountersRef.UnreliableLost++;
                        return SendResult.Ok;
                    }
                }
                else
                {
                    Owner.CountersRef.ReliableMessages++;
                    if (loss > 0)
                    {
                        int total = 0, worst = 0;
                        for (int p = 0; p < packets; p++)
                        {
                            int rounds = 0;
                            while (rounds < MaxRetransmissions && Owner.NextDouble() < loss) rounds++;
                            total += rounds;
                            if (rounds > worst) worst = rounds;
                        }
                        Owner.CountersRef.RetransmittedPackets += total;
                        item.Remaining = count + total * ((double)count / packets);
                        item.ExtraDelayTicks = worst * RetransmitTicks(options);
                    }
                }

                item.Buffer = BufferPool.Rent(count);
                System.Buffer.BlockCopy(buffer, offset, item.Buffer, 0, count);
                if (options.PrioritizeLanes && lane == Lane.Bulk)
                {
                    _low.Enqueue(item);
                }
                else
                {
                    _high.Enqueue(item);
                    _highQueuedBytes += count;
                }
                if (delivery == Delivery.ReliableOrdered) _queuedReliableBytes += count;
                else _queuedUnreliableBytes += count;

                AdvanceLocked(now, options);   // an unlimited link sends at once
                ReleaseLocked(now);
                return SendResult.Ok;
            }
        }

        public ConnectionStats GetStats()
        {
            lock (Owner.SyncRoot)
            {
                long now = Owner.NowTicks();
                NetSimOptions options = Owner.CurrentOptions;
                if (!Closed && !RemoteClosed)
                {
                    AdvanceLocked(now, options);
                    ReleaseLocked(now);
                }
                double bandwidth = Bandwidth(options);
                long queued = _queuedReliableBytes + _queuedUnreliableBytes;
                return new ConnectionStats
                {
                    PingMs = (int)Math.Round(2 * Positive(options.LatencyMs) + Positive(options.JitterMs)),
                    Quality = Closed || RemoteClosed ? 0f : (float)(1 - Rate(options.LossRate)),
                    SendRateBytesPerSecond = bandwidth > 0 ? (int)bandwidth : 0,
                    PendingReliableBytes = Clamp(_queuedReliableBytes),
                    PendingUnreliableBytes = Clamp(_queuedUnreliableBytes),
                    QueueTimeMicroseconds = bandwidth > 0 ? (long)(queued / bandwidth * 1e6) : 0,
                    OutBytesPerSecond = _out.Rate(now),
                    InBytesPerSecond = _in.Rate(now)
                };
            }
        }

        /// <summary>
        /// Closes the simulated link and the inner connection. With <paramref name="linger"/> every queued and in-flight
        /// message is handed to the inner connection first (in order, without further delay); without it they are lost.
        /// </summary>
        public void Close(int reason, string text, bool linger)
        {
            lock (Owner.SyncRoot)
            {
                if (Closed) return;
                Closed = true;
                if (linger && !RemoteClosed) FlushLocked();
                DropAllLocked();
                Owner.ForgetLocked(this);
            }
            Inner.Close(reason, text, linger);
        }

        public override string ToString()
        {
            return "netsim#" + Id.ToString(CultureInfo.InvariantCulture) + " to " + Peer;
        }

        // ---- called by the owner (under SyncRoot) ----

        internal void PumpLocked(long now)
        {
            if (Closed || RemoteClosed) return;
            AdvanceLocked(now, Owner.CurrentOptions);
            ReleaseLocked(now);
        }

        internal void CountIncomingLocked(long now, int bytes)
        {
            _in.Add(now, bytes);
        }

        internal void OnRemoteClosedLocked()
        {
            RemoteClosed = true;
            DropAllLocked();
        }

        internal void DropAllLocked()
        {
            while (_high.Count > 0) BufferPool.Return(_high.Dequeue().Buffer);
            while (_low.Count > 0) BufferPool.Return(_low.Dequeue().Buffer);
            for (int i = 0; i < _heapCount; i++)
            {
                BufferPool.Return(_heap[i].Buffer);
                _heap[i] = default;
            }
            _heapCount = 0;
            _queuedReliableBytes = 0;
            _queuedUnreliableBytes = 0;
            _highQueuedBytes = 0;
        }

        // ---- the link model ----

        private void AdvanceLocked(long now, NetSimOptions options)
        {
            if (now < _lastAdvanceTicks) now = _lastAdvanceTicks;
            double bandwidth = Bandwidth(options);
            if (bandwidth <= 0)
            {
                while (_high.Count > 0) Depart(_high.Dequeue(), now, options, true);
                while (_low.Count > 0) Depart(_low.Dequeue(), now, options, false);
                _lastAdvanceTicks = now;
                return;
            }
            // bytes the link could carry since the last advance; an idle link accumulates no credit
            double budget = (now - _lastAdvanceTicks) / (double)TimeSpan.TicksPerSecond * bandwidth;
            double t = _lastAdvanceTicks;
            while (true)
            {
                bool high = _high.Count > 0;
                RingQueue<LinkItem> queue = high ? _high : _low.Count > 0 ? _low : null;
                if (queue == null) break;
                ref LinkItem head = ref queue.PeekRef();
                if (head.Remaining > budget)
                {
                    head.Remaining -= budget;
                    break;
                }
                budget -= head.Remaining;
                t += head.Remaining / bandwidth * TimeSpan.TicksPerSecond;
                head.Remaining = 0;
                Depart(queue.Dequeue(), (long)Math.Ceiling(t), options, high);
            }
            _lastAdvanceTicks = now;
        }

        private void Depart(LinkItem item, long departure, NetSimOptions options, bool fromHighQueue)
        {
            if (fromHighQueue) _highQueuedBytes -= item.Count;
            if (item.Delivery == Delivery.ReliableOrdered) _queuedReliableBytes -= item.Count;
            else _queuedUnreliableBytes -= item.Count;
            _out.Add(departure, item.Count);

            long arrival = departure + MillisecondsToTicks(options.LatencyMs) + Jitter(options);
            if (item.Delivery == Delivery.ReliableOrdered)
            {
                arrival += item.ExtraDelayTicks;
                int lane = (int)item.Lane;
                if (arrival < _lastReliableArrival[lane]) arrival = _lastReliableArrival[lane];
                _lastReliableArrival[lane] = arrival;
                Push(arrival, item.Buffer, item.Count, item.Lane, item.Delivery);
                return;
            }
            double reorder = Rate(options.ReorderRate);
            if (reorder > 0 && Owner.NextDouble() < reorder)
            {
                // held back long enough for the next messages of a steady stream to overtake it
                arrival += (long)((1 + Owner.NextDouble()) * Math.Max(Positive(options.LatencyMs), 10) * TimeSpan.TicksPerMillisecond);
                Owner.CountersRef.UnreliableReordered++;
            }
            Push(arrival, item.Buffer, item.Count, item.Lane, item.Delivery);
            double duplicate = Rate(options.DuplicateRate);
            if (duplicate > 0 && Owner.NextDouble() < duplicate)
            {
                byte[] copy = BufferPool.Rent(item.Count);
                System.Buffer.BlockCopy(item.Buffer, 0, copy, 0, item.Count);
                long extra = (long)(Owner.NextDouble() * Math.Max(Positive(options.JitterMs), 1) * TimeSpan.TicksPerMillisecond);
                Push(arrival + extra, copy, item.Count, item.Lane, item.Delivery);
                Owner.CountersRef.UnreliableDuplicated++;
            }
        }

        /// <summary>Hands every due message to the inner connection, in arrival order.</summary>
        private void ReleaseLocked(long now)
        {
            while (_heapCount > 0 && _heap[0].Arrival <= now)
            {
                ref InFlight next = ref _heap[0];
                SendResult result = Inner.Send(next.Lane, next.Delivery, next.Buffer, 0, next.Count);
                if (result == SendResult.Busy && next.Delivery == Delivery.ReliableOrdered)
                {
                    // the inner transport is full (its receiver does not poll): keep the order and retry later
                    Owner.CountersRef.InnerBusyRetries++;
                    return;
                }
                InFlight done = Pop();
                BufferPool.Return(done.Buffer);
                if (result == SendResult.Ok)
                {
                    Owner.CountersRef.DeliveredMessages++;
                    Owner.CountersRef.DeliveredBytes += done.Count;
                }
                else if (result == SendResult.Closed)
                {
                    OnRemoteClosedLocked();
                    return;
                }
            }
        }

        /// <summary>Linger: everything queued departs now and every in-flight message is handed over in order.</summary>
        private void FlushLocked()
        {
            long now = _lastAdvanceTicks;
            NetSimOptions options = Owner.CurrentOptions;
            while (_high.Count > 0) Depart(_high.Dequeue(), now, options, true);
            while (_low.Count > 0) Depart(_low.Dequeue(), now, options, false);
            while (_heapCount > 0)
            {
                InFlight next = Pop();
                SendResult result = Inner.Send(next.Lane, next.Delivery, next.Buffer, 0, next.Count);
                BufferPool.Return(next.Buffer);
                if (result == SendResult.Ok)
                {
                    Owner.CountersRef.DeliveredMessages++;
                    Owner.CountersRef.DeliveredBytes += next.Count;
                }
                else if (result == SendResult.Closed)
                {
                    break;
                }
            }
        }

        // ---- in-flight min-heap ordered by (arrival, sequence) ----

        private void Push(long arrival, byte[] buffer, int count, Lane lane, Delivery delivery)
        {
            if (_heapCount == _heap.Length) Array.Resize(ref _heap, _heap.Length * 2);
            var item = new InFlight { Arrival = arrival, Sequence = _nextSequence++, Buffer = buffer, Count = count, Lane = lane, Delivery = delivery };
            int i = _heapCount++;
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (!Before(in item, in _heap[parent])) break;
                _heap[i] = _heap[parent];
                i = parent;
            }
            _heap[i] = item;
        }

        private InFlight Pop()
        {
            InFlight top = _heap[0];
            InFlight last = _heap[--_heapCount];
            _heap[_heapCount] = default;
            if (_heapCount > 0)
            {
                int i = 0;
                while (true)
                {
                    int child = 2 * i + 1;
                    if (child >= _heapCount) break;
                    if (child + 1 < _heapCount && Before(in _heap[child + 1], in _heap[child])) child++;
                    if (!Before(in _heap[child], in last)) break;
                    _heap[i] = _heap[child];
                    i = child;
                }
                _heap[i] = last;
            }
            return top;
        }

        private static bool Before(in InFlight a, in InFlight b)
        {
            return a.Arrival < b.Arrival || (a.Arrival == b.Arrival && a.Sequence < b.Sequence);
        }

        // ---- option helpers ----

        private long Jitter(NetSimOptions options)
        {
            double jitter = Positive(options.JitterMs);
            return jitter > 0 ? (long)(Owner.NextDouble() * jitter * TimeSpan.TicksPerMillisecond) : 0;
        }

        private static long RetransmitTicks(NetSimOptions options)
        {
            double rto = Math.Max(MinRetransmitMs, 2 * Positive(options.LatencyMs) + 2 * Positive(options.JitterMs));
            return MillisecondsToTicks(rto);
        }

        private static double Bandwidth(NetSimOptions options) => options.BandwidthBytesPerSecond > 0 ? options.BandwidthBytesPerSecond : 0;

        private static double Rate(double value) => double.IsNaN(value) || value <= 0 ? 0 : value >= 1 ? 1 : value;

        private static double Positive(double value) => double.IsNaN(value) || value <= 0 ? 0 : Math.Min(value, 600_000);

        private static long MillisecondsToTicks(double ms) => (long)(Positive(ms) * TimeSpan.TicksPerMillisecond);

        private static int Clamp(long value) => value <= 0 ? 0 : value >= int.MaxValue ? int.MaxValue : (int)value;
    }
}
