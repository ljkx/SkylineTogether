using System;

namespace SkylineTogether.Core.Replay
{
    /// <summary>
    /// A client-side transport that records what it delivers (ARCHITECTURE.md#solo-test-record-and-replay): every connection event and every received
    /// message goes into a <see cref="RecordingWriter"/> right before the client's sink gets it, with the time since
    /// <see cref="Origin"/> and the host frame of <see cref="Frame"/>. Sending is not recorded (the replay's client
    /// sends its own messages). A failing writer never disturbs the session: recording just stops.
    /// </summary>
    public sealed class RecordingTransport : ITransport, ITransportEventSink
    {
        private readonly ITransport _inner;
        private readonly RecordingWriter _writer;
        private readonly IClock _clock;
        private ITransportEventSink _sink;

        public RecordingTransport(ITransport inner, RecordingWriter writer, IClock clock, Func<uint> frame, double origin)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            Frame = frame ?? (() => 0u);
            Origin = origin;
        }

        /// <summary>The clock reading recorded as second 0.</summary>
        public double Origin { get; }

        /// <summary>The host frame stamped on each entry.</summary>
        public Func<uint> Frame { get; }

        /// <summary>False pauses recording (events still reach the client).</summary>
        public bool Recording { get; set; } = true;

        public RecordingWriter Writer => _writer;

        public TransportKind Kind => _inner.Kind;

        /// <summary>Seconds since <see cref="Origin"/>.</summary>
        public double Now => _clock.Seconds - Origin;

        [ThreadAffinity(ThreadKind.Main)]
        public void Listen(ListenOptions options) => _inner.Listen(options);

        [ThreadAffinity(ThreadKind.Main)]
        public IConnection Connect(Endpoint endpoint) => _inner.Connect(endpoint);

        [ThreadAffinity(ThreadKind.Network)]
        public void Poll(ITransportEventSink sink)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _inner.Poll(this);
        }

        public void Dispose() => _inner.Dispose();

        void ITransportEventSink.OnConnected(IConnection connection)
        {
            if (Recording) _writer.Connected(Now, SafeFrame());
            _sink?.OnConnected(connection);
        }

        void ITransportEventSink.OnDisconnected(IConnection connection, int reason, string text)
        {
            if (Recording) _writer.Disconnected(Now, SafeFrame(), reason, text);
            _sink?.OnDisconnected(connection, reason, text);
        }

        void ITransportEventSink.OnMessage(IConnection connection, Lane lane, byte[] buffer, int offset, int count)
        {
            if (Recording && count + 1 <= RecordingFormat.MaxPayloadBytes) _writer.Message(Now, SafeFrame(), lane, buffer, offset, count);
            _sink?.OnMessage(connection, lane, buffer, offset, count);
        }

        private uint SafeFrame()
        {
            try
            {
                return Frame();
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
