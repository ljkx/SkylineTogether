using System;
using System.Collections.Generic;
using System.IO;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;

namespace SkylineTogether.Core.Replay
{
    /// <summary>Pace and limits of a <see cref="PlaybackTransport"/>.</summary>
    public sealed class PlaybackOptions
    {
        /// <summary>
        /// Recorded seconds played per real second once the client's city is loaded; 0 or less plays as fast as the
        /// client takes it (<see cref="PlaybackTransport.Hold"/> applies the brakes). Everything before the client's load
        /// is delivered at once (the snapshot download).
        /// </summary>
        public double Speed = 1.0;

        /// <summary>Messages delivered per poll at most (one poll per frame), so a burst never freezes a frame.</summary>
        public int MaxMessagesPerPoll = 4096;

        /// <summary>Bytes delivered per poll at most.</summary>
        public int MaxBytesPerPoll = 24 << 20;

        /// <summary>The display name of the recorded host (what the connection reports as its peer).</summary>
        public string HostName = "Recorded host";
    }

    /// <summary>
    /// Plays a recording into a client as if it came from the host (ARCH §13.5): the client connects to this transport
    /// like to a real one, gets <see cref="ITransportEventSink.OnConnected"/>, and then receives the recorded messages,
    /// byte for byte, through its normal receive path. Nothing touches the network: what the client sends is decoded
    /// only to notice its progress (<see cref="RecordingBarrier"/>) and counted per kind, then dropped.
    /// <para>
    /// Pace: the stream up to the <see cref="RecordingBarrier.LoadDone"/> barrier (the join: welcome, snapshot, the first
    /// buffered batches) is delivered as fast as the limits allow; the barrier holds playback until the client itself
    /// reports its load as done; from there the recorded timing is reproduced at <see cref="PlaybackOptions.Speed"/>.
    /// While <see cref="Hold"/> returns true (the client's apply queue is long) the playback clock stands still, so a
    /// slow client never receives a burst. The end of the recording sets <see cref="Finished"/>; the connection stays
    /// open, so the caller decides when the session ends.
    /// </para>
    /// Main thread (the session polls once per frame).
    /// </summary>
    public sealed class PlaybackTransport : ITransport
    {
        private readonly RecordingReader _reader;
        private readonly IClock _clock;
        private readonly PlaybackOptions _options;
        private readonly ILog _log;
        private readonly HashSet<RecordingBarrier> _released = new HashSet<RecordingBarrier>();
        private readonly Dictionary<MessageKind, long> _sentByKind = new Dictionary<MessageKind, long>();
        private readonly EnvelopeCodec _codec = new EnvelopeCodec(null);
        private readonly Reassembler _reassembler = new Reassembler();
        private readonly ByteWriter _scratch = new ByteWriter(1024);
        private readonly ByteWriter _scratch2 = new ByteWriter(1024);
        private PlaybackConnection _connection;
        private RecordingEntry _next;
        private bool _connectedPending;
        private bool _paced;
        private double _base;
        private double _played;
        private double _lastPoll = double.NaN;
        private bool _disposed;

        public PlaybackTransport(RecordingReader reader, IClock clock, PlaybackOptions options, ILog log)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _options = options ?? new PlaybackOptions();
            _log = log;
        }

        /// <summary>
        /// The password a replaying client is given: a recording of a password-protected session contains the host's
        /// challenge; the client answers it (to nowhere) and the recorded welcome follows, so any non-empty value works.
        /// </summary>
        public const string AnyPassword = "replay";

        public TransportKind Kind => TransportKind.Playback;

        public RecordingHeader Header => _reader.Header;

        /// <summary>Applies the brakes while it returns true (the playback clock stops; nothing is delivered after the join).</summary>
        public Func<bool> Hold { get; set; }

        /// <summary>The whole recording was delivered (or it ended early: <see cref="EndReason"/>).</summary>
        public bool Finished { get; private set; }

        /// <summary>Why the recording ended: its end note, the host's disconnect, or a truncated or damaged file.</summary>
        public string EndReason { get; private set; } = "";

        /// <summary>The barrier playback is waiting at, if any.</summary>
        public RecordingBarrier? WaitingFor { get; private set; }

        /// <summary>True once playback runs at the recorded pace (the client's load is done).</summary>
        public bool Paced => _paced;

        public long MessagesDelivered { get; private set; }

        public long BytesDelivered { get; private set; }

        /// <summary>Recorded seconds of the newest delivered message.</summary>
        public double DeliveredSeconds { get; private set; }

        /// <summary>The host frame of the newest delivered message.</summary>
        public uint DeliveredFrame { get; private set; }

        /// <summary>The newest note of the recording that was passed (checkpoints, the recorder's state).</summary>
        public string LastNote { get; private set; } = "";

        /// <summary>The fraction of the file played (0..1; 0 when the length is unknown).</summary>
        public double Progress
        {
            get
            {
                long length = _reader.Length;
                return length > 0 ? Math.Min(1.0, _reader.Position / (double)length) : 0.0;
            }
        }

        /// <summary>Messages the client sent, per kind (they go nowhere; a repair request is a divergence the client noticed).</summary>
        public IReadOnlyDictionary<MessageKind, long> SentByKind => _sentByKind;

        public long MessagesSent { get; private set; }

        /// <summary>True once the client closed its connection.</summary>
        public bool ClosedByClient => _connection != null && _connection.Closed;

        [ThreadAffinity(ThreadKind.Main)]
        public void Listen(ListenOptions options)
        {
            throw new InvalidOperationException("a recording cannot accept connections");
        }

        [ThreadAffinity(ThreadKind.Main)]
        public IConnection Connect(Endpoint endpoint)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PlaybackTransport));
            if (_connection != null) throw new InvalidOperationException("a recording plays into one connection");
            _connection = new PlaybackConnection(this, new PeerIdentity(0, "replay", string.IsNullOrEmpty(_options.HostName) ? "Recorded host" : _options.HostName));
            _connectedPending = true;
            return _connection;
        }

        /// <summary>Lets playback pass a barrier without the client (tests; the client reports it by itself).</summary>
        public void Release(RecordingBarrier barrier) => _released.Add(barrier);

        [ThreadAffinity(ThreadKind.Network)]
        public void Poll(ITransportEventSink sink)
        {
            if (sink == null) throw new ArgumentNullException(nameof(sink));
            if (_disposed || _connection == null) return;
            double now = _clock.Seconds;
            double dt = double.IsNaN(_lastPoll) ? 0.0 : Math.Max(0.0, now - _lastPoll);
            _lastPoll = now;
            if (_connectedPending)
            {
                _connectedPending = false;
                sink.OnConnected(_connection);
            }
            if (_connection.Closed || Finished) return;
            bool held = false;
            try
            {
                held = Hold != null && Hold();
            }
            catch (Exception)
            {
                held = false;
            }
            bool fast = _options.Speed <= 0;
            if (_paced && !held && !fast) _played += dt * _options.Speed;
            int messages = 0;
            long bytes = 0;
            while (!_connection.Closed)
            {
                if (_next == null && !ReadNext()) break;
                RecordingEntry e = _next;
                if (e.Kind == RecordingEntryKind.Barrier)
                {
                    if (!_released.Contains(e.Barrier))
                    {
                        WaitingFor = e.Barrier;
                        break;
                    }
                    WaitingFor = null;
                    _next = null;
                    _paced = true;
                    _base = e.Seconds;
                    _played = 0;
                    continue;
                }
                if (e.Kind == RecordingEntryKind.Message)
                {
                    if (_paced && (held || (!fast && e.Seconds - _base > _played))) break;
                    if (messages >= _options.MaxMessagesPerPoll || bytes >= _options.MaxBytesPerPoll) break;
                    _next = null;
                    messages++;
                    bytes += e.Data.Length;
                    MessagesDelivered++;
                    BytesDelivered += e.Data.Length;
                    DeliveredSeconds = e.Seconds;
                    DeliveredFrame = e.Frame;
                    sink.OnMessage(_connection, e.Lane, e.Data, 0, e.Data.Length);
                    continue;
                }
                _next = null;
                switch (e.Kind)
                {
                    case RecordingEntryKind.Note:
                        LastNote = e.Text ?? "";
                        continue;
                    case RecordingEntryKind.Disconnected:
                        Finish("the host closed the connection while recording (" + e.Text + ")");
                        return;
                    case RecordingEntryKind.End:
                        Finish(string.IsNullOrEmpty(e.Text) ? "the recording ended" : e.Text);
                        return;
                    default:
                        continue; // Connected: already reported when the client connected
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_connection != null) _connection.Closed = true;
            _reader.Dispose();
        }

        private bool ReadNext()
        {
            try
            {
                if (_reader.TryRead(out RecordingEntry e))
                {
                    _next = e;
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
            {
                Finish("reading the recording failed: " + ex.Message);
                return false;
            }
            if (_reader.Corrupt) Finish("the recording is damaged (" + _reader.CorruptReason + ")");
            else if (_reader.Truncated) Finish("the recording was cut off (the game was closed while recording)");
            else Finish("the recording ended without an end marker");
            return false;
        }

        private void Finish(string reason)
        {
            if (Finished) return;
            Finished = true;
            EndReason = reason ?? "";
            WaitingFor = null;
            _log?.Info(LogCategory.Session, "replay: end of the recording after " + MessagesDelivered + " messages (" + EndReason + ")");
        }

        /// <summary>A message the client sent: counted by kind; a successful load report releases its barrier.</summary>
        private void OnClientSent(byte[] buffer, int offset, int count)
        {
            MessagesSent++;
            try
            {
                Envelope e = _codec.Decode(buffer, offset, count, _scratch);
                if ((e.Flags & EnvelopeFlags.Fragment) != 0)
                {
                    if (!_reassembler.Accept(e, out byte[] message, out int length)) return;
                    e = _codec.Decode(message, 0, length, _scratch2);
                }
                _sentByKind.TryGetValue(e.Kind, out long n);
                _sentByKind[e.Kind] = n + 1;
                if (e.Kind != MessageKind.Control) return;
                ControlMessage c = ControlMessage.Decode(e.Body, e.BodyOffset, e.BodyCount);
                if (c.Kind == ControlKind.LoadDone && c.Payload != null && c.Payload.Length > 0 && c.Payload[0] != 0)
                {
                    _released.Add(RecordingBarrier.LoadDone);
                    _log?.Info(LogCategory.Session, "replay: the client loaded the city; playing on at " + (_options.Speed > 0 ? _options.Speed.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "x" : "full speed"));
                }
            }
            catch (Exception ex) when (ex is DecodeException || ex is ArgumentException || ex is InvalidOperationException || ex is IndexOutOfRangeException)
            {
                // the client's own bytes; they go nowhere anyway
            }
        }

        private sealed class PlaybackConnection : IConnection
        {
            private readonly PlaybackTransport _owner;

            public PlaybackConnection(PlaybackTransport owner, PeerIdentity peer)
            {
                _owner = owner;
                Peer = peer;
            }

            public int Id => 1;

            public PeerIdentity Peer { get; }

            public bool Closed;

            public SendResult Send(Lane lane, Delivery delivery, byte[] buffer, int offset, int count)
            {
                if (Closed) return SendResult.Closed;
                if (buffer == null || offset < 0 || count < 0 || offset > buffer.Length - count) return SendResult.Failed;
                _owner.OnClientSent(buffer, offset, count);
                return SendResult.Ok;
            }

            public ConnectionStats GetStats()
            {
                return new ConnectionStats { PingMs = 0, Quality = Closed ? 0f : 1f, SendRateBytesPerSecond = 64 << 20 };
            }

            public void Close(int reason, string text, bool linger)
            {
                Closed = true;
            }
        }
    }
}
