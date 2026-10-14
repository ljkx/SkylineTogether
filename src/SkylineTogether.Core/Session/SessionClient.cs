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
        /// <summary>Longest wait for the host's snapshot after the welcome (the host saves the city first).</summary>
        public double SnapshotWaitTimeoutSeconds = 300;
        /// <summary>The download is abandoned when no chunk arrived for this long.</summary>
        public double DownloadStallTimeoutSeconds = 120;
        /// <summary>Longest wait for the host's "live" after the city loaded.</summary>
        public double LiveWaitTimeoutSeconds = 120;
        /// <summary>
        /// w8/robust: a live session in which nothing at all arrives from the host for this long ends (the host pings every
        /// second): its game froze or is gone while the connection itself stays up. The player is offered a rejoin.
        /// </summary>
        public double LiveSilenceTimeoutSeconds = 120;
        /// <summary>w8/robust: the longest the local load of the host's city may take before the join is abandoned.</summary>
        public double LoadTimeoutSeconds = 1200;
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

    /// <summary>Why a client session ended, for the joiner's error screen (the text is in <see cref="SessionClient.EndReason"/>).</summary>
    public enum ClientEndKind
    {
        None,
        /// <summary>The player left.</summary>
        Left,
        /// <summary>The host refused the join (<see cref="SessionClient.RefusedFor"/>, maybe <see cref="SessionClient.EndReport"/>).</summary>
        Refused,
        /// <summary>The session has a password and none was given.</summary>
        PasswordRequired,
        Kicked,
        /// <summary>The host ended the session.</summary>
        SessionEnded,
        /// <summary>A watchdog fired (no answer, no city, stalled download, no confirmation).</summary>
        Timeout,
        /// <summary>The connection failed or was lost.</summary>
        ConnectionLost,
        /// <summary>Anything else.</summary>
        Failed,

        // robust branch: finer kinds the rejoin decision needs (RejoinController, SessionClient.IsRetryable)
        /// <summary>The host could not be reached while connecting. Retryable.</summary>
        Unreachable,
        /// <summary>The join failed in a way a new attempt can fix (damaged download, busy loader). Retryable.</summary>
        JoinFailed,
        /// <summary>The host refused, but only for now (an earlier attempt of ours is still registered). Retryable.</summary>
        RefusedTemporarily,
        /// <summary>This game could not load the host's city.</summary>
        LoadFailed,
        /// <summary>The host sent data this version cannot read.</summary>
        ProtocolError,
        /// <summary>This game hit an error that may have left the replica different from the host's city; a rejoin with
        /// a fresh snapshot repairs it (offered, not automatic: a deterministic fault would loop).</summary>
        LocalFault
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
        private double _lastChunkAt;
        private double _loadedAt = -1;
        // w8/robust: when anything last arrived from the host (live silence watchdog)
        private double _lastHeard;
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

        /// <summary>The category of <see cref="EndReason"/>.</summary>
        public ClientEndKind EndKind { get; private set; }

        /// <summary>The host's refusal reason when <see cref="EndKind"/> is <see cref="ClientEndKind.Refused"/>.</summary>
        public RefuseReason RefusedFor { get; private set; }

        /// <summary>The structured compatibility report of a refusal (mods to enable, disable, update), or null.</summary>
        public MismatchReport EndReport { get; private set; }

        /// <summary>Bytes of the city snapshot received so far (while downloading).</summary>
        public long DownloadedBytes => _receiver != null ? _receiver.BytesReceived : 0;

        /// <summary>Size of the city snapshot in bytes, 0 before the download started.</summary>
        public long DownloadTotalBytes => _manifest != null ? _manifest.Size : 0;

        /// <summary>
        /// True once this client started loading the host's city: from then on the local world is (or is becoming) the
        /// host's city, so an unexpected end leaves the player in a copy of it rather than where they came from.
        /// </summary>
        public bool EnteredCity { get; private set; }

        /// <summary>True once this client was live.</summary>
        public bool WasLive { get; private set; }

        /// <summary>True for end kinds an automatic rejoin can fix.</summary>
        public static bool IsRetryable(ClientEndKind kind)
        {
            return kind == ClientEndKind.Unreachable || kind == ClientEndKind.ConnectionLost || kind == ClientEndKind.Timeout || kind == ClientEndKind.JoinFailed || kind == ClientEndKind.RefusedTemporarily;
        }

        public PeerId LocalPeer { get; private set; }

        /// <summary>gameplay branch: this player's role (from the welcome, then from every player list the host sends).</summary>
        public SessionRole LocalRole { get; private set; } = SessionRole.Builder;

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

        /// <summary>
        /// loadspeed: the stage timings of this join (connect, welcome, download, verify, load, live, catch-up) for the
        /// log, the diagnostics dump and the UI. The session marks every stage up to live; the replication layer reports
        /// the backlog it still has to apply (<see cref="ReportBacklog"/>), which marks the catch-up.
        /// </summary>
        public JoinTimeline Timeline { get; } = new JoinTimeline { Who = "this player" };

        /// <summary>
        /// loadspeed: the replication layer's backlog after going live (batches still waiting to be applied). Zero
        /// once live ends the "catching up" state and logs the join's timings.
        /// </summary>
        public void ReportBacklog(int remaining)
        {
            Timeline.BacklogRemaining = Math.Max(0, remaining);
            if (State != ClientState.Live || Timeline.Has(JoinStage.CaughtUp) || remaining > 0) return;
            Timeline.Mark(JoinStage.CaughtUp, _clock.Seconds);
            _log.Info(LogCategory.Join, Timeline.Describe());
        }

        public event Action StateChanged;
        public event Action PlayersChanged;
        public event Action<ChatEntry> ChatReceived;
        public event Action<string> ToastReceived;
        public event Action<SpeedState> SpeedChanged;

        /// <summary>Replication-layer messages from the host, in order, only while live (earlier ones are buffered).</summary>
        /// <summary>
        /// w2/ids: sequence numbers of the host's <c>Epoch</c> messages in dispatch order. A handler of <see cref="AppMessage"/>
        /// that applies an <c>Epoch</c> message later holds <see cref="Sync.ApplyAckTracker.Received"/> and releases it once
        /// applied; the replication layer acknowledges <see cref="Sync.ApplyAckTracker.AppliedThrough"/> to the host.
        /// </summary>
        public Sync.ApplyAckTracker ApplyAcks { get; } = new Sync.ApplyAckTracker();

        public event Action<Envelope> AppMessage
        {
            add => _appMessage += value;
            remove => _appMessage -= value;
        }

        /// <summary>The client is live: the city is loaded and buffered messages are about to be delivered.</summary>
        public event Action BecameLive;

        /// <summary>
        /// Another player's presence (relayed by the host, which set <see cref="PresenceMessage.From"/>), only while
        /// live; never the local player's own. The instance is new for every call.
        /// </summary>
        public event Action<PresenceMessage> PresenceReceived;

        /// <summary>Link numbers of the connection to the host.</summary>
        public SessionNetStats GetNetStats()
        {
            if (_connection == null || _channel == null || State == ClientState.Ended) return new SessionNetStats { PingMs = -1 };
            ConnectionStats c = _connection.GetStats();
            return new SessionNetStats
            {
                PingMs = c.PingMs,
                InBytesPerSecond = Math.Max(0f, c.InBytesPerSecond),
                OutBytesPerSecond = Math.Max(0f, c.OutBytesPerSecond),
                QueuedBytes = _channel.Outbox.QueuedBytes,
                Connections = 1,
            };
        }

        /// <summary>Sends the local player's presence to the host (unreliable, live only); false when not sent.</summary>
        public bool SendPresence(PresenceMessage presence)
        {
            if (presence == null || State != ClientState.Live || _channel == null) return false;
            presence.From = LocalPeer;
            _body.Reset();
            PresenceMessage.Write(_body, presence);
            _channel.SendUnreliable(MessageKind.Presence, _body);
            return true;
        }

        public void Connect(Endpoint endpoint)
        {
            if (State != ClientState.Idle) throw new InvalidOperationException("already connecting");
            SetState(ClientState.Connecting);
            Timeline.Mark(JoinStage.Start, _clock.Seconds);
            _log.Info(LogCategory.Session, "connecting to " + endpoint);
            _connection = _transport.Connect(endpoint);
            if (_connection == null)
            {
                End("Could not start the connection to " + endpoint + ".", ClientEndKind.Unreachable);
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
                End(State == ClientState.Connecting ? "The host did not answer. Check the address or Steam ID and that the host is hosting." : "The host did not finish the handshake.", ClientEndKind.Timeout);
                return;
            }
            // join watchdogs: a host that never sends the city (or never answers the load) must not leave the player
            // waiting forever; the load itself is local and has no time limit
            if (State == ClientState.WaitingForSnapshot && now - _stateSince > _options.SnapshotWaitTimeoutSeconds)
            {
                End("The host did not send the city in time. Try joining again.", ClientEndKind.Timeout);
                return;
            }
            if (State == ClientState.Downloading && now - _lastChunkAt > _options.DownloadStallTimeoutSeconds)
            {
                End("The city download stalled (nothing received for " + (int)_options.DownloadStallTimeoutSeconds + " s). Try joining again.", ClientEndKind.Timeout);
                return;
            }
            if (State == ClientState.Loading && _loadedAt < 0 && _options.LoadTimeoutSeconds > 0 && now - _stateSince > _options.LoadTimeoutSeconds)
            {
                End("Loading the host's city did not finish in " + (int)(_options.LoadTimeoutSeconds / 60) + " minutes. Try joining again.", ClientEndKind.Timeout);
                return;
            }
            if (State == ClientState.Live && _options.LiveSilenceTimeoutSeconds > 0 && now - _lastHeard > _options.LiveSilenceTimeoutSeconds)
            {
                // w8/robust: the transport polled first, so anything that was waiting for this side counted already
                End("The host stopped responding (nothing received for " + (int)(now - _lastHeard) + " s). Their game may be frozen or closed.", ClientEndKind.Timeout);
                return;
            }
            if (State == ClientState.Loading && _loadedAt >= 0 && now - _loadedAt > _options.LiveWaitTimeoutSeconds)
            {
                End("The host did not confirm the join after the city loaded. Try joining again.", ClientEndKind.Timeout);
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
                if (State == ClientState.Downloading) SessionPayloads.WriteLoadProgress(_payload, JoinPhase.Downloading, _receiver != null ? _receiver.Fraction : 0f);
                else SessionPayloads.WriteLoadProgress(_payload, JoinPhase.Loading, _loadProgress);
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

        /// <summary>
        /// Ends the session because this game failed (<see cref="ClientEndKind.LocalFault"/>): tells the host it is
        /// leaving, like <see cref="Leave"/>, but the game layer offers a rejoin instead of treating it as a choice.
        /// </summary>
        public void Abort(string reason)
        {
            if (_channel != null && State != ClientState.Ended)
            {
                _payload.Reset();
                SendControl(ControlKind.Disconnecting, _payload);
            }
            End(reason ?? "This game hit an error.", ClientEndKind.LocalFault);
        }

        /// <summary>Leaves the session politely.</summary>
        public void Leave()
        {
            if (_channel != null && State != ClientState.Ended)
            {
                _payload.Reset();
                SendControl(ControlKind.Disconnecting, _payload);
            }
            End("You left the session.", ClientEndKind.Left);
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
            bool connecting = State == ClientState.Connecting;
            End(connecting
                ? "Could not connect to the host (" + (string.IsNullOrEmpty(text) ? reason.ToString() : text) + ")."
                : "Lost the connection to the host (" + (string.IsNullOrEmpty(text) ? reason.ToString() : text) + ").",
                connecting ? ClientEndKind.Unreachable : ClientEndKind.ConnectionLost);
        }

        void ITransportEventSink.OnMessage(IConnection connection, Lane lane, byte[] buffer, int offset, int count)
        {
            if (connection != _connection || _channel == null || State == ClientState.Ended) return;
            _lastHeard = _clock.Seconds;
            try
            {
                if (!_channel.TryDecode(buffer, offset, count, out Envelope e)) return;
                Handle(e);
            }
            catch (DecodeException ex)
            {
                _log.Warn(LogCategory.Protocol, "malformed message from the host: " + ex.Message);
                End("The host sent data this version cannot read (" + ex.Message + ").", ClientEndKind.ProtocolError);
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
                    RefusedFor = r.Reason;
                    EndReport = MismatchReport.Decode(r.MismatchReport);
                    End(string.IsNullOrEmpty(r.Detail) ? "The host refused the connection (" + r.Reason + ")." : r.Detail, r.Reason == RefuseReason.HostPreparing ? ClientEndKind.RefusedTemporarily : ClientEndKind.Refused);
                    return;
                }
                case MessageKind.Welcome:
                {
                    WelcomeMessage w = WelcomeMessage.Decode(e.Body, e.BodyOffset, e.BodyCount);
                    LocalPeer = w.AssignedPeer;
                    LocalRole = w.Role;
                    byte[] settings = w.SessionSettings ?? Array.Empty<byte>();
                    _reader.Reset(settings, 0, settings.Length);
                    Session = settings.Length > 0 ? SessionPayloads.ReadSessionInfo(_reader) : new SessionInfo();
                    _log.Info(LogCategory.Session, "welcomed to \"" + Session.SessionName + "\" as " + LocalPeer);
                    // the city load stalls this game's main thread (and the host's while it saves): longer keepalive
                    ConnectionTimeouts.Apply(_connection, joining: true, _log);
                    Timeline.Mark(JoinStage.Welcome, _clock.Seconds);
                    SetState(ClientState.WaitingForSnapshot);
                    return;
                }
                case MessageKind.Presence:
                {
                    // cosmetic and unreliable: never buffered, dropped until the city is ours
                    if (State != ClientState.Live) return;
                    PresenceMessage m = PresenceMessage.Decode(e.Body, e.BodyOffset, e.BodyCount);
                    if (m.From != LocalPeer) PresenceReceived?.Invoke(m);
                    return;
                }
                case MessageKind.Control:
                    HandleControl(ControlMessage.Decode(e.Body, e.BodyOffset, e.BodyCount));
                    return;
                case MessageKind.BlobChunk:
                    if (_receiver == null || State != ClientState.Downloading) return;
                    _lastChunkAt = _clock.Seconds;
                    _receiver.OnChunk(e.Body, e.BodyOffset, e.BodyCount);
                    if (_receiver.IsFailed)
                    {
                        End("The city download failed: " + _receiver.FailureReason, ClientEndKind.JoinFailed);
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
                        if (e.Kind == MessageKind.Epoch) ApplyAcks.OnReceived(); // w2/ids
                        DeliverApp(e);
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
                        End("This session needs a password. Enter it in the Join settings and try again.", ClientEndKind.PasswordRequired);
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
                    foreach (PlayerInfo p in _players)
                    {
                        if (p.Id == LocalPeer) LocalRole = p.Role;
                    }
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
                    End("The host removed you from the session: " + SessionPayloads.ReadText(_reader), ClientEndKind.Kicked);
                    return;
                case ControlKind.SessionEnd:
                    End(SessionPayloads.ReadText(_reader), ClientEndKind.SessionEnded);
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
                End("Could not prepare the city download: " + ex.Message, ClientEndKind.LoadFailed);
                return;
            }
            _lastChunkAt = _clock.Seconds;
            Timeline.SnapshotBytes = manifest.Size;
            Timeline.Mark(JoinStage.TransferStarted, _clock.Seconds);
            SetState(ClientState.Downloading);
            _payload.Reset();
            SendControl(ControlKind.ReadyForSnapshot, _payload);
            _log.Info(LogCategory.Join, "downloading the city (" + manifest.Size + " bytes)");
            if (_receiver.IsComplete) OnDownloadComplete();
        }

        private void OnDownloadComplete()
        {
            Timeline.Mark(JoinStage.Downloaded, _clock.Seconds);
            SendHave();
            // the digest was computed while the chunks arrived in order; only chunks that came ahead of a gap are read back
            if (!_receiver.VerifyDigest())
            {
                End("The downloaded city is damaged (checksum mismatch). Try joining again.", ClientEndKind.JoinFailed);
                return;
            }
            Timeline.Mark(JoinStage.Verified, _clock.Seconds);
            CloseTarget();
            EnteredCity = true;
            SetState(ClientState.Loading);
            double rate = Timeline.TransferBytesPerSecond;
            _log.Info(LogCategory.Join, "download complete (" + _manifest.Size + " bytes in "
                                        + Timeline.TransferSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " s"
                                        + (double.IsNaN(rate) ? "" : ", " + (rate / 1048576.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " MiB/s")
                                        + "); loading the city");
            Timeline.Mark(JoinStage.LoadStarted, _clock.Seconds);
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
                // "busy": this game was still loading another city (e.g. an earlier attempt's); a retry can succeed
                End("Loading the host's city failed: " + (outcome.FailureDetail ?? outcome.FailureKey ?? "unknown error"),
                    outcome.FailureKey == "busy" ? ClientEndKind.JoinFailed : ClientEndKind.LoadFailed);
            }
            else
            {
                _loadProgress = 1f;
                _loadedAt = _clock.Seconds;
                Timeline.Mark(JoinStage.Loaded, _loadedAt);
                _log.Info(LogCategory.Join, "city loaded in " + Timeline.LoadSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " s; waiting for the host");
            }
        }

        private void GoLive()
        {
            if (State != ClientState.Loading) return;
            if (_loadedAt < 0)
            {
                // only the host's answer to our LoadDone may make us live; anything else is a protocol error
                _log.Warn(LogCategory.Protocol, "the host sent \"live\" before the city finished loading; ignored");
                return;
            }
            ConnectionTimeouts.Apply(_connection, joining: false, _log);
            WasLive = true;
            Timeline.Mark(JoinStage.Live, _clock.Seconds);
            Timeline.BacklogMessages = _buffered.Count;
            long backlogBytes = 0;
            foreach (Buffered b in _buffered) backlogBytes += b.Body.Length;
            Timeline.BacklogBytes = backlogBytes;
            _log.Info(LogCategory.Join, "live after " + Timeline.TotalSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " s; "
                                        + _buffered.Count + " buffered message(s) (" + backlogBytes + " bytes) follow");
            SetState(ClientState.Live);
            // final review: one throwing listener used to stop GoLive here: the later listeners (gate, clock, movers)
            // never ran and the buffered messages stayed queued, so every later message was queued behind them and the
            // client stayed "live" without applying anything. Each listener and each delivery is isolated now.
            Action live = BecameLive;
            if (live != null)
            {
                foreach (Delegate d in live.GetInvocationList())
                {
                    try
                    {
                        ((Action)d)();
                    }
                    catch (Exception ex)
                    {
                        _log.Error(LogCategory.Session, "a \"became live\" listener failed", ex);
                    }
                }
            }
            while (_buffered.Count > 0 && State == ClientState.Live)
            {
                Buffered b = _buffered.Dequeue();
                if (b.Kind == MessageKind.Epoch) ApplyAcks.OnReceived(); // w2/ids
                DeliverApp(new Envelope { Kind = b.Kind, Body = b.Body, BodyOffset = 0, BodyCount = b.Body.Length });
            }
        }

        /// <summary>
        /// Hands an application message to every <see cref="AppMessage"/> listener; a listener that throws is logged and
        /// never keeps the message from the others (or ends the session as a protocol error).
        /// </summary>
        private void DeliverApp(Envelope e)
        {
            Action<Envelope> handlers = _appMessage;
            if (handlers == null) return;
            foreach (Delegate d in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<Envelope>)d)(e);
                }
                catch (Exception ex)
                {
                    _log.Error(LogCategory.Session, "handling a " + e.Kind + " message failed", ex);
                }
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
            if (state == ClientState.Live) _lastHeard = _clock.Seconds; // w8/robust: the silence watchdog starts at live
            if (State == state) return;
            State = state;
            _stateSince = _clock.Seconds;
            StateChanged?.Invoke();
        }

        private void End(string reason, ClientEndKind kind = ClientEndKind.Failed)
        {
            if (State == ClientState.Ended) return;
            EndReason = reason;
            EndKind = kind;
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
