using System;
using System.Collections.Generic;
using System.IO;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Session
{
    public sealed class ClientOptions
    {
        public string PlayerName = "Player";
        public ulong SteamId;
        public string Password = "";
        public double ConnectTimeoutSeconds = 30;
    }

    public enum ClientState
    {
        Idle,
        Connecting,
        Handshaking,
        WaitingForSnapshot,
        Downloading,
        Loading,
        Live,
        Ended
    }

    /// <summary>
    /// The client side of a session (main thread): connects, proves its versions, mods and password, downloads the
    /// host's city snapshot, has the game load it, then follows the host. Replication messages that arrive before the
    /// load finished are buffered and delivered in order once the client is live. Call <see cref="Tick"/> once per frame.
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public sealed class SessionClient : ITransportEventSink, IDisposable
    {
        private struct Buffered
        {
            public MessageKind Kind;
            public byte[] Body;
        }

        private readonly ITransport _transport;
        private readonly IGameInfoProvider _info;
        private readonly IWorldLoader _loader;
        private readonly ClientOptions _options;
        private readonly IClock _clock;
        private readonly ILog _log;
        private readonly ByteWriter _body = new ByteWriter(4096);
        private readonly ByteWriter _payload = new ByteWriter(4096);
        private readonly ByteReader _reader = new ByteReader();
        private readonly Queue<Buffered> _buffered = new Queue<Buffered>();
        private readonly byte[] _clientNonce = SessionCrypto.RandomBytes(16);
        private IConnection _connection;
        private PeerChannel _channel;
        private double _stateSince;
        private BlobReceiver _receiver;
        private Stream _target;
        private SnapshotManifest _manifest;
        private List<PlayerInfo> _players = new List<PlayerInfo>();
        private double _lastProgressSent;
        private double _lastHaveSent;
        private float _loadProgress;
        private bool _disposed;
        private Action<Envelope> _appMessage;

        public SessionClient(ITransport transport, IGameInfoProvider info, IWorldLoader loader, ClientOptions options, IClock clock, ILog log)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _info = info ?? throw new ArgumentNullException(nameof(info));
            _loader = loader ?? throw new ArgumentNullException(nameof(loader));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public ClientState State { get; private set; }

        /// <summary>Why the session ended (shown to the player), once <see cref="State"/> is <see cref="ClientState.Ended"/>.</summary>
        public string EndReason { get; private set; }

        public PeerId LocalPeer { get; private set; }

        public SessionInfo Session { get; private set; }

        public SpeedState Speed { get; private set; } = new SpeedState { Speed = 1, Paused = true, Held = true };

        public IReadOnlyList<PlayerInfo> Players => _players;

        /// <summary>Join progress 0..1 (download, then load).</summary>
        public float JoinProgress
        {
            get
            {
                switch (State)
                {
                    case ClientState.Downloading: return _receiver != null ? 0.5f * _receiver.Fraction : 0f;
                    case ClientState.Loading: return 0.5f + 0.5f * _loadProgress;
                    case ClientState.Live: return 1f;
                    default: return 0f;
                }
            }
        }

        public ConnectionStats Stats => _connection != null ? _connection.GetStats() : default(ConnectionStats);

        public event Action StateChanged;
        public event Action PlayersChanged;
        public event Action<ChatEntry> ChatReceived;
        public event Action<string> ToastReceived;
        public event Action<SpeedState> SpeedChanged;

        /// <summary>Replication-layer messages from the host, in order, only while live (earlier ones are buffered).</summary>
        public event Action<Envelope> AppMessage
        {
            add => _appMessage += value;
            remove => _appMessage -= value;
        }

        /// <summary>The client is live: the city is loaded and buffered messages are about to be delivered.</summary>
        public event Action BecameLive;

        public void Connect(Endpoint endpoint)
        {
            if (State != ClientState.Idle) throw new InvalidOperationException("already connecting");
            SetState(ClientState.Connecting);
            _log.Info(LogCategory.Session, "connecting to " + endpoint);
            _connection = _transport.Connect(endpoint);
            if (_connection == null)
            {
                End("Could not start the connection to " + endpoint + ".");
            }
        }

        /// <summary>The game layer reports load progress (0..1) while the snapshot loads.</summary>
        public void ReportLoadProgress(float progress)
        {
            _loadProgress = Math.Max(0f, Math.Min(1f, progress));
        }

        public void Tick()
        {
            if (_disposed) return;
            _transport.Poll(this);
            if (State == ClientState.Ended || _disposed) return;
            double now = _clock.Seconds;
            if ((State == ClientState.Connecting || State == ClientState.Handshaking) && now - _stateSince > _options.ConnectTimeoutSeconds)
            {
                End(State == ClientState.Connecting ? "The host did not answer. Check the address or Steam ID and that the host is hosting." : "The host did not finish the handshake.");
                return;
            }
            if (State == ClientState.Downloading && _receiver != null && _receiver.ResendRequested && now - _lastHaveSent > 0.5)
            {
                SendHave();
            }
            if ((State == ClientState.Downloading || State == ClientState.Loading) && now - _lastProgressSent > 0.5)
            {
                _lastProgressSent = now;
                _payload.Reset();
                _payload.WriteSingle(JoinProgress);
                SendControl(ControlKind.LoadProgress, _payload);
            }
            _channel?.Pump();
        }

        public void SendChat(string text)
        {
            if (State != ClientState.Live || _channel == null) return;
            text = ProtocolText.Truncate(ProtocolText.Sanitize(text ?? ""), Limits.MaxChatChars).Trim();
            if (text.Length == 0) return;
            _body.Reset();
            ChatMessage.Write(_body, new ChatMessage { From = LocalPeer, Text = text });
            _channel.Send(MessageKind.Chat, _body);
        }

        /// <summary>Asks the host for another game speed; the host's answer arrives as <see cref="SpeedChanged"/>.</summary>
        public void RequestSpeed(SpeedState wanted)
        {
            if (State != ClientState.Live) return;
            _payload.Reset();
            SessionPayloads.WriteSpeed(_payload, wanted);
            SendControl(ControlKind.SpeedRequest, _payload);
        }

        /// <summary>Sends a replication message (an intent) to the host.</summary>
        public bool SendToHost(MessageKind kind, byte[] body, int offset, int count)
        {
            if (State != ClientState.Live || _channel == null) return false;
            return _channel.Send(kind, body, offset, count);
        }

        /// <summary>Leaves the session politely.</summary>
        public void Leave()
        {
            if (_channel != null && State != ClientState.Ended)
            {
                _payload.Reset();
                SendControl(ControlKind.Disconnecting, _payload);
            }
            End("You left the session.");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CloseTarget();
            _channel?.Dispose();
            _transport.Dispose();
        }

        // ---------------------------------------------------------------- transport events

        void ITransportEventSink.OnConnected(IConnection connection)
        {
            if (connection != _connection && _connection != null) return;
            _connection = connection;
            _channel = new PeerChannel(connection, PeerId.Host, _clock, _log);
            SetState(ClientState.Handshaking);
            HandshakeDigests digests = _info.GetDigests();
            _body.Reset();
            HelloMessage.Write(_body, new HelloMessage
            {
                Digests = digests,
                SteamId = _options.SteamId,
                DisplayName = _options.PlayerName,
                ClientNonce = _clientNonce,
                CompressionMask = CompressionNegotiation.AlwaysSupported,
            });
            _channel.Send(MessageKind.Hello, _body);
            HandshakeManifest manifest = _info.BuildManifest(false);
            _payload.Reset();
            ManifestDetailMessage.Write(_payload, new ManifestDetailMessage { Manifest = manifest });
            SendControl(ControlKind.ManifestDetail, _payload);
            _channel.Pump();
            _log.Info(LogCategory.Session, "connected; sent hello (" + manifest.Mods.Count + " mods)");
        }

        void ITransportEventSink.OnDisconnected(IConnection connection, int reason, string text)
        {
            if (connection != _connection || State == ClientState.Ended) return;
            End(State == ClientState.Connecting
                ? "Could not connect to the host (" + (string.IsNullOrEmpty(text) ? reason.ToString() : text) + ")."
                : "Lost the connection to the host (" + (string.IsNullOrEmpty(text) ? reason.ToString() : text) + ").");
        }

        void ITransportEventSink.OnMessage(IConnection connection, Lane lane, byte[] buffer, int offset, int count)
        {
            if (connection != _connection || _channel == null || State == ClientState.Ended) return;
            try
            {
                if (!_channel.TryDecode(buffer, offset, count, out Envelope e)) return;
                Handle(e);
            }
            catch (DecodeException ex)
            {
                _log.Warn(LogCategory.Protocol, "malformed message from the host: " + ex.Message);
                End("The host sent data this version cannot read (" + ex.Message + ").");
            }
            catch (Exception ex)
            {
                _log.Error(LogCategory.Session, "message handling failed", ex);
            }
        }

        // ---------------------------------------------------------------- messages

        private void Handle(Envelope e)
        {
            switch (e.Kind)
            {
                case MessageKind.Refuse:
                {
                    RefuseMessage r = RefuseMessage.Decode(e.Body, e.BodyOffset, e.BodyCount);
                    End(string.IsNullOrEmpty(r.Detail) ? "The host refused the connection (" + r.Reason + ")." : r.Detail);
                    return;
                }
                case MessageKind.Welcome:
                {
                    WelcomeMessage w = WelcomeMessage.Decode(e.Body, e.BodyOffset, e.BodyCount);
                    LocalPeer = w.AssignedPeer;
                    byte[] settings = w.SessionSettings ?? Array.Empty<byte>();
                    _reader.Reset(settings, 0, settings.Length);
                    Session = settings.Length > 0 ? SessionPayloads.ReadSessionInfo(_reader) : new SessionInfo();
                    _log.Info(LogCategory.Session, "welcomed to \"" + Session.SessionName + "\" as " + LocalPeer);
                    SetState(ClientState.WaitingForSnapshot);
                    return;
                }
                case MessageKind.Control:
                    HandleControl(ControlMessage.Decode(e.Body, e.BodyOffset, e.BodyCount));
                    return;
                case MessageKind.BlobChunk:
                    if (_receiver == null || State != ClientState.Downloading) return;
                    _receiver.OnChunk(e.Body, e.BodyOffset, e.BodyCount);
                    if (_receiver.IsFailed)
                    {
                        End("The city download failed: " + _receiver.FailureReason);
                        return;
                    }
                    if (_receiver.IsComplete) OnDownloadComplete();
                    return;
                case MessageKind.Ping:
                {
                    PingMessage ping = PingMessage.Decode(e.Body, e.BodyOffset, e.BodyCount);
                    if (ping.IsReply) return;
                    ping.IsReply = true;
                    _body.Reset();
                    PingMessage.Write(_body, ping);
                    _channel.Send(MessageKind.Ping, _body);
                    return;
                }
                case MessageKind.Chat:
                {
                    ChatMessage chat = ChatMessage.Decode(e.Body, e.BodyOffset, e.BodyCount);
                    ChatReceived?.Invoke(new ChatEntry { From = chat.From, Name = chat.IsSystem ? "" : NameOf(chat.From), Text = chat.Text, IsSystem = chat.IsSystem });
                    return;
                }
                default:
                    if (State == ClientState.Live && _buffered.Count == 0)
                    {
                        _appMessage?.Invoke(e);
                    }
                    else if (State != ClientState.Ended)
                    {
                        var copy = new byte[e.BodyCount];
                        Buffer.BlockCopy(e.Body, e.BodyOffset, copy, 0, e.BodyCount);
                        _buffered.Enqueue(new Buffered { Kind = e.Kind, Body = copy });
                    }
                    return;
            }
        }

        private void HandleControl(ControlMessage c)
        {
            byte[] payload = c.Payload ?? Array.Empty<byte>();
            _reader.Reset(payload, 0, payload.Length);
            switch (c.Kind)
            {
                case ControlKind.PasswordChallenge:
                {
                    PasswordChallengeMessage ch = PasswordChallengeMessage.Decode(payload, 0, payload.Length);
                    if (string.IsNullOrEmpty(_options.Password))
                    {
                        End("This session needs a password. Enter it in the Join settings and try again.");
                        return;
                    }
                    _body.Reset();
                    PasswordResponseMessage.Write(_body, new PasswordResponseMessage
                    {
                        ClientNonce = _clientNonce,
                        Mac = SessionCrypto.PasswordMac(_options.Password, ch.HostNonce, _clientNonce, ch.Salt, ch.SessionId),
                    });
                    var wrapped = new ByteWriter(_body.Length + 8);
                    ControlMessage.Write(wrapped, ControlKind.PasswordResponse, _body.Buffer, 0, _body.Length);
                    _channel.Send(MessageKind.Control, wrapped);
                    return;
                }
                case ControlKind.Players:
                    _players = SessionPayloads.ReadPlayers(_reader);
                    PlayersChanged?.Invoke();
                    return;
                case ControlKind.SpeedChanged:
                    Speed = SessionPayloads.ReadSpeed(_reader);
                    SpeedChanged?.Invoke(Speed);
                    return;
                case ControlKind.Toast:
                    ToastReceived?.Invoke(SessionPayloads.ReadText(_reader));
                    return;
                case ControlKind.SnapshotManifest:
                    BeginDownload(SnapshotManifestCodec.Decode(payload, 0, payload.Length));
                    return;
                case ControlKind.Live:
                    GoLive();
                    return;
                case ControlKind.Kick:
                    End("The host removed you from the session: " + SessionPayloads.ReadText(_reader));
                    return;
                case ControlKind.SessionEnd:
                    End(SessionPayloads.ReadText(_reader));
                    return;
                default:
                    return;
            }
        }

        // ---------------------------------------------------------------- join

        private void BeginDownload(SnapshotManifest manifest)
        {
            if (State != ClientState.WaitingForSnapshot) return;
            _manifest = manifest;
            try
            {
                _target = _loader.OpenBlobTarget(manifest);
                _receiver = new BlobReceiver(new BlobManifestInfo
                {
                    BlobId = manifest.SnapshotId,
                    Size = manifest.Size,
                    ChunkSize = manifest.ChunkSize,
                    Digest = new Hashing.Digest128(manifest.DigestLo, manifest.DigestHi),
                    Purpose = "snapshot",
                }, _target, _log);
            }
            catch (Exception ex)
            {
                _log.Error(LogCategory.Join, "could not prepare the download", ex);
                End("Could not prepare the city download: " + ex.Message);
                return;
            }
            SetState(ClientState.Downloading);
            _payload.Reset();
            SendControl(ControlKind.ReadyForSnapshot, _payload);
            _log.Info(LogCategory.Join, "downloading the city (" + manifest.Size + " bytes)");
            if (_receiver.IsComplete) OnDownloadComplete();
        }

        private void OnDownloadComplete()
        {
            SendHave();
            if (!_receiver.VerifyDigest())
            {
                End("The downloaded city is damaged (checksum mismatch). Try joining again.");
                return;
            }
            CloseTarget();
            SetState(ClientState.Loading);
            _log.Info(LogCategory.Join, "download complete; loading the city");
            try
            {
                _loader.BeginLoad(_manifest, Array.Empty<SettingEntry>(), OnLoaded);
            }
            catch (Exception ex)
            {
                _log.Error(LogCategory.Join, "starting the load failed", ex);
                OnLoaded(new LoadOutcome { Success = false, FailureDetail = ex.Message });
            }
        }

        private void OnLoaded(LoadOutcome outcome)
        {
            if (State != ClientState.Loading) return;
            _payload.Reset();
            _payload.WriteBool(outcome.Success);
            SessionPayloads.WriteText(_payload, outcome.Success ? "" : (outcome.FailureDetail ?? outcome.FailureKey ?? "unknown error"));
            SendControl(ControlKind.LoadDone, _payload);
            _channel?.Pump();
            if (!outcome.Success)
            {
                End("Loading the host's city failed: " + (outcome.FailureDetail ?? outcome.FailureKey ?? "unknown error"));
            }
            else
            {
                _loadProgress = 1f;
                _log.Info(LogCategory.Join, "city loaded; waiting for the host");
            }
        }

        private void GoLive()
        {
            if (State != ClientState.Loading) return;
            SetState(ClientState.Live);
            BecameLive?.Invoke();
            while (_buffered.Count > 0 && State == ClientState.Live)
            {
                Buffered b = _buffered.Dequeue();
                _appMessage?.Invoke(new Envelope { Kind = b.Kind, Body = b.Body, BodyOffset = 0, BodyCount = b.Body.Length });
            }
        }

        private void SendHave()
        {
            if (_receiver == null || _channel == null) return;
            _lastHaveSent = _clock.Seconds;
            byte[] bitmap = _receiver.BuildHaveBitmap();
            _body.Reset();
            BlobCodec.WriteHave(_body, _receiver.Manifest.BlobId, bitmap, 0, bitmap.Length);
            _channel.Send(MessageKind.BlobHave, _body);
        }

        // ---------------------------------------------------------------- helpers

        private string NameOf(PeerId id)
        {
            foreach (PlayerInfo p in _players) if (p.Id == id) return p.Name;
            return id.IsHost ? Session?.HostName ?? "Host" : "Player " + id.Value;
        }

        private void SendControl(ControlKind kind, ByteWriter payload)
        {
            if (_channel == null) return;
            _body.Reset();
            ControlMessage.Write(_body, kind, payload.Buffer, 0, payload.Length);
            _channel.Send(MessageKind.Control, _body);
        }

        private void CloseTarget()
        {
            try
            {
                _target?.Dispose();
            }
            catch (Exception ex)
            {
                _log.Warn(LogCategory.Join, "closing the download target failed", ex);
            }
            _target = null;
        }

        private void SetState(ClientState state)
        {
            if (State == state) return;
            State = state;
            _stateSince = _clock.Seconds;
            StateChanged?.Invoke();
        }

        private void End(string reason)
        {
            if (State == ClientState.Ended) return;
            EndReason = reason;
            _log.Info(LogCategory.Session, "session ended: " + reason);
            CloseTarget();
            _buffered.Clear();
            if (_channel != null && _connection != null)
            {
                try
                {
                    _channel.Close(0, "ended");
                }
                catch (Exception)
                {
                    // already closed
                }
            }
            SetState(ClientState.Ended);
        }
    }
}
