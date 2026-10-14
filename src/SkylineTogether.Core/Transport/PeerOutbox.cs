using System;
using System.Globalization;
using System.Threading;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;

namespace SkylineTogether.Core.Transport
{
    /// <summary>
    /// The per-peer reliable outbox (ARCHITECTURE.md#outbox-lanes-and-rate-control): an application-level queue per lane in front of an
    /// <see cref="IConnection"/>, so a reliable message is never lost when the transport's send buffer is full.
    /// <para>
    /// Steam refuses a reliable message with <c>k_EResultLimitExceeded</c> (<see cref="SendResult.Busy"/>) when the
    /// connection's send buffer is full, and with lanes that buffer is shared by R and B (R06 §5.4). The outbox
    /// therefore hands a message over only while the transport's pending bytes plus the message stay inside a window:
    /// lanes R and U (reliable messages) up to <see cref="OutboxConfig.SendBufferBytes"/> −
    /// <see cref="OutboxConfig.ReliableHeadroomBytes"/>; lane B only while no reliable R/U message is queued and the
    /// pending bytes stay below the adaptive bulk window (2-8 MiB, about one second at the reported send rate), which
    /// always leaves headroom for R. A send that still answers <see cref="SendResult.Busy"/> keeps the message at the
    /// head of its queue for the next <see cref="Pump"/> (~1 ms later); nothing is dropped, so no gap can appear, and
    /// every lane keeps the order in which <see cref="Send"/> accepted its messages.
    /// </para>
    /// <para>
    /// Unreliable messages go straight to the connection and are dropped when it refuses them. Reliable messages
    /// above <see cref="Limits.MaxMessageBytes"/> are refused at once with <see cref="SendResult.TooLarge"/> (the caller
    /// fragments first, <see cref="Fragmenter"/>); every other reliable send answers <see cref="SendResult.Ok"/> or,
    /// once the connection is closed, <see cref="SendResult.Closed"/>. The queued bytes count toward the peer's P0
    /// backlog and trip <see cref="OverLimit"/> at the "connection too slow" thresholds (64 MB or 60 s, ARCHITECTURE.md#live-session).
    /// </para>
    /// <para>
    /// Thread-safe: any thread may send and read the counters; <see cref="Pump"/> runs on the network thread (a
    /// concurrent second pump returns at once). The connection is never sent to while the outbox lock is held, so
    /// producers never wait for the transport.
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class PeerOutbox : IPeerOutput, IDisposable
    {
        private struct QueuedMessage
        {
            public byte[] Buffer;
            public int Length;
            public long EnqueuedTicks;
        }

        private enum DrainOutcome : byte { Drained, WindowFull, Busy, Failed, Stopped }

        private const int LaneCount = 3;
        private const long BusyWarnTicks = 5 * TimeSpan.TicksPerSecond;
        private const long FailureLogTicks = 10 * TimeSpan.TicksPerSecond;

        private readonly IConnection _connection;
        private readonly OutboxConfig _config;
        private readonly IClock _clock;
        private readonly ILog _log;
        private readonly string _name;
        private readonly object _queueLock = new object();
        private readonly object _pumpLock = new object();
        private readonly object _statsLock = new object();
        private readonly RingQueue<QueuedMessage>[] _queues;

        // queue totals: written under _queueLock with Interlocked, read lock-free
        private long _queuedBytes;
        private long _queuedHighBytes;
        private long _queuedBulkBytes;
        private int _queuedMessages;

        private volatile bool _closed;
        private volatile bool _faulted;
        private string _faultReason;
        private ConnectionStats _lastStats;
        private long _statsFrame; // the StatsFrame stamp of the last query made for Stats (under _statsLock)
        private long _statsReused; // under _statsLock
        private long _statsQueries;
        private int _bulkWindow;

        // counters (Interlocked)
        private long _sentReliable;
        private long _sentBulk;
        private long _sentBytes;
        private long _busyRetries;
        private long _sentUnreliable;
        private long _droppedUnreliable;
        private long _sendFailures;
        private long _pumps;

        // pump-thread state (under _pumpLock)
        private long _busySinceTicks = -1;
        private long _lastBusyWarnTicks;
        private bool _overLimitReported;
        private long _lastFailureLogTicks = long.MinValue;

        public PeerOutbox(IConnection connection, PeerId peer, OutboxConfig config, IClock clock, ILog log)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _config = (config ?? new OutboxConfig()).Clone();
            _config.Validate();
            _log = log;
            Peer = peer;
            _queues = new[]
            {
                new RingQueue<QueuedMessage>(64),   // Lane.Reliable
                new RingQueue<QueuedMessage>(4),    // Lane.Unreliable (reliable messages on lane U are rare)
                new RingQueue<QueuedMessage>(64)    // Lane.Bulk
            };
            _bulkWindow = _config.BulkWindowMinBytes;
            _name = "outbox of peer " + peer.Value.ToString(CultureInfo.InvariantCulture) + " (connection "
                    + connection.Id.ToString(CultureInfo.InvariantCulture) + ")";
        }

        public PeerId Peer { get; }

        /// <summary>The wrapped connection.</summary>
        public IConnection Connection => _connection;

        /// <summary>
        /// The transport's real-time statistics (queried from the connection; the last known values when it fails). Reads
        /// within one <see cref="StatsFrame"/> stamp share one query, so every main-thread consumer of a frame sees the same
        /// numbers; the pump and <see cref="CanAcceptReliable"/> always query.
        /// </summary>
        public ConnectionStats Stats
        {
            get
            {
                long frame = StatsFrame.Current;
                if (frame != 0)
                {
                    lock (_statsLock)
                    {
                        if (_statsFrame == frame)
                        {
                            _statsReused++;
                            return _lastStats;
                        }
                    }
                }
                ConnectionStats stats = QueryStats();
                if (frame != 0)
                {
                    lock (_statsLock) _statsFrame = frame;
                }
                return stats;
            }
        }

        /// <summary>Transport statistics queries made (the pump's included).</summary>
        public long StatsQueries => Interlocked.Read(ref _statsQueries);

        /// <summary><see cref="Stats"/> reads answered from the frame's query.</summary>
        public long StatsReused
        {
            get
            {
                lock (_statsLock) return _statsReused;
            }
        }

        /// <summary>Bytes waiting in the outbox, all lanes (they count as P0 backlog, ARCHITECTURE.md#live-session).</summary>
        public long QueuedBytes => Interlocked.Read(ref _queuedBytes);

        /// <summary>Bytes of reliable messages waiting on lanes R and U.</summary>
        public long QueuedReliableBytes => Interlocked.Read(ref _queuedHighBytes);

        /// <summary>Bytes waiting on lane B.</summary>
        public long QueuedBulkBytes => Interlocked.Read(ref _queuedBulkBytes);

        /// <summary>Messages waiting, all lanes.</summary>
        public int QueuedMessages => Volatile.Read(ref _queuedMessages);

        /// <summary>Age of the oldest queued message in seconds (0 when nothing is queued).</summary>
        public double OldestQueuedSeconds
        {
            get
            {
                long oldest = long.MaxValue;
                lock (_queueLock)
                {
                    for (int i = 0; i < LaneCount; i++)
                    {
                        if (_queues[i].Count > 0) oldest = Math.Min(oldest, _queues[i].PeekRef().EnqueuedTicks);
                    }
                }
                if (oldest == long.MaxValue) return 0;
                double age = (_clock.Ticks - oldest) / (double)TimeSpan.TicksPerSecond;
                return age > 0 ? age : 0;
            }
        }

        /// <summary>
        /// Queued bytes or the age of the oldest queued message above the disconnect thresholds (ARCHITECTURE.md#live-session); the
        /// host then disconnects the peer with "connection too slow for this session". Also true once the outbox is
        /// faulted (<see cref="IsFaulted"/>), because its reliable stream cannot continue either.
        /// </summary>
        public bool OverLimit
        {
            get
            {
                if (_faulted) return true;
                if (_closed) return false;
                return QueuedBytes > _config.DisconnectQueuedBytes || OldestQueuedSeconds > _config.DisconnectQueuedSeconds;
            }
        }

        /// <summary>Reliable messages (all lanes) handed to the connection.</summary>
        public long SentReliableMessages => Interlocked.Read(ref _sentReliable);

        /// <summary>Lane B messages handed to the connection (included in <see cref="SentReliableMessages"/>).</summary>
        public long SentBulkMessages => Interlocked.Read(ref _sentBulk);

        /// <summary>Bytes of reliable messages handed to the connection.</summary>
        public long SentReliableBytes => Interlocked.Read(ref _sentBytes);

        /// <summary>Times the connection answered <see cref="SendResult.Busy"/> to a queued message (each one retried later).</summary>
        public long BusyRetries => Interlocked.Read(ref _busyRetries);

        public long SentUnreliableMessages => Interlocked.Read(ref _sentUnreliable);

        /// <summary>Unreliable messages the connection refused (they are dropped, as unreliable traffic may be).</summary>
        public long DroppedUnreliableMessages => Interlocked.Read(ref _droppedUnreliable);

        /// <summary>Times the connection failed a queued message with an error other than Busy (each one retried later).</summary>
        public long SendFailures => Interlocked.Read(ref _sendFailures);

        /// <summary>Calls of <see cref="Pump"/> that ran.</summary>
        public long Pumps => Interlocked.Read(ref _pumps);

        /// <summary>The bulk window of the last pump that reached lane B.</summary>
        public int CurrentBulkWindowBytes => Volatile.Read(ref _bulkWindow);

        /// <summary>The connection reported <see cref="SendResult.Closed"/>, or <see cref="Dispose"/> was called.</summary>
        public bool IsClosed => _closed;

        /// <summary>The transport refused a queued reliable message for good (<see cref="SendResult.TooLarge"/>).</summary>
        public bool IsFaulted => _faulted;

        public string FaultReason => _faultReason;

        /// <summary>A copy of the configuration in use.</summary>
        public OutboxConfig Config => _config.Clone();

        /// <summary>
        /// Reliable: copied into the lane's queue, never dropped (<see cref="SendResult.Ok"/>; <see cref="SendResult.Closed"/>
        /// once the connection is closed; <see cref="SendResult.TooLarge"/> above <see cref="Limits.MaxMessageBytes"/>).
        /// Unreliable: sent at once, dropped when refused (the connection's result is returned).
        /// </summary>
        public SendResult Send(Lane lane, Delivery delivery, byte[] buffer, int offset, int count)
        {
            if (buffer == null)
            {
                if (count != 0 || offset != 0) throw new ArgumentNullException(nameof(buffer));
                buffer = Array.Empty<byte>();
            }
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count), "the range lies outside the buffer");
            if ((byte)lane >= LaneCount) throw new ArgumentOutOfRangeException(nameof(lane), lane, "undefined lane");
            if (delivery == Delivery.Unreliable) return SendUnreliable(lane, buffer, offset, count);
            if (delivery != Delivery.ReliableOrdered) throw new ArgumentOutOfRangeException(nameof(delivery), delivery, "undefined delivery");

            if (_closed || _faulted) return SendResult.Closed;
            if (count > Limits.MaxMessageBytes)
            {
                _log.Error(LogCategory.Net, _name + ": refused a reliable " + count.ToString(CultureInfo.InvariantCulture)
                                            + "-byte message on lane " + lane + " above the transport cap of "
                                            + Limits.MaxMessageBytes.ToString(CultureInfo.InvariantCulture) + " bytes (fragment it first)");
                return SendResult.TooLarge;
            }
            byte[] copy = count == 0 ? Array.Empty<byte>() : BufferPool.Rent(count);
            if (count > 0) System.Buffer.BlockCopy(buffer, offset, copy, 0, count);
            long now = _clock.Ticks;
            lock (_queueLock)
            {
                if (_closed || _faulted)
                {
                    BufferPool.Return(copy);
                    return SendResult.Closed;
                }
                _queues[(int)lane].Enqueue(new QueuedMessage { Buffer = copy, Length = count, EnqueuedTicks = now });
                Interlocked.Increment(ref _queuedMessages);
                Interlocked.Add(ref _queuedBytes, count);
                if (lane == Lane.Bulk) Interlocked.Add(ref _queuedBulkBytes, count);
                else Interlocked.Add(ref _queuedHighBytes, count);
            }
            return SendResult.Ok;
        }

        /// <summary>
        /// True when lane R's window has room for <paramref name="bytes"/> more: the reliable bytes queued here plus
        /// the transport's pending bytes plus the request stay within <see cref="OutboxConfig.SendBufferBytes"/> −
        /// <see cref="OutboxConfig.ReliableHeadroomBytes"/>. Callers ask before they queue deferrable traffic
        /// (ARCHITECTURE.md#deferred-values-and-link-pacing); false once the outbox is closed.
        /// </summary>
        public bool CanAcceptReliable(int bytes)
        {
            if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
            if (_closed || _faulted) return false;
            ConnectionStats stats = QueryStats();
            long pending = Pending(stats);
            return QueuedReliableBytes + pending + bytes <= _config.ReliableWindowBytes;
        }

        /// <summary>
        /// Hands queued messages to the connection inside the windows (see the class summary): lane R, then reliable
        /// messages of lane U, then lane B when both are empty. Stops at the first <see cref="SendResult.Busy"/> (the
        /// message stays at the head) or failure. Never throws for transport errors; they are counted and logged.
        /// </summary>
        [ThreadAffinity(ThreadKind.Network)]
        public void Pump()
        {
            if (!Monitor.TryEnter(_pumpLock)) return;
            try
            {
                Interlocked.Increment(ref _pumps);
                if (_closed || _faulted)
                {
                    ReleaseQueued();
                    return;
                }
                long now = _clock.Ticks;
                if (QueuedMessages == 0)
                {
                    _busySinceTicks = -1;
                    ReportOverLimit(false);
                    return;
                }

                ConnectionStats stats = QueryStats();
                long pending = Pending(stats);
                long reliableWindow = _config.ReliableWindowBytes;
                DrainOutcome outcome = Drain(Lane.Reliable, reliableWindow, ref pending, now);
                if (outcome == DrainOutcome.Drained) outcome = Drain(Lane.Unreliable, reliableWindow, ref pending, now);
                if (outcome == DrainOutcome.Drained)
                {
                    int window = BulkWindow.Compute(stats.SendRateBytesPerSecond, _config.BulkWindowMinBytes, _config.BulkWindowMaxBytes);
                    Volatile.Write(ref _bulkWindow, window);
                    outcome = Drain(Lane.Bulk, window, ref pending, now);
                }

                TrackBusy(outcome == DrainOutcome.Busy, now);
                if (_closed || _faulted)
                {
                    ReleaseQueued();
                    return;
                }
                ReportOverLimit(OverLimit);
            }
            finally
            {
                Monitor.Exit(_pumpLock);
            }
        }

        /// <summary>Drops every queued message and refuses further reliable sends (the connection is not closed).</summary>
        public void Dispose()
        {
            lock (_pumpLock)
            {
                _closed = true;
                ReleaseQueued();
            }
        }

        public override string ToString()
        {
            return _name + ": " + QueuedMessages.ToString(CultureInfo.InvariantCulture) + " queued ("
                   + QueuedBytes.ToString(CultureInfo.InvariantCulture) + " B), " + SentReliableMessages.ToString(CultureInfo.InvariantCulture)
                   + " sent, " + BusyRetries.ToString(CultureInfo.InvariantCulture) + " busy retries"
                   + (_faulted ? ", faulted" : _closed ? ", closed" : string.Empty);
        }

        // ---- internals ----

        private SendResult SendUnreliable(Lane lane, byte[] buffer, int offset, int count)
        {
            if (_closed || _faulted) return SendResult.Closed;
            if (count > Limits.MaxMessageBytes)
            {
                Interlocked.Increment(ref _droppedUnreliable);
                return SendResult.TooLarge;
            }
            SendResult result;
            try
            {
                result = _connection.Send(lane, Delivery.Unreliable, buffer, offset, count);
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                LogFailure("the connection threw on an unreliable send", e);
                result = SendResult.Failed;
            }
            if (result == SendResult.Ok)
            {
                Interlocked.Increment(ref _sentUnreliable);
            }
            else
            {
                Interlocked.Increment(ref _droppedUnreliable);
                if (result == SendResult.Closed) MarkClosed();
            }
            return result;
        }

        private DrainOutcome Drain(Lane lane, long window, ref long pending, long now)
        {
            RingQueue<QueuedMessage> queue = _queues[(int)lane];
            while (true)
            {
                QueuedMessage head;
                lock (_queueLock)
                {
                    if (queue.Count == 0) return DrainOutcome.Drained;
                    head = queue.PeekRef();
                }
                // an empty transport always takes one message, so a window smaller than a message cannot stall the lane
                if (pending > 0 && pending + head.Length > window) return DrainOutcome.WindowFull;

                SendResult result;
                try
                {
                    result = _connection.Send(lane, Delivery.ReliableOrdered, head.Buffer, 0, head.Length);
                }
                catch (Exception e) when (!(e is OutOfMemoryException))
                {
                    Interlocked.Increment(ref _sendFailures);
                    LogFailure("the connection threw on a queued " + lane + " message; it stays queued", e);
                    return DrainOutcome.Failed;
                }

                switch (result)
                {
                    case SendResult.Ok:
                        lock (_queueLock)
                        {
                            queue.Dequeue();
                            Interlocked.Decrement(ref _queuedMessages);
                            Interlocked.Add(ref _queuedBytes, -head.Length);
                            if (lane == Lane.Bulk) Interlocked.Add(ref _queuedBulkBytes, -head.Length);
                            else Interlocked.Add(ref _queuedHighBytes, -head.Length);
                        }
                        BufferPool.Return(head.Buffer);
                        pending += head.Length;
                        Interlocked.Increment(ref _sentReliable);
                        Interlocked.Add(ref _sentBytes, head.Length);
                        if (lane == Lane.Bulk) Interlocked.Increment(ref _sentBulk);
                        continue;
                    case SendResult.Busy:
                        Interlocked.Increment(ref _busyRetries);
                        return DrainOutcome.Busy;
                    case SendResult.Closed:
                        MarkClosed();
                        return DrainOutcome.Stopped;
                    case SendResult.TooLarge:
                        Fault("the transport refused a queued " + head.Length.ToString(CultureInfo.InvariantCulture) + "-byte " + lane
                              + " message as too large; the reliable stream cannot continue");
                        return DrainOutcome.Stopped;
                    default:
                        Interlocked.Increment(ref _sendFailures);
                        LogFailure("the connection failed a queued " + lane + " message (" + result + "); it stays queued", null);
                        return DrainOutcome.Failed;
                }
            }
        }

        private ConnectionStats QueryStats()
        {
            ConnectionStats stats;
            Interlocked.Increment(ref _statsQueries);
            try
            {
                stats = _connection.GetStats();
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                LogFailure("the connection's statistics are unavailable", e);
                lock (_statsLock) return _lastStats;
            }
            lock (_statsLock) _lastStats = stats;
            return stats;
        }

        /// <summary>The transport's pending bytes (its send buffer counts both kinds).</summary>
        private static long Pending(in ConnectionStats stats)
        {
            return Math.Max(0L, stats.PendingReliableBytes) + Math.Max(0L, stats.PendingUnreliableBytes);
        }

        private void MarkClosed()
        {
            if (_closed) return;
            _closed = true;
            _log.Info(LogCategory.Net, _name + ": the connection is closed; " + QueuedMessages.ToString(CultureInfo.InvariantCulture)
                                       + " queued message(s) (" + QueuedBytes.ToString(CultureInfo.InvariantCulture) + " bytes) are dropped");
        }

        private void Fault(string reason)
        {
            _faultReason = reason;
            _faulted = true;
            _log.Error(LogCategory.Net, _name + ": " + reason);
        }

        /// <summary>Returns every queued buffer to the pool (under the pump lock, so no pump is using a head buffer).</summary>
        private void ReleaseQueued()
        {
            lock (_queueLock)
            {
                for (int i = 0; i < LaneCount; i++)
                {
                    RingQueue<QueuedMessage> queue = _queues[i];
                    while (queue.Count > 0)
                    {
                        QueuedMessage message = queue.Dequeue();
                        BufferPool.Return(message.Buffer);
                    }
                }
                Interlocked.Exchange(ref _queuedMessages, 0);
                Interlocked.Exchange(ref _queuedBytes, 0);
                Interlocked.Exchange(ref _queuedHighBytes, 0);
                Interlocked.Exchange(ref _queuedBulkBytes, 0);
            }
        }

        private void TrackBusy(bool busy, long now)
        {
            if (!busy)
            {
                _busySinceTicks = -1;
                return;
            }
            if (_busySinceTicks < 0)
            {
                _busySinceTicks = now;
                return;
            }
            if (now - _busySinceTicks >= BusyWarnTicks && now - _lastBusyWarnTicks >= BusyWarnTicks)
            {
                _lastBusyWarnTicks = now;
                _log.Warn(LogCategory.Net, _name + ": the transport has refused sends for "
                                           + ((now - _busySinceTicks) / (double)TimeSpan.TicksPerSecond).ToString("0.0", CultureInfo.InvariantCulture)
                                           + " s; " + QueuedBytes.ToString(CultureInfo.InvariantCulture) + " bytes queued, "
                                           + BusyRetries.ToString(CultureInfo.InvariantCulture) + " busy retries so far");
            }
        }

        private void ReportOverLimit(bool over)
        {
            if (over == _overLimitReported) return;
            _overLimitReported = over;
            if (over)
            {
                _log.Warn(LogCategory.Net, _name + ": over the disconnect limit (" + QueuedBytes.ToString(CultureInfo.InvariantCulture)
                                           + " bytes queued, oldest " + OldestQueuedSeconds.ToString("0.0", CultureInfo.InvariantCulture)
                                           + " s; limits " + _config.DisconnectQueuedBytes.ToString(CultureInfo.InvariantCulture) + " bytes, "
                                           + _config.DisconnectQueuedSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s)"
                                           + (_faulted ? "; faulted: " + _faultReason : string.Empty));
            }
            else
            {
                _log.Info(LogCategory.Net, _name + ": back under the disconnect limit");
            }
        }

        private void LogFailure(string what, Exception exception)
        {
            // rate-limited: a failing transport is retried every pump (~1 ms)
            long now = _clock.Ticks;
            long last = Interlocked.Read(ref _lastFailureLogTicks);
            if (last != long.MinValue && now - last < FailureLogTicks) return;
            if (Interlocked.CompareExchange(ref _lastFailureLogTicks, now, last) != last) return;
            _log.Warn(LogCategory.Net, _name + ": " + what + " (" + SendFailures.ToString(CultureInfo.InvariantCulture) + " failure(s) so far)", exception);
        }
    }
}
