using System;
using System.Collections.Generic;
using System.IO;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Motion;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Replay
{
    /// <summary>Settings of a <see cref="RecorderPeer"/>.</summary>
    public sealed class RecorderPeerOptions
    {
        /// <summary>The name in the host's player list.</summary>
        public string Name = "Test recorder";

        /// <summary>The host's password (the recorder joins through the normal handshake).</summary>
        public string Password = "";

        /// <summary>Seconds between apply acknowledgements and motion acks.</summary>
        public double AckInterval = 0.1;

        /// <summary>Seconds between view reports (the host camera).</summary>
        public double ViewInterval = 0.1;

        /// <summary>Ask for the host's icon sets once live, as every client does.</summary>
        public bool RequestIconFullSync = true;

        /// <summary>Longest wait for the host's snapshot (the host saves the city first).</summary>
        public double SnapshotWaitTimeoutSeconds = 900;
    }

    /// <summary>
    /// The virtual client of solo testing (ARCH §13.5): joins the host's own session in-process and records everything it
    /// receives. It is a real <see cref="SessionClient"/> on a <see cref="LoopbackTransport"/> pair whose host end is
    /// attached to the host's <see cref="CompositeTransport"/>, so the host admits it through the normal handshake, takes
    /// a join snapshot for it (the same <c>ISnapshotProvider</c> path a joiner gets), streams the snapshot, queues its
    /// per-peer batches and finally treats it as live like any other player. Its "world loader" keeps the downloaded
    /// snapshot as a file and reports success at once; a <see cref="RecordingBarrier.LoadDone"/> marks that point in
    /// the stream.
    /// <para>
    /// It answers like a perfect client that applies everything instantly: apply acknowledgements for every
    /// <c>Epoch</c> message received (id reuse on the host goes on), motion acks for every <c>MotionControl</c>, view
    /// reports with the camera <see cref="View"/> supplies (normally the host's own, so movers and attention values
    /// follow what the host looks at), and the icon full-sync request every client sends when it goes live.
    /// </para>
    /// Main thread: <see cref="Tick"/> once per frame after the host's tick.
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public sealed class RecorderPeer : IDisposable
    {
        private const double StopTimeoutSeconds = 5.0;

        private readonly CompositeTransport _host;
        private readonly LoopbackTransport _hostEnd;
        private readonly RecordingTransport _tap;
        private readonly RecordingWriter _writer;
        private readonly RecorderPeerOptions _options;
        private readonly IClock _clock;
        private readonly ILog _log;
        private readonly ByteWriter _body = new ByteWriter(64);
        private readonly ByteReader _reader = new ByteReader();
        private uint _controlSeq;
        private bool _hasControl;
        private uint _ackedControl;
        private bool _ackedAnyControl;
        private long _ackedEpochs = -1;
        private double _lastAck = double.NegativeInfinity;
        private double _lastView = double.NegativeInfinity;
        private double _stopAt = -1;
        private bool _disposed;

        public RecorderPeer(CompositeTransport hostTransport, IGameInfoProvider info, RecordingWriter writer, Func<Stream> openSnapshotTarget, IClock clock, Func<uint> frame, ILog log, RecorderPeerOptions options = null)
        {
            _host = hostTransport ?? throw new ArgumentNullException(nameof(hostTransport));
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _options = options ?? new RecorderPeerOptions();
            if (info == null) throw new ArgumentNullException(nameof(info));
            var (hostEnd, clientEnd) = LoopbackTransport.CreatePair(new PeerIdentity(0, "recorder-host", "Host"), new PeerIdentity(0, "recorder", _options.Name));
            _hostEnd = hostEnd;
            _hostEnd.Listen(new ListenOptions { MaxConnections = 1 });
            _tap = new RecordingTransport(clientEnd, writer, clock, frame, clock.Seconds);
            Loader = new RecorderLoader(openSnapshotTarget, () => _writer.Barrier(_tap.Now, SafeFrame(frame), RecordingBarrier.LoadDone));
            Client = new SessionClient(_tap, info, Loader, new ClientOptions
            {
                PlayerName = _options.Name,
                Password = _options.Password ?? "",
                SnapshotWaitTimeoutSeconds = _options.SnapshotWaitTimeoutSeconds,
            }, clock, log);
            Client.AppMessage += OnAppMessage;
            Client.BecameLive += OnLive;
            _host.Attach(_hostEnd);
        }

        /// <summary>The recorder's own client (its state, its peer id once welcomed).</summary>
        public SessionClient Client { get; }

        public RecorderLoader Loader { get; }

        /// <summary>Fills the view report (camera, selection, infoview); null sends none (no movers or attention values).</summary>
        public Action<ViewReport> View { get; set; }

        /// <summary>The peer id the host gave the recorder (valid once welcomed).</summary>
        public PeerId Peer => Client.LocalPeer;

        public bool IsLive => Client.State == ClientState.Live;

        /// <summary>Seconds since the recording started (the time of the next entry).</summary>
        public double Seconds => _tap.Now;

        public RecordingWriter Writer => _writer;

        /// <summary>True once <see cref="Stop"/> was called.</summary>
        public bool Stopping => _stopAt >= 0;

        /// <summary>The recorder left and the host has dropped it (or the stop timed out): <see cref="Dispose"/> may run.</summary>
        public bool Finished => _stopAt >= 0 && (_hostEnd.ConnectionCount == 0 || _clock.Seconds - _stopAt > StopTimeoutSeconds);

        /// <summary>Connects to the host (through the in-process pair).</summary>
        public void Start()
        {
            _log.Info(LogCategory.Session, "test recorder joining the session as \"" + _options.Name + "\"");
            Client.Connect(new Endpoint());
        }

        public void Tick()
        {
            if (_disposed) return;
            Client.Tick();
            double now = _clock.Seconds;
            if (Client.State == ClientState.Live && !Stopping)
            {
                if (now - _lastAck >= _options.AckInterval)
                {
                    _lastAck = now;
                    long applied = Client.ApplyAcks.AppliedThrough;
                    if (applied != _ackedEpochs)
                    {
                        _ackedEpochs = applied;
                        _body.Reset();
                        ApplyAckMessage.Write(_body, applied);
                        Client.SendToHost(MessageKind.ApplyAck, _body.Buffer, 0, _body.Length);
                    }
                    if (_hasControl && (!_ackedAnyControl || _ackedControl != _controlSeq))
                    {
                        _ackedAnyControl = true;
                        _ackedControl = _controlSeq;
                        _body.Reset();
                        MotionClientStager.WriteAck(_body, _controlSeq);
                        Client.SendToHost(MessageKind.Ack, _body.Buffer, 0, _body.Length);
                    }
                }
                Action<ViewReport> view = View;
                if (view != null && now - _lastView >= _options.ViewInterval)
                {
                    _lastView = now;
                    var report = new ViewReport();
                    try
                    {
                        view(report);
                        _body.Reset();
                        ViewReport.Write(_body, report);
                        Client.SendToHost(MessageKind.View, _body.Buffer, 0, _body.Length);
                    }
                    catch (Exception ex)
                    {
                        _log.Debug(LogCategory.Session, "test recorder: no view report this time: " + ex.Message);
                    }
                }
            }
            _writer.FlushIfDue(now);
        }

        /// <summary>Leaves the session (the host drops the recorder) and ends the stream with <paramref name="reason"/>.</summary>
        public void Stop(string reason)
        {
            if (Stopping) return;
            _stopAt = _clock.Seconds;
            _writer.End(_tap.Now, 0, reason ?? "recording stopped");
            _tap.Recording = false;
            Client.Leave();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!Stopping) Stop("recording stopped");
            _host.Detach(_hostEnd);
            try
            {
                Client.Dispose();
            }
            finally
            {
                _hostEnd.Dispose();
                Loader.CloseTarget();
            }
        }

        private void OnAppMessage(Envelope e)
        {
            if (e.Kind != MessageKind.MotionControl || e.BodyCount < 4) return;
            _reader.Reset(e.Body, e.BodyOffset, e.BodyCount);
            _controlSeq = _reader.ReadUInt32();
            _hasControl = true;
        }

        private void OnLive()
        {
            _log.Info(LogCategory.Session, "test recorder is live as " + Client.LocalPeer + "; recording");
            if (!_options.RequestIconFullSync) return;
            var w = new SyncBatch.Writer();
            w.RequestIds(SyncRequest.IconFullSync, Array.Empty<uint>());
            ArraySegment<byte> bytes = w.Finish();
            Client.SendToHost(MessageKind.Proposal, bytes.Array, bytes.Offset, bytes.Count);
        }

        private static uint SafeFrame(Func<uint> frame)
        {
            try
            {
                return frame != null ? frame() : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// The recorder's world loader: the snapshot goes into a file of the recording (or wherever the factory says), and
    /// the "load" succeeds at once; <c>onLoaded</c> runs right before the client reports it (the barrier entry).
    /// </summary>
    public sealed class RecorderLoader : IWorldLoader
    {
        private readonly Func<Stream> _open;
        private readonly Action _onLoaded;
        private Stream _target;

        public RecorderLoader(Func<Stream> openTarget, Action onLoaded)
        {
            _open = openTarget;
            _onLoaded = onLoaded;
        }

        public bool IsLoading => false;

        /// <summary>Bytes of the snapshot written so far.</summary>
        public long SnapshotBytes { get; private set; }

        public bool Loaded { get; private set; }

        public Stream OpenBlobTarget(SnapshotManifest manifest)
        {
            CloseTarget();
            _target = (_open != null ? _open() : null) ?? new MemoryStream();
            return new CountingStream(_target, n => SnapshotBytes += n);
        }

        public void BeginLoad(SnapshotManifest manifest, IReadOnlyList<SettingEntry> pushedSettings, Action<LoadOutcome> onDone)
        {
            CloseTarget();
            Loaded = true;
            _onLoaded?.Invoke();
            onDone(new LoadOutcome { Success = true, SlotCoverage = 1f });
        }

        public void CloseTarget()
        {
            try
            {
                _target?.Dispose();
            }
            catch (IOException)
            {
                // best effort
            }
            _target = null;
        }

        /// <summary>Counts what is written; reads and seeks go through (the receiver may verify the digest).</summary>
        private sealed class CountingStream : Stream
        {
            private readonly Stream _inner;
            private readonly Action<long> _written;

            public CountingStream(Stream inner, Action<long> written)
            {
                _inner = inner;
                _written = written;
            }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => _inner.CanSeek;
            public override bool CanWrite => _inner.CanWrite;
            public override long Length => _inner.Length;

            public override long Position
            {
                get => _inner.Position;
                set => _inner.Position = value;
            }

            public override void Flush() => _inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
            public override void SetLength(long value) => _inner.SetLength(value);

            public override void Write(byte[] buffer, int offset, int count)
            {
                _inner.Write(buffer, offset, count);
                _written(count);
            }

            protected override void Dispose(bool disposing)
            {
                // the loader owns the inner stream (closed before the load is reported)
                if (disposing) _inner.Flush();
                base.Dispose(disposing);
            }
        }
    }
}
