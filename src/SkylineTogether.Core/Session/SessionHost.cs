using System;
using System.Collections.Generic;
using System.IO;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Transport;

namespace SkylineTogether.Core.Session
{
    /// <summary>Host settings, fixed for the session's lifetime.</summary>
    public sealed class HostOptions
    {
        public string SessionName = "Skyline Together";
        public string HostName = "Host";
        public ulong HostSteamId;
        /// <summary>Empty: no password.</summary>
        public string Password = "";
        public int MaxPlayers = 8;
        /// <summary>Refuse joiners whose mod list differs from the host's.</summary>
        public bool RequireSameMods = true;
        public ListenOptions Listen = new ListenOptions();
        public double HandshakeTimeoutSeconds = 30;
    }

    /// <summary>A chat line as the UI shows it.</summary>
    public sealed class ChatEntry
    {
        public PeerId From;
        public string Name;
        public string Text;
        public bool IsSystem;
    }

    /// <summary>
    /// The host side of a session (main thread): accepts connections, runs the handshake (versions, mods, password),
    /// sends each joiner a snapshot of the city and admits them once their load succeeded, keeps the player list, relays
    /// chat, dictates the game speed and carries the replication layer's messages (<see cref="Broadcast"/>,
    /// <see cref="AppMessage"/>). Call <see cref="Tick"/> once per frame.
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public sealed class SessionHost : ITransportEventSink, IDisposable
    {
        private sealed class Peer
        {
            public PeerId Id;
            public PeerChannel Channel;
            public PlayerState State;
            public string Name = "?";
            public ulong SteamId;
            public HelloMessage Hello;
            public HandshakeManifest Manifest;
            public bool AwaitingPassword;
            public byte[] HostNonce;
            public double Since;
            public BlobSender Sender;
            public FileStream SenderStream;
            public SnapshotReady Snapshot;
            public long SnapshotSize;
            public float LoadProgress;
            public int PingMs;
            public double LastPingSent;
            public uint PingSequence;
            public bool Admitted;
        }

        private readonly ITransport _transport;
        private readonly IGameInfoProvider _info;
        private readonly ISnapshotProvider _snapshots;
        private readonly HostOptions _options;
        private readonly IClock _clock;
        private readonly ILog _log;
        private readonly Dictionary<IConnection, Peer> _peers = new Dictionary<IConnection, Peer>();
        private readonly ByteWriter _body = new ByteWriter(4096);
        private readonly ByteWriter _payload = new ByteWriter(4096);
        private readonly ByteReader _reader = new ByteReader();
        private readonly Random _random = new Random();
        private readonly uint _sessionId;
        private readonly byte[] _salt;
        private SnapshotReady _snapshot;
        private readonly List<SnapshotReady> _retired = new List<SnapshotReady>();
        private bool _snapshotPending;
        private uint _nextBlobId = 1;
        private bool _playersDirty;
        private double _lastPlayersSent;
        private SpeedState _speed = new SpeedState { Speed = 1, Paused = true };
        private SpeedState _sentSpeed;
        private bool _disposed;
        private Action<PeerId, Envelope> _appMessage;

        public SessionHost(ITransport transport, IGameInfoProvider info, ISnapshotProvider snapshots, HostOptions options, IClock clock, ILog log)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _info = info ?? throw new ArgumentNullException(nameof(info));
            _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _sessionId = (uint)_random.Next(1, int.MaxValue);
            _salt = SessionCrypto.RandomBytes(16);
        }

        public HostOptions Options => _options;

        /// <summary>The player list changed (join, leave, state, ping).</summary>
        public event Action PlayersChanged;

        public event Action<ChatEntry> ChatReceived;

        /// <summary>Replication-layer messages from a live peer (kinds other than the session's own).</summary>
        public event Action<PeerId, Envelope> AppMessage
        {
            add => _appMessage += value;
            remove => _appMessage -= value;
        }

        /// <summary>A peer became live (its load succeeded); the replication layer starts tracking it.</summary>
        public event Action<PeerId> PeerLive;

        /// <summary>A live player asked for another game speed (the host decides whether to apply it).</summary>
        public event Action<PeerId, SpeedState> SpeedRequested;

        /// <summary>A peer left (any state).</summary>
        public event Action<PeerId> PeerLeft;

        /// <summary>A peer was admitted and will receive a snapshot; the replication layer starts queueing for it.</summary>
        public event Action<PeerId> PeerJoining;

        /// <summary>True while a joiner is between admission and the end of its load: the game holds the simulation.</summary>
        public bool HoldSimulation
        {
            get
            {
                foreach (Peer p in _peers.Values)
                {
                    if (p.Admitted && p.State != PlayerState.Live) return true;
                }
                return false;
            }
        }

        /// <summary>The speed the host's game runs at (set by the game layer each frame); sent to every peer when it changes.</summary>
        public SpeedState Speed
        {
            get => _speed;
            set => _speed = value;
        }

        public int LivePeerCount
        {
            get
            {
                int n = 0;
                foreach (Peer p in _peers.Values) if (p.State == PlayerState.Live) n++;
                return n;
            }
        }

        public void Start()
        {
            _transport.Listen(_options.Listen);
            _log.Info(LogCategory.Session, "hosting \"" + _options.SessionName + "\" (" + _transport.Kind + ", up to " + _options.MaxPlayers + " players"
                                           + (string.IsNullOrEmpty(_options.Password) ? "" : ", password") + ")");
            _playersDirty = true;
        }

        public IReadOnlyList<PlayerInfo> GetPlayers()
        {
            var list = new List<PlayerInfo>
            {
                new PlayerInfo { Id = PeerId.Host, Name = _options.HostName, SteamId = _options.HostSteamId, Role = SessionRole.Host, State = PlayerState.Live },
            };
            foreach (Peer p in _peers.Values)
            {
                if (!p.Admitted) continue;
                list.Add(new PlayerInfo { Id = p.Id, Name = p.Name, SteamId = p.SteamId, Role = SessionRole.Builder, State = p.State, PingMs = p.PingMs, Progress = Progress(p) });
            }
            list.Sort((a, b) => a.Id.Value.CompareTo(b.Id.Value));
            return list;
        }

        public void Tick()
        {
            if (_disposed) return;
            _transport.Poll(this);
            double now = _clock.Seconds;
            var drop = new List<Peer>();
            foreach (Peer p in _peers.Values)
            {
                if (!p.Admitted && now - p.Since > _options.HandshakeTimeoutSeconds)
                {
                    drop.Add(p);
                    continue;
                }
                if (p.Sender != null)
                {
                    p.Sender.Pump();
                    if (p.Sender.IsFailed)
                    {
                        _log.Warn(LogCategory.Join, p.Name + ": snapshot transfer failed: " + p.Sender.FailureReason);
                        drop.Add(p);
                        continue;
                    }
                }
                if (p.Admitted && now - p.LastPingSent > 1.0)
                {
                    p.LastPingSent = now;
                    _body.Reset();
                    PingMessage.Write(_body, new PingMessage { Sequence = ++p.PingSequence, SentSeconds = now });
                    p.Channel.Send(MessageKind.Ping, _body);
                }
                if (p.Channel.Outbox.OverLimit)
                {
                    _log.Warn(LogCategory.Net, p.Name + ": the connection is too slow to keep up; disconnecting");
                    drop.Add(p);
                    continue;
                }
                p.Channel.Pump();
            }
            foreach (Peer p in drop) Remove(p, TransportCloseReason.Timeout, p.Admitted ? "connection too slow" : "handshake timed out", true);

            if (!_speed.Equals(_sentSpeed))
            {
                _sentSpeed = _speed;
                _payload.Reset();
                SessionPayloads.WriteSpeed(_payload, _speed);
                BroadcastControl(ControlKind.SpeedChanged, _payload, live: true);
            }
            if (_playersDirty && now - _lastPlayersSent > 0.25 || now - _lastPlayersSent > 2.0)
            {
                _playersDirty = false;
                _lastPlayersSent = now;
                _payload.Reset();
                SessionPayloads.WritePlayers(_payload, GetPlayers());
                BroadcastControl(ControlKind.Players, _payload, live: false);
                PlayersChanged?.Invoke();
            }
            ReleaseSnapshotIfUnused();
        }

        /// <summary>Sends a replication message to every admitted peer (queued for joiners until they are live).</summary>
        public void Broadcast(MessageKind kind, byte[] body, int offset, int count, Lane lane = Lane.Reliable)
        {
            foreach (Peer p in _peers.Values)
            {
                if (p.Admitted) p.Channel.Send(kind, body, offset, count, lane);
            }
        }

        public void SendTo(PeerId peer, MessageKind kind, byte[] body, int offset, int count)
        {
            foreach (Peer p in _peers.Values)
            {
                if (p.Admitted && p.Id == peer) p.Channel.Send(kind, body, offset, count);
            }
        }

        /// <summary>
        /// Movers branch: the raw send side of a live peer, for engines that write complete envelopes themselves
        /// (the motion encoder, ARCH §4.9.3); null when the peer is unknown or not live.
        /// </summary>
        public IPeerOutput GetPeerOutput(PeerId peer)
        {
            foreach (Peer p in _peers.Values)
            {
                if (p.Admitted && p.Id == peer && p.State == PlayerState.Live) return p.Channel.Outbox;
            }
            return null;
        }

        public void SendChat(string text)
        {
            text = ProtocolText.Truncate(ProtocolText.Sanitize(text ?? ""), Limits.MaxChatChars).Trim();
            if (text.Length == 0) return;
            DistributeChat(new ChatEntry { From = PeerId.Host, Name = _options.HostName, Text = text });
        }

        public void SystemMessage(string text) => DistributeChat(new ChatEntry { From = PeerId.Host, Name = "", Text = text, IsSystem = true });

        /// <summary>Shows a toast on every client.</summary>
        public void Toast(string text)
        {
            _payload.Reset();
            SessionPayloads.WriteText(_payload, text);
            BroadcastControl(ControlKind.Toast, _payload, live: false);
        }

        public void Kick(PeerId id, string reason)
        {
            foreach (Peer p in new List<Peer>(_peers.Values))
            {
                if (p.Id != id || !p.Admitted) continue;
                _payload.Reset();
                SessionPayloads.WriteText(_payload, reason ?? "Kicked by the host");
                SendControl(p, ControlKind.Kick, _payload);
                Remove(p, 0, "kicked", true);
            }
        }

        /// <summary>Ends the session for everyone.</summary>
        public void Stop(string reason)
        {
            if (_disposed) return;
            _payload.Reset();
            SessionPayloads.WriteText(_payload, reason ?? "The host ended the session");
            BroadcastControl(ControlKind.SessionEnd, _payload, live: false);
            foreach (Peer p in new List<Peer>(_peers.Values)) Remove(p, 0, "session ended", true);
            Dispose();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (Peer p in _peers.Values) CloseSender(p);
            _peers.Clear();
            if (_snapshot != null) _snapshots.Release(_snapshot.Manifest.SnapshotId);
            _snapshot = null;
            foreach (SnapshotReady r in _retired) _snapshots.Release(r.Manifest.SnapshotId);
            _retired.Clear();
            _transport.Dispose();
        }

        // ---------------------------------------------------------------- transport events

        void ITransportEventSink.OnConnected(IConnection connection)
        {
            if (_disposed) return;
            var peer = new Peer
            {
                Id = PeerId.Host,
                Channel = new PeerChannel(connection, PeerId.Host, _clock, _log),
                State = PlayerState.Handshaking,
                Since = _clock.Seconds,
                Name = connection.Peer.DisplayName ?? connection.Peer.ToString(),
                SteamId = connection.Peer.SteamId,
            };
            _peers[connection] = peer;
            _log.Info(LogCategory.Session, "connection from " + connection.Peer + "; waiting for its hello");
        }

        void ITransportEventSink.OnDisconnected(IConnection connection, int reason, string text)
        {
            if (!_peers.TryGetValue(connection, out Peer p)) return;
            _log.Info(LogCategory.Session, p.Name + " disconnected (" + reason + ": " + text + ")");
            Remove(p, reason, text, false);
        }

        void ITransportEventSink.OnMessage(IConnection connection, Lane lane, byte[] buffer, int offset, int count)
        {
            if (!_peers.TryGetValue(connection, out Peer p)) return;
            try
            {
                if (!p.Channel.TryDecode(buffer, offset, count, out Envelope e)) return;
                Handle(p, e);
            }
            catch (DecodeException ex)
            {
                _log.Warn(LogCategory.Protocol, p.Name + ": malformed message, disconnecting: " + ex.Message);
                Remove(p, TransportCloseReason.ProtocolError, "malformed message", true);
            }
            catch (Exception ex)
            {
                _log.Error(LogCategory.Session, p.Name + ": message handling failed", ex);
            }
        }

        // ---------------------------------------------------------------- handshake

        private void Handle(Peer p, Envelope e)
        {
            switch (e.Kind)
            {
                case MessageKind.Hello:
                    if (p.Hello != null) return;
                    if (HelloMessage.PeekProtocolVersion(e.Body, e.BodyOffset, e.BodyCount) != ProtocolVersion.Current)
                    {
                        Refuse(p, RefuseReason.ProtocolVersion, "Skyline Together versions differ. Host: " + ProtocolVersion.Current + ". Update both games to the same mod version.");
                        return;
                    }
                    p.Hello = HelloMessage.Decode(e.Body, e.BodyOffset, e.BodyCount);
                    if (!string.IsNullOrEmpty(p.Hello.DisplayName)) p.Name = p.Hello.DisplayName;
                    if (p.Hello.SteamId != 0) p.SteamId = p.Hello.SteamId;
                    TryAdmit(p);
                    return;
                case MessageKind.Control:
                    HandleControl(p, ControlMessage.Decode(e.Body, e.BodyOffset, e.BodyCount));
                    return;
                case MessageKind.Ping:
                {
                    PingMessage ping = PingMessage.Decode(e.Body, e.BodyOffset, e.BodyCount);
                    if (ping.IsReply)
                    {
                        int ms = (int)Math.Round((_clock.Seconds - ping.SentSeconds) * 1000);
                        if (ms >= 0 && ms != p.PingMs)
                        {
                            p.PingMs = ms;
                            _playersDirty = true;
                        }
                    }
                    else
                    {
                        ping.IsReply = true;
                        _body.Reset();
                        PingMessage.Write(_body, ping);
                        p.Channel.Send(MessageKind.Ping, _body);
                    }
                    return;
                }
                case MessageKind.Chat:
                {
                    if (!p.Admitted) return;
                    ChatMessage chat = ChatMessage.Decode(e.Body, e.BodyOffset, e.BodyCount);
                    string text = (chat.Text ?? "").Trim();
                    if (text.Length > 0) DistributeChat(new ChatEntry { From = p.Id, Name = p.Name, Text = text });
                    return;
                }
                case MessageKind.BlobHave:
                {
                    if (p.Sender == null) return;
                    ArraySegment<byte> bitmap = BlobCodec.DecodeHave(e.Body, e.BodyOffset, e.BodyCount, out uint blobId);
                    if (blobId == p.Sender.Manifest.BlobId) p.Sender.OnHave(bitmap.Array, bitmap.Offset, bitmap.Count);
                    return;
                }
                default:
                    if (p.State == PlayerState.Live) _appMessage?.Invoke(p.Id, e);
                    return;
            }
        }

        private void HandleControl(Peer p, ControlMessage c)
        {
            byte[] payload = c.Payload ?? Array.Empty<byte>();
            switch (c.Kind)
            {
                case ControlKind.ManifestDetail:
                    if (p.Manifest != null) return;
                    p.Manifest = ManifestDetailMessage.Decode(payload, 0, payload.Length).Manifest ?? new HandshakeManifest();
                    TryAdmit(p);
                    return;
                case ControlKind.PasswordResponse:
                {
                    if (!p.AwaitingPassword) return;
                    PasswordResponseMessage r = PasswordResponseMessage.Decode(payload, 0, payload.Length);
                    byte[] expected = SessionCrypto.PasswordMac(_options.Password, p.HostNonce, r.ClientNonce, _salt, _sessionId);
                    if (!SessionCrypto.FixedTimeEquals(expected, r.Mac))
                    {
                        Refuse(p, RefuseReason.BadPassword, "Wrong password.");
                        return;
                    }
                    p.AwaitingPassword = false;
                    Admit(p);
                    return;
                }
                case ControlKind.ReadyForSnapshot:
                    // the client created its receiver; nothing else to do, the sender is already pumping
                    return;
                case ControlKind.LoadDone:
                {
                    if (p.State != PlayerState.Loading && p.State != PlayerState.Downloading) return;
                    _reader.Reset(payload, 0, payload.Length);
                    bool ok = _reader.ReadBool();
                    string detail = SessionPayloads.ReadText(_reader);
                    if (!ok)
                    {
                        _log.Warn(LogCategory.Join, p.Name + ": loading the city failed: " + detail);
                        SystemMessage(p.Name + " could not load the city: " + detail);
                        Remove(p, 0, "load failed", true);
                        return;
                    }
                    CloseSender(p);
                    p.State = PlayerState.Live;
                    _playersDirty = true;
                    _log.Info(LogCategory.Join, p.Name + " is live");
                    _payload.Reset();
                    SessionPayloads.WriteSpeed(_payload, _speed);
                    SendControl(p, ControlKind.SpeedChanged, _payload);
                    _payload.Reset();
                    SendControl(p, ControlKind.Live, _payload);
                    SystemMessage(p.Name + " joined the city");
                    PeerLive?.Invoke(p.Id);
                    return;
                }
                case ControlKind.LoadProgress:
                {
                    if (payload.Length < 4 || (p.State != PlayerState.Downloading && p.State != PlayerState.Loading)) return;
                    _reader.Reset(payload, 0, payload.Length);
                    float value = _reader.ReadSingle();
                    p.LoadProgress = float.IsNaN(value) ? 0f : Math.Max(0f, Math.Min(1f, value));
                    p.State = PlayerState.Loading;
                    _playersDirty = true;
                    return;
                }
                case ControlKind.Disconnecting:
                    Remove(p, 0, "left", false);
                    return;
                case ControlKind.SpeedRequest:
                    if (p.State != PlayerState.Live) return;
                    _reader.Reset(payload, 0, payload.Length);
                    SpeedRequested?.Invoke(p.Id, SessionPayloads.ReadSpeed(_reader));
                    return;
                default:
                    return;
            }
        }

        private void TryAdmit(Peer p)
        {
            if (p.Hello == null || p.Manifest == null || p.Admitted || p.AwaitingPassword) return;
            HandshakeDigests mine = _info.GetDigests();
            HandshakeDigests theirs = p.Hello.Digests;
            if (theirs.ModBuild != mine.ModBuild)
            {
                Refuse(p, RefuseReason.Mismatch, "Skyline Together builds differ. Both players need exactly the same mod version.");
                return;
            }
            if (!string.Equals(theirs.GameVersion, mine.GameVersion, StringComparison.Ordinal))
            {
                Refuse(p, RefuseReason.Mismatch, "Game versions differ (host " + mine.GameVersion + ", you " + theirs.GameVersion + "). Update both games.");
                return;
            }
            if (_options.RequireSameMods)
            {
                string diff = SessionPayloads.CompareMods(_info.BuildManifest(false).Mods, p.Manifest.Mods);
                if (diff != null)
                {
                    Refuse(p, RefuseReason.Mismatch, "Your mods differ from the host's. " + diff);
                    return;
                }
            }
            int admitted = 0;
            foreach (Peer o in _peers.Values) if (o.Admitted) admitted++;
            if (admitted + 1 >= _options.MaxPlayers)
            {
                Refuse(p, RefuseReason.Full, "The session is full.");
                return;
            }
            if (!string.IsNullOrEmpty(_options.Password))
            {
                p.AwaitingPassword = true;
                p.HostNonce = SessionCrypto.RandomBytes(16);
                _payload.Reset();
                PasswordChallengeMessage.Write(_payload, new PasswordChallengeMessage { HostNonce = p.HostNonce, Salt = _salt, SessionId = _sessionId });
                SendControl(p, ControlKind.PasswordChallenge, _payload);
                return;
            }
            Admit(p);
        }

        private void Admit(Peer p)
        {
            p.Id = AllocatePeerId();
            if (p.Id == PeerId.Host)
            {
                Refuse(p, RefuseReason.Full, "The session is full.");
                return;
            }
            p.Admitted = true;
            p.State = PlayerState.WaitingForSnapshot;
            _payload.Reset();
            SessionPayloads.WriteSessionInfo(_payload, new SessionInfo { SessionName = _options.SessionName, HostName = _options.HostName });
            _body.Reset();
            WelcomeMessage.Write(_body, new WelcomeMessage
            {
                SessionId = _sessionId,
                AssignedPeer = p.Id,
                Role = SessionRole.Builder,
                Permissions = Permission.All,
                SessionSettings = _payload.ToArray(),
                HostNonce = new byte[0],
                EnabledSysEntries = new uint[0],
                Compression = CompressionKind.ManagedLz4,
            });
            p.Channel.Send(MessageKind.Welcome, _body);
            _playersDirty = true;
            _log.Info(LogCategory.Session, "admitted " + p.Name + " as " + p.Id);
            SystemMessage(p.Name + " is joining…");
            PeerJoining?.Invoke(p.Id);
            if (_snapshot != null && !_snapshotPending)
            {
                // the replication stream for this peer starts now, so it needs a snapshot taken from now on
                _retired.Add(_snapshot);
                _snapshot = null;
            }
            RequestSnapshot();
        }

        private PeerId AllocatePeerId()
        {
            var used = new HashSet<byte>();
            foreach (Peer o in _peers.Values) if (o.Admitted) used.Add(o.Id.Value);
            for (int v = 1; v < 255; v++)
            {
                if (!used.Contains((byte)v)) return new PeerId((byte)v);
            }
            return PeerId.Host;
        }

        // ---------------------------------------------------------------- snapshots

        private void RequestSnapshot()
        {
            if (_snapshotPending) return;
            _snapshotPending = true;
            _log.Info(LogCategory.Join, "taking a snapshot of the city");
            _snapshots.RequestSnapshot(OnSnapshotReady, OnSnapshotFailed);
        }

        private void OnSnapshotReady(SnapshotReady ready)
        {
            _snapshotPending = false;
            if (_disposed)
            {
                _snapshots.Release(ready.Manifest.SnapshotId);
                return;
            }
            _snapshot = ready;
            _log.Info(LogCategory.Join, "snapshot ready: " + new FileInfo(ready.FilePath).Length + " bytes");
            foreach (Peer p in _peers.Values)
            {
                if (p.Admitted && p.State == PlayerState.WaitingForSnapshot) StartTransfer(p);
            }
        }

        private void OnSnapshotFailed(string reason)
        {
            _snapshotPending = false;
            _log.Error(LogCategory.Join, "snapshot failed: " + reason);
            foreach (Peer p in new List<Peer>(_peers.Values))
            {
                if (p.Admitted && p.State == PlayerState.WaitingForSnapshot)
                {
                    _payload.Reset();
                    SessionPayloads.WriteText(_payload, "The host could not save the city: " + reason);
                    SendControl(p, ControlKind.SessionEnd, _payload);
                    Remove(p, 0, "snapshot failed", true);
                }
            }
        }

        private void StartTransfer(Peer p)
        {
            try
            {
                p.Snapshot = _snapshot;
                p.SenderStream = new FileStream(_snapshot.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                p.Sender = new BlobSender(p.Channel.Outbox, _log);
                BlobManifestInfo info = p.Sender.Start(_nextBlobId++, p.SenderStream, "snapshot");
                p.SnapshotSize = info.Size;
                SnapshotManifest m = _snapshot.Manifest;
                m.Size = info.Size;
                m.DigestLo = info.Digest.Lo;
                m.DigestHi = info.Digest.Hi;
                m.ChunkSize = info.ChunkSize;
                uint providerId = m.SnapshotId; // the provider's id (for Release); the wire carries the blob id
                m.SnapshotId = info.BlobId;
                _payload.Reset();
                try
                {
                    SnapshotManifestCodec.Write(_payload, m);
                }
                finally
                {
                    m.SnapshotId = providerId;
                }
                SendControl(p, ControlKind.SnapshotManifest, _payload);
                p.State = PlayerState.Downloading;
                _playersDirty = true;
                _log.Info(LogCategory.Join, p.Name + ": sending the snapshot (" + info.Size + " bytes, " + info.ChunkCount + " chunks)");
            }
            catch (Exception ex)
            {
                _log.Error(LogCategory.Join, p.Name + ": could not start the snapshot transfer", ex);
                Remove(p, 0, "snapshot transfer failed", true);
            }
        }

        private void ReleaseSnapshotIfUnused()
        {
            for (int i = _retired.Count - 1; i >= 0; i--)
            {
                if (IsStreamed(_retired[i])) continue;
                _snapshots.Release(_retired[i].Manifest.SnapshotId);
                _retired.RemoveAt(i);
            }
            if (_snapshot == null || IsStreamed(_snapshot)) return;
            foreach (Peer p in _peers.Values)
            {
                if (p.Admitted && p.State == PlayerState.WaitingForSnapshot) return;
            }
            _snapshots.Release(_snapshot.Manifest.SnapshotId);
            _snapshot = null;
        }

        private bool IsStreamed(SnapshotReady snapshot)
        {
            foreach (Peer p in _peers.Values)
            {
                if (p.Snapshot == snapshot) return true;
            }
            return false;
        }

        private static float Progress(Peer p)
        {
            switch (p.State)
            {
                case PlayerState.Downloading:
                    return p.Sender != null && p.SnapshotSize > 0 ? (float)Math.Min(1.0, p.Sender.TotalBytesSent / (double)p.SnapshotSize) : 0f;
                case PlayerState.Loading:
                    return p.LoadProgress;
                case PlayerState.Live:
                    return 1f;
                default:
                    return 0f;
            }
        }

        // ---------------------------------------------------------------- helpers

        private void DistributeChat(ChatEntry entry)
        {
            _body.Reset();
            ChatMessage.Write(_body, new ChatMessage { From = entry.From, Text = entry.Text, IsSystem = entry.IsSystem });
            foreach (Peer p in _peers.Values)
            {
                if (p.Admitted) p.Channel.Send(MessageKind.Chat, _body);
            }
            ChatReceived?.Invoke(entry);
        }

        private void SendControl(Peer p, ControlKind kind, ByteWriter payload)
        {
            _body.Reset();
            ControlMessage.Write(_body, kind, payload.Buffer, 0, payload.Length);
            p.Channel.Send(MessageKind.Control, _body);
        }

        private void BroadcastControl(ControlKind kind, ByteWriter payload, bool live)
        {
            _body.Reset();
            ControlMessage.Write(_body, kind, payload.Buffer, 0, payload.Length);
            foreach (Peer p in _peers.Values)
            {
                if (p.Admitted && (!live || p.State == PlayerState.Live)) p.Channel.Send(MessageKind.Control, _body);
            }
        }

        private void Refuse(Peer p, RefuseReason reason, string detail)
        {
            _log.Info(LogCategory.Session, "refused " + p.Name + " (" + reason + "): " + detail);
            _body.Reset();
            RefuseMessage.Write(_body, new RefuseMessage { Reason = reason, Detail = ProtocolText.Truncate(detail, RefuseMessage.MaxDetailChars) });
            p.Channel.Send(MessageKind.Refuse, _body);
            Remove(p, 0, "refused: " + reason, true);
        }

        private void Remove(Peer p, int reason, string text, bool close)
        {
            if (!_peers.Remove(p.Channel.Connection)) return;
            CloseSender(p);
            if (close) p.Channel.Close(reason, text);
            else p.Channel.Dispose();
            if (p.Admitted)
            {
                _playersDirty = true;
                if (p.State == PlayerState.Live) SystemMessage(p.Name + " left");
                PeerLeft?.Invoke(p.Id);
            }
        }

        private static void CloseSender(Peer p)
        {
            p.Sender?.Cancel();
            p.Sender = null;
            p.SenderStream?.Dispose();
            p.SenderStream = null;
            p.Snapshot = null;
        }
    }
}
