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
        /// <summary>Empty: no password. Read live: a change applies to the next challenge.</summary>
        public string Password = "";
        /// <summary>
        /// A connection that arrived on the direct-IP socket or has no Steam identity must answer the
        /// password challenge; without a password set such connections are refused (<c>NotInvited</c>), so a direct-IP
        /// listen socket never admits anyone unauthenticated. In-process connections (<see cref="PeerIdentity.Local"/>, the
        /// solo test recorder) are exempt; turn it off only for a host without any network part.
        /// </summary>
        public bool PasswordForUnauthenticated = true;
        /// <summary>Open, or Steam friends and invited players only (read live).</summary>
        public JoinPolicy JoinPolicy = JoinPolicy.Open;
        /// <summary>True when a Steam ID is the host's Steam friend or was invited (null: nobody is).</summary>
        public Func<ulong, bool> IsFriendOrInvited;
        /// <summary>Banned Steam IDs and addresses (read live; the game layer persists it).</summary>
        public BanList Bans = new BanList();
        /// <summary>Password challenges that may be open at once per Steam ID or address.</summary>
        public int MaxPendingChallengesPerKey = 2;
        public int MaxPlayers = 8;
        /// <summary>Refuse joiners whose mod list differs from the host's (read live; otherwise they are told what differs).</summary>
        public bool RequireSameMods = true;
        /// <summary>
        /// Refuse joiners whose asset packs differ from the host's (read live). Off: they join, are told which packs differ,
        /// and objects of packs they lack are left out on their side.
        /// </summary>
        public bool RequireSameAssets;
        public ListenOptions Listen = new ListenOptions();
        public double HandshakeTimeoutSeconds = 30;
        /// <summary>
        /// An admitted joiner that sends nothing at all (not even ping replies) for this long is dropped, so a joiner whose
        /// game hung does not hold the host's simulation forever. Generous, because the joiner's main thread stalls
        /// while the game deserializes the city.
        /// </summary>
        public double JoinStallTimeoutSeconds = 180;
        /// <summary>Presence updates of a peer that arrive faster than this are dropped instead of relayed.</summary>
        public double PresenceMinIntervalSeconds = 0.04;
        /// <summary>
        /// A joiner admitted within this many seconds of the last snapshot request gets that snapshot (still
        /// being written, or finished and kept) instead of a new one, as long as the city cannot have changed since the
        /// request: nothing was broadcast and the simulation was held or paused at every tick. 0 turns reuse off.
        /// </summary>
        public double SnapshotReuseSeconds = 30;
        /// <summary>
        /// Snapshot chunks are also pumped from a background thread every 2 ms (<see cref="Transport.BackgroundPump"/>),
        /// so a low frame rate or a frame hitch never leaves the transport idle. Only connections that are
        /// <see cref="IThreadSafeConnection"/> (Steam) use it; the session's own tick keeps pumping as well.
        /// </summary>
        public bool BackgroundPump;

        /// <summary>
        /// A live peer that sends nothing at all (live clients answer the host's pings every second and
        /// acknowledge every batch) for this long is dropped as not responding, so a frozen client does not pin the id
        /// quarantine and its queue forever. Its game is offered a rejoin.
        /// </summary>
        public double LiveSilenceTimeoutSeconds = 120;

        /// <summary>
        /// The longest a join may take from admission to live, whatever the joiner reports (a joiner that
        /// keeps sending load progress but never finishes would hold the host's simulation forever).
        /// </summary>
        public double MaxJoinSeconds = 1800;

        /// <summary>
        /// A snapshot the save system has not delivered after this long is abandoned (the waiting joiners are
        /// told, the next join requests a new one); without it a lost callback blocked every later join.
        /// </summary>
        public double SnapshotTimeoutSeconds = 360; // beyond the joiner's own 300 s wait (ClientOptions.SnapshotWaitTimeoutSeconds)

        /// <summary>The largest message a connection may send before it is admitted (handshake messages only).</summary>
        public int UnadmittedMaxBodyBytes = 1024 * 1024;

        /// <summary>Message handling failures (other than malformed data) after which a peer is disconnected.</summary>
        public int MaxHandlingFailures = 50;

        /// <summary>Chat lines a peer may send per second (on average; bursts of 5).</summary>
        public double ChatPerSecond = 2;

        /// <summary>
        /// Game speed requests a peer may send per second (on average; bursts of <see cref="SessionHost.SpeedRequestBurst"/>):
        /// every accepted request changes the host's speed and is broadcast to everyone, so a flood is dropped.
        /// </summary>
        public double SpeedRequestsPerSecond = 2;
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
            /// <summary>The remote address (meaningful for direct connections only).</summary>
            public string Address;
            /// <summary>The connection arrived on the direct-IP socket.</summary>
            public bool Direct;
            /// <summary>An in-process connection (the test recorder): no Steam identity needed, no join policy.</summary>
            public bool Local;
            /// <summary>Steam authenticated <see cref="SteamId"/> (the hello's claim is never trusted).</summary>
            public bool SteamAuthenticated;
            /// <summary>The password was right, or none was needed; nothing compatibility-related is sent before.</summary>
            public bool Authenticated;
            /// <summary>The keys of the password attempt limiter (Steam ID and/or address).</summary>
            public string[] AuthKeys;
            /// <summary>The chat line when this player is removed (" was removed", " was banned"); null: " left".</summary>
            public string LeaveText;
            public double Since;
            public BlobSender Sender;
            public FileStream SenderStream;
            public SnapshotReady Snapshot;
            // the first snapshot request this peer may use (one issued after its admission)
            public uint MinSnapshotGen;
            public long SnapshotSize;
            public float LoadProgress;
            /// <summary>The joiner's own download fraction (from its LoadProgress reports), -1 until it reported one.</summary>
            public float DownloadProgress = -1f;
            /// <summary>Last time anything arrived from this peer.</summary>
            public double LastHeard;
            public double LastPresence = double.NegativeInfinity;
            public double LiveSince;
            public int PingMs;
            public double LastPingSent;
            public uint PingSequence;
            public bool Admitted;
            /// <summary>Reliable <c>Epoch</c> messages queued for this peer (the watermarks of <c>ApplyAckBook</c>).</summary>
            public long EpochSent;
            /// <summary>The player's role (ARCHITECTURE.md#roles-and-moderation); the host toggles builder ↔ viewer in the panel.</summary>
            public SessionRole Role = SessionRole.Builder;
            /// <summary>The stage timings of this player's join (from its admission).</summary>
            public JoinTimeline Timeline;
            /// <summary>The background pump's registration of this peer's snapshot transfer (null without one).</summary>
            public IDisposable PumpToken;
            /// <summary>When the peer was admitted (join deadline).</summary>
            public double AdmittedAt;
            /// <summary>Message handling failures that were not decode errors, and when the last was logged.</summary>
            public int Failures;
            public double LastFailureLog = double.NegativeInfinity;
            /// <summary>Flood guards for chat lines and blob have-bitmaps.</summary>
            public double ChatTokens = 5;
            public double ChatRefilledAt;
            public double SpeedTokens = SpeedRequestBurst;
            public double SpeedRefilledAt;
            public long SpeedRequestsDropped;
            public double LastHave = double.NegativeInfinity;
        }

        /// <summary>Completed joins kept for the diagnostics and the UI.</summary>
        private const int RecentJoinCount = 8;

        private static readonly long JoinerQueueLimitBytes = new OutboxConfig().DisconnectQueuedBytes;

        private readonly ITransport _transport;
        private readonly IGameInfoProvider _info;
        private readonly ISnapshotProvider _snapshots;
        private readonly HostOptions _options;
        private readonly IClock _clock;

        /// <summary>Speed requests a peer may send at once before <see cref="HostOptions.SpeedRequestsPerSecond"/> applies.</summary>
        public const int SpeedRequestBurst = 4;
        private readonly ILog _log;
        private readonly Dictionary<IConnection, Peer> _peers = new Dictionary<IConnection, Peer>();
        private readonly ByteWriter _body = new ByteWriter(4096);
        private readonly ByteWriter _broadcastEnvelope = new ByteWriter(4096); // a broadcast's shared envelope
        private readonly ByteWriter _payload = new ByteWriter(4096);
        private readonly ByteReader _reader = new ByteReader();
        private readonly Random _random = new Random();
        private readonly uint _sessionId;
        private readonly byte[] _salt;
        private SnapshotReady _snapshot;
        private readonly List<SnapshotReady> _retired = new List<SnapshotReady>();
        private bool _snapshotPending;
        private uint _snapshotGen;
        // the generation of _snapshot, when the latest request was issued, and whether the city can have
        // changed since (a broadcast, or a tick with the simulation running): only an unchanged city reuses a snapshot
        private uint _snapshotReadyGen;
        private double _snapshotRequestedAt = double.NegativeInfinity;
        private bool _snapshotUnchanged;
        private double _snapshotSaveSeconds = double.NaN;
        private readonly List<JoinTimeline> _recentJoins = new List<JoinTimeline>();
        private BackgroundPump _pump;
        private uint _nextBlobId = 1;
        private bool _playersDirty;
        private double _lastPlayersSent;
        private SpeedState _speed = new SpeedState { Speed = 1, Paused = true };
        private SpeedState _sentSpeed;
        private bool _disposed;
        private bool _stopping;
        // the PBKDF2 key of the current password (derived once per password, not per answer)
        private string _keyFor;
        private byte[] _key;
        private Action<PeerId, Envelope> _appMessage;
        // frame stalls of the host itself (a long save, a hitch, a minimised game): the silence and age limits
        // are suspended for a while afterwards, because the peers' messages were waiting for this side
        private double _lastTickAt = double.NaN;
        private double _stallGraceUntil = double.NegativeInfinity;
        // snapshot requests that timed out (their late callbacks release the snapshot and are otherwise ignored)
        private uint _abandonedSnapshotGen;
        private long _listenerFailures;

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

        /// <summary>Wrong-password lockouts per Steam ID and address (tunable by tests).</summary>
        public PasswordAttemptLimiter PasswordAttempts { get; } = new PasswordAttemptLimiter();

        /// <summary>The ban list changed (<see cref="Ban"/>, <see cref="Unban"/>); the game layer persists it.</summary>
        public event Action BansChanged;

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

        /// <summary>
        /// A joiner was admitted with differences that do not block the join (the same report it receives as
        /// <see cref="ControlKind.JoinNotice"/>): the player's id and name, and the report.
        /// </summary>
        public event Action<PeerId, string, MismatchReport> JoinerDiffers;

        /// <summary>
        /// A live peer's presence (cursor, tool) arrived; <see cref="PresenceMessage.From"/> is set by the host. It has
        /// already been relayed to the other live peers. The instance is new for every call.
        /// </summary>
        public event Action<PresenceMessage> PresenceReceived;

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
                list.Add(new PlayerInfo { Id = p.Id, Name = p.Name, SteamId = p.SteamId, Role = p.Role, State = p.State, PingMs = p.PingMs, Progress = Progress(p) });
            }
            list.Sort((a, b) => a.Id.Value.CompareTo(b.Id.Value));
            return list;
        }

        public void Tick()
        {
            if (_disposed) return;
            _transport.Poll(this);
            double now = _clock.Seconds;
            // after a stall of this side, give the peers time to be heard before anything is judged silent
            if (!double.IsNaN(_lastTickAt) && now - _lastTickAt > StallSeconds)
            {
                _stallGraceUntil = now + StallGraceSeconds;
                _log.Info(LogCategory.Session, "the host did not tick for " + (now - _lastTickAt).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                                               + " s; silence and queue-age limits wait " + StallGraceSeconds + " s");
            }
            _lastTickAt = now;
            bool grace = now < _stallGraceUntil;
            if (_snapshotPending && _options.SnapshotTimeoutSeconds > 0 && now - _snapshotRequestedAt > _options.SnapshotTimeoutSeconds) AbandonSnapshot(now);
            // a running simulation may change the city at any frame, so a snapshot requested earlier is no
            // longer the city of a new joiner
            if (_snapshotUnchanged && !HoldSimulation && !_speed.Paused && !_speed.Held) _snapshotUnchanged = false;
            List<(Peer Peer, string Reason)> drop = null;
            foreach (Peer p in _peers.Values)
            {
                if (!p.Admitted && now - p.Since > _options.HandshakeTimeoutSeconds)
                {
                    (drop ?? (drop = new List<(Peer, string)>())).Add((p, "handshake timed out"));
                    continue;
                }
                if (p.Admitted && p.State != PlayerState.Live && _options.MaxJoinSeconds > 0 && now - p.AdmittedAt > _options.MaxJoinSeconds && !grace)
                {
                    // an absolute deadline, whatever progress the joiner reports
                    _log.Warn(LogCategory.Join, p.Name + ": still joining after " + (int)(now - p.AdmittedAt) + " s (" + p.State + "); dropping it");
                    SystemMessage(p.Name + " took too long to join.");
                    SendText(p, ControlKind.SessionEnd, "Joining took too long (more than " + (int)(_options.MaxJoinSeconds / 60) + " minutes). Try joining again.");
                    (drop ?? (drop = new List<(Peer, string)>())).Add((p, "join took too long"));
                    continue;
                }
                if (p.Admitted && p.State == PlayerState.Live && _options.LiveSilenceTimeoutSeconds > 0 && now - p.LastHeard > _options.LiveSilenceTimeoutSeconds && !grace)
                {
                    // live clients answer pings every second; total silence means its game froze or is gone
                    _log.Warn(LogCategory.Session, p.Name + ": nothing received for " + (int)(now - p.LastHeard) + " s; dropping it as not responding");
                    SystemMessage(p.Name + " stopped responding.");
                    SendText(p, ControlKind.Kick, "The host received nothing from your game for " + (int)_options.LiveSilenceTimeoutSeconds + " s. Join again to continue.");
                    (drop ?? (drop = new List<(Peer, string)>())).Add((p, "not responding"));
                    continue;
                }
                if (p.Admitted && p.State != PlayerState.Live && now - p.LastHeard > _options.JoinStallTimeoutSeconds && !grace)
                {
                    // a hung joiner would otherwise hold the host's simulation forever
                    _log.Warn(LogCategory.Join, p.Name + ": nothing received for " + (int)(now - p.LastHeard) + " s while joining (" + p.State + "); dropping it");
                    SystemMessage(p.Name + " stopped responding while joining.");
                    (drop ?? (drop = new List<(Peer, string)>())).Add((p, "stopped responding while joining"));
                    continue;
                }
                if (p.Sender != null)
                {
                    // with a background pump the transfer is pumped there (no lock contention on this thread)
                    if (p.PumpToken == null) p.Sender.Pump();
                    if (p.Sender.IsFailed)
                    {
                        _log.Warn(LogCategory.Join, p.Name + ": snapshot transfer failed: " + p.Sender.FailureReason);
                        (drop ?? (drop = new List<(Peer, string)>())).Add((p, "snapshot transfer failed"));
                        continue;
                    }
                    if (p.Timeline != null && !p.Timeline.Has(JoinStage.TransferSent) && p.Sender.IsComplete) p.Timeline.Mark(JoinStage.TransferSent, now);
                }
                if (p.Admitted && now - p.LastPingSent > 1.0)
                {
                    p.LastPingSent = now;
                    _body.Reset();
                    PingMessage.Write(_body, new PingMessage { Sequence = ++p.PingSequence, SentSeconds = now });
                    p.Channel.Send(MessageKind.Ping, _body);
                }
                // a joiner's game may not read for a long time while it loads the city: only the size limit applies
                // then, not the 60 s age limit (which would drop a joiner whose load stalls its main thread)
                PeerOutbox outbox = p.Channel.Outbox;
                // (and for a minute after it went live, while it drains what queued up during its load)
                // and not right after a stall of the host itself (its own frames, not the peer, held the queue)
                bool ageApplies = !grace && (!p.Admitted || (p.State == PlayerState.Live && now - p.LiveSince > 60.0));
                bool tooSlow = ageApplies
                    ? outbox.OverLimit
                    : outbox.IsFaulted || outbox.QueuedBytes > JoinerQueueLimitBytes;
                if (tooSlow)
                {
                    _log.Warn(LogCategory.Net, p.Name + ": the connection is too slow to keep up; disconnecting");
                    // the players learn why (the dropped player rejoins with a fresh snapshot: its catch-up)
                    if (p.Admitted) SystemMessage(p.Name + " could not keep up with the session and was disconnected; joining again catches up.");
                    (drop ?? (drop = new List<(Peer, string)>())).Add((p, "connection too slow"));
                    continue;
                }
                p.Channel.Pump();
            }
            if (drop != null)
            {
                foreach ((Peer p, string reason) in drop) Remove(p, TransportCloseReason.Timeout, reason, true);
            }

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

        /// <summary>
        /// Sends a replication message to every admitted peer (queued for joiners until they are live). With
        /// several peers the body is encoded and compressed once and the same envelope is queued for each of them.
        /// </summary>
        public void Broadcast(MessageKind kind, byte[] body, int offset, int count, Lane lane = Lane.Reliable)
        {
            // the city changed after the last snapshot request; a later joiner needs a newer snapshot
            _snapshotUnchanged = false;
            Peer first = null;
            bool shared = false;
            foreach (Peer p in _peers.Values)
            {
                if (!p.Admitted) continue;
                if (first == null)
                {
                    first = p;
                    continue;
                }
                if (!shared)
                {
                    shared = true;
                    _broadcastEnvelope.Reset();
                    first.Channel.Encode(kind, body, offset, count, _broadcastEnvelope);
                    SendEncodedCounted(first, kind, lane);
                }
                if (p.Channel.Compression == first.Channel.Compression) SendEncodedCounted(p, kind, lane);
                else SendCounted(p, kind, body, offset, count, lane);
            }
            if (first != null && !shared) SendCounted(first, kind, body, offset, count, lane);
        }

        private void SendEncodedCounted(Peer p, MessageKind kind, Lane lane)
        {
            bool ok = p.Channel.SendEncoded(kind, _broadcastEnvelope.Buffer, 0, _broadcastEnvelope.Length, lane);
            if (ok && kind == MessageKind.Epoch && lane == Lane.Reliable) p.EpochSent++;
        }

        public void SendTo(PeerId peer, MessageKind kind, byte[] body, int offset, int count)
        {
            foreach (Peer p in _peers.Values)
            {
                if (p.Admitted && p.Id == peer) SendCounted(p, kind, body, offset, count, Lane.Reliable);
            }
        }

        /// <summary>Every reliable <c>Epoch</c> message a peer is sent is counted (its client counts what it applied).</summary>
        private static void SendCounted(Peer p, MessageKind kind, byte[] body, int offset, int count, Lane lane)
        {
            bool ok = p.Channel.Send(kind, body, offset, count, lane);
            if (ok && kind == MessageKind.Epoch && lane == Lane.Reliable) p.EpochSent++;
        }

        /// <summary>Reliable <c>Epoch</c> messages queued so far for an admitted peer (0 when unknown).</summary>
        public long GetEpochSent(PeerId peer)
        {
            foreach (Peer p in _peers.Values)
            {
                if (p.Admitted && p.Id == peer) return p.EpochSent;
            }
            return 0;
        }

        /// <summary>
        /// True while a join is in progress (a peer between admission and live, or a snapshot being taken): a
        /// snapshot may then still be written with the current bindings, so released net ids must stay quarantined.
        /// </summary>
        public bool JoinInProgress => _snapshotPending || HoldSimulation;

        /// <summary>The admitted peers (appended to <paramref name="output"/>).</summary>
        public void GetAdmittedPeers(List<PeerId> output)
        {
            foreach (Peer p in _peers.Values)
            {
                if (p.Admitted) output.Add(p.Id);
            }
        }

        /// <summary>
        /// The send-side state of an admitted peer for the bandwidth scheduler (ARCHITECTURE.md#deferred-values-and-link-pacing): whether it is live,
        /// the reliable bytes waiting in its outbox, the bytes the transport still holds and its send rate.
        /// </summary>
        public bool TryGetLink(PeerId peer, out PeerSendState link)
        {
            foreach (Peer p in _peers.Values)
            {
                if (!p.Admitted || p.Id != peer) continue;
                ConnectionStats c = p.Channel.Outbox.Stats;
                link = new PeerSendState
                {
                    Live = p.State == PlayerState.Live,
                    QueuedReliableBytes = p.Channel.Outbox.QueuedReliableBytes,
                    PendingReliableBytes = Math.Max(0, c.PendingReliableBytes),
                    SendRateBytesPerSecond = Math.Max(0, c.SendRateBytesPerSecond),
                    OutBytesPerSecond = Math.Max(0f, c.OutBytesPerSecond),
                };
                return true;
            }
            link = default;
            return false;
        }

        /// <summary>Sends the host player's own presence to every live peer (unreliable; dropped when the link is busy).</summary>
        public void SendPresence(PresenceMessage presence)
        {
            if (_disposed || presence == null) return;
            presence.From = PeerId.Host;
            _body.Reset();
            PresenceMessage.Write(_body, presence);
            foreach (Peer p in _peers.Values)
            {
                if (p.State == PlayerState.Live) p.Channel.SendUnreliable(MessageKind.Presence, _body);
            }
        }

        /// <summary>Link numbers summed over the admitted peers (ping: the worst live peer).</summary>
        public SessionNetStats GetNetStats()
        {
            var s = new SessionNetStats { PingMs = -1 };
            foreach (Peer p in _peers.Values)
            {
                if (!p.Admitted) continue;
                ConnectionStats c = p.Channel.Outbox.Stats; // the frame's query (the HUD reads it every frame)
                s.Connections++;
                s.InBytesPerSecond += Math.Max(0f, c.InBytesPerSecond);
                s.OutBytesPerSecond += Math.Max(0f, c.OutBytesPerSecond);
                s.QueuedBytes += p.Channel.Outbox.QueuedBytes;
                if (p.State == PlayerState.Live) s.PingMs = Math.Max(s.PingMs, p.PingMs);
            }
            return s;
        }

        /// <summary>One line per connection (admitted or not) for the diagnostics dump.</summary>
        public IReadOnlyList<string> DescribePeers()
        {
            var lines = new List<string>();
            double now = _clock.Seconds;
            foreach (Peer p in _peers.Values)
            {
                ConnectionStats c = p.Channel.Connection.GetStats();
                lines.Add((p.Admitted ? p.Id.ToString() : "unadmitted") + " \"" + p.Name + "\" steam=" + p.SteamId + (p.SteamAuthenticated ? "" : " (unauthenticated)") + (p.Direct ? " direct" : "")
                          + " role=" + p.Role + " auth=" + (p.Authenticated ? "ok" : p.AwaitingPassword ? "challenged" : "pending") + " state=" + p.State
                          + " progress=" + Progress(p).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                          + " ping=" + p.PingMs + "ms steamPing=" + c.PingMs + "ms quality=" + c.Quality.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                          + " in=" + (int)c.InBytesPerSecond + "B/s out=" + (int)c.OutBytesPerSecond + "B/s pendingReliable=" + c.PendingReliableBytes
                          + " outboxQueued=" + p.Channel.Outbox.QueuedBytes + " lastHeard=" + (now - p.LastHeard).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s ago"
                          + (p.Sender != null ? " snapshotSent=" + p.Sender.TotalBytesSent + "/" + p.SnapshotSize + " window=" + p.Sender.CurrentWindowBytes : "")
                          + " sendRate=" + c.SendRateBytesPerSecond + "B/s");
                if (p.Timeline != null && p.State != PlayerState.Live) lines.Add("    " + p.Timeline.Describe());
            }
            return lines;
        }

        /// <summary>
        /// The timings of the last completed joins (oldest first, at most 8; copies), for the diagnostics
        /// dump and the UI.
        /// </summary>
        public IReadOnlyList<JoinTimeline> RecentJoins
        {
            get
            {
                var list = new List<JoinTimeline>(_recentJoins.Count);
                foreach (JoinTimeline t in _recentJoins) list.Add(t.Clone());
                return list;
            }
        }

        /// <summary>The most recent completed join (a copy), or null.</summary>
        public JoinTimeline LastJoin => _recentJoins.Count > 0 ? _recentJoins[_recentJoins.Count - 1].Clone() : null;

        /// <summary>A copy of the join timings of an admitted peer (null when unknown).</summary>
        public JoinTimeline GetJoinTimeline(PeerId peer)
        {
            foreach (Peer p in _peers.Values)
            {
                if (p.Admitted && p.Id == peer) return p.Timeline?.Clone();
            }
            return null;
        }

        /// <summary>Bumped whenever a join completes (UI refresh key).</summary>
        public int JoinsCompleted { get; private set; }

        /// <summary>
        /// The raw send side of a live peer, for engines that write complete envelopes themselves
        /// (the motion encoder, ARCHITECTURE.md#movers); null when the peer is unknown or not live.
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

        /// <summary>A player's role changed (<see cref="SetRole"/>).</summary>
        public event Action<PeerId, SessionRole> RoleChanged;

        /// <summary>The role of an admitted peer (<see cref="SessionRole.Host"/> for the host itself, Viewer for unknown peers).</summary>
        public SessionRole GetRole(PeerId id)
        {
            if (id == PeerId.Host) return SessionRole.Host;
            foreach (Peer p in _peers.Values)
            {
                if (p.Admitted && p.Id == id) return p.Role;
            }
            return SessionRole.Viewer;
        }

        /// <summary>
        /// Sets a peer's role (builder, co-host or viewer; the host role cannot be given). The player list carries it to
        /// every client, whose own role decides whether its tools predict (viewer: no-predict gate). False when the
        /// peer is unknown or the role is not assignable.
        /// </summary>
        public bool SetRole(PeerId id, SessionRole role)
        {
            if (role == SessionRole.Host || id == PeerId.Host) return false;
            foreach (Peer p in _peers.Values)
            {
                if (!p.Admitted || p.Id != id) continue;
                if (p.Role == role) return true;
                p.Role = role;
                _playersDirty = true;
                _log.Info(LogCategory.Session, p.Name + " is now a " + role.ToString().ToLowerInvariant());
                SystemMessage(p.Name + " is now a " + role.ToString().ToLowerInvariant() + ".");
                RoleChanged?.Invoke(id, role);
                return true;
            }
            return false;
        }

        public void Kick(PeerId id, string reason)
        {
            foreach (Peer p in new List<Peer>(_peers.Values))
            {
                if (p.Id != id || !p.Admitted) continue;
                _payload.Reset();
                SessionPayloads.WriteText(_payload, reason ?? "Kicked by the host");
                SendControl(p, ControlKind.Kick, _payload);
                _log.Info(LogCategory.Session, p.Name + " was removed: " + (reason ?? "kicked by the host"));
                p.LeaveText = " was removed";
                Remove(p, 0, "kicked", true);
            }
        }

        /// <summary>
        /// Removes an admitted player and bans them: by authenticated Steam ID, or by address when the
        /// connection had no Steam identity. The player is told (<see cref="ControlKind.Ban"/>) and refused with
        /// <see cref="RefuseReason.Banned"/> from then on. False for the host or an unknown peer.
        /// </summary>
        public bool Ban(PeerId id, string reason)
        {
            if (id == PeerId.Host) return false;
            Peer p = FindAdmitted(id);
            if (p == null) return false;
            bool added = _options.Bans.Add(p.SteamAuthenticated ? p.SteamId : 0, p.SteamAuthenticated ? null : p.Address, p.Name, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            _payload.Reset();
            SessionPayloads.WriteText(_payload, reason ?? "Banned by the host");
            SendControl(p, ControlKind.Ban, _payload);
            _log.Info(LogCategory.Session, p.Name + " was banned (" + (p.SteamAuthenticated ? "Steam ID " + p.SteamId : "address " + BanList.AddressHost(p.Address)) + ")" + (added ? "" : "; already on the list"));
            p.LeaveText = " was banned";
            Remove(p, 0, "banned", true);
            if (added) BansChanged?.Invoke();
            EnforceBans();
            return true;
        }

        /// <summary>Removes a ban by its <see cref="BanEntry.Key"/>; false when there is none.</summary>
        public bool Unban(string key)
        {
            if (!_options.Bans.Remove(key)) return false;
            _log.Info(LogCategory.Session, "ban lifted: " + key);
            BansChanged?.Invoke();
            return true;
        }

        /// <summary>Drops every connection the ban list matches now (after it changed from outside).</summary>
        public void EnforceBans()
        {
            foreach (Peer p in new List<Peer>(_peers.Values))
            {
                if (!IsBanned(p)) continue;
                if (p.Admitted)
                {
                    _payload.Reset();
                    SessionPayloads.WriteText(_payload, "Banned by the host");
                    SendControl(p, ControlKind.Ban, _payload);
                    p.LeaveText = " was banned";
                    Remove(p, 0, "banned", true);
                }
                else
                {
                    Refuse(p, RefuseReason.Banned, "You are banned from this session.");
                }
            }
        }

        private bool IsBanned(Peer p) => _options.Bans != null && _options.Bans.IsBanned(p.SteamAuthenticated ? p.SteamId : 0, p.Direct || !p.SteamAuthenticated ? p.Address : null);

        private Peer FindAdmitted(PeerId id)
        {
            foreach (Peer p in _peers.Values)
            {
                if (p.Admitted && p.Id == id) return p;
            }
            return null;
        }

        /// <summary>
        /// Ends the session for everyone: players get <see cref="ControlKind.SessionEnd"/>, connections still in the
        /// handshake a <see cref="RefuseReason.HostShuttingDown"/> refusal (before, they only saw the
        /// connection drop).
        /// </summary>
        public void Stop(string reason)
        {
            if (_disposed) return;
            _stopping = true;
            _payload.Reset();
            SessionPayloads.WriteText(_payload, reason ?? "The host ended the session");
            BroadcastControl(ControlKind.SessionEnd, _payload, live: false);
            foreach (Peer p in new List<Peer>(_peers.Values))
            {
                if (p.Admitted) Remove(p, 0, "session ended", true);
                else Refuse(p, RefuseReason.HostShuttingDown, reason ?? "The host ended the session.");
            }
            Dispose();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (Peer p in _peers.Values) CloseSender(p);
            _pump?.Dispose();
            _pump = null;
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
                LastHeard = _clock.Seconds,
                Name = connection.Peer.DisplayName ?? connection.Peer.ToString(),
                SteamId = connection.Peer.SteamId,
                SteamAuthenticated = connection.Peer.SteamId != 0,
                Address = connection.Peer.Address,
                Direct = connection.Peer.Direct,
                Local = connection.Peer.Local,
            };
            // until it is admitted a connection may only send small handshake messages
            peer.Channel.MaxBodyBytes = _options.UnadmittedMaxBodyBytes;
            _peers[connection] = peer;
            _log.Info(LogCategory.Session, "connection from " + connection.Peer + "; waiting for its hello");
        }

        void ITransportEventSink.OnDisconnected(IConnection connection, int reason, string text)
        {
            if (!_peers.TryGetValue(connection, out Peer p)) return;
            _log.Info(LogCategory.Session, p.Name + " disconnected (" + reason + ": " + text + ")");
            bool wasJoining = p.Admitted && p.State != PlayerState.Live;
            Remove(p, reason, text, false);
            if (wasJoining) SystemMessage(p.Name + " lost the connection while joining.");
        }

        void ITransportEventSink.OnMessage(IConnection connection, Lane lane, byte[] buffer, int offset, int count)
        {
            if (_disposed || !_peers.TryGetValue(connection, out Peer p)) return;
            p.LastHeard = _clock.Seconds;
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
                // rate-limited, and a peer whose messages keep failing is disconnected (no error storm)
                p.Failures++;
                double now = _clock.Seconds;
                if (now - p.LastFailureLog >= 10)
                {
                    p.LastFailureLog = now;
                    _log.Error(LogCategory.Session, p.Name + ": message handling failed (" + p.Failures + " so far)", ex);
                }
                if (p.Failures >= _options.MaxHandlingFailures && _peers.ContainsKey(connection))
                {
                    _log.Warn(LogCategory.Session, p.Name + ": " + p.Failures + " messages could not be handled; disconnecting");
                    SendText(p, ControlKind.Kick, "The host could not handle the data your game sends. Join again to get a fresh copy of the city.");
                    Remove(p, TransportCloseReason.ProtocolError, "messages keep failing", true);
                }
            }
        }

        /// <summary>A frame gap above this many seconds counts as a stall of the host.</summary>
        private const double StallSeconds = 5;

        /// <summary>How long silence and queue-age limits wait after a stall of the host.</summary>
        private const double StallGraceSeconds = 15;

        /// <summary>Total exceptions thrown by app-message listeners (diagnostics).</summary>
        public long ListenerFailures => _listenerFailures;

        private void SendText(Peer p, ControlKind kind, string text)
        {
            try
            {
                _payload.Reset();
                SessionPayloads.WriteText(_payload, text);
                SendControl(p, kind, _payload);
            }
            catch (Exception)
            {
                // best effort: the peer is being dropped
            }
        }

        /// <summary>
        /// Delivers a replication message to every listener on its own, like the client does: one listener
        /// that throws (a feature's bug) neither stops the others nor counts against the peer.
        /// </summary>
        private void DeliverApp(PeerId peer, Envelope e)
        {
            Action<PeerId, Envelope> listeners = _appMessage;
            if (listeners == null) return;
            foreach (Delegate d in listeners.GetInvocationList())
            {
                try
                {
                    ((Action<PeerId, Envelope>)d)(peer, e);
                }
                catch (Exception ex)
                {
                    _listenerFailures++;
                    if (_listenerFailures <= 5 || _listenerFailures % 100 == 0)
                    {
                        _log.Error(LogCategory.Session, "a listener failed on " + e.Kind + " from " + peer + " (" + _listenerFailures + " so far)", ex);
                    }
                }
            }
        }

        /// <summary>
        /// The save system did not deliver the requested snapshot in time: the waiting joiners are told and
        /// removed, and the next join requests a new snapshot. A late delivery of the abandoned one is released unused.
        /// </summary>
        private void AbandonSnapshot(double now)
        {
            _abandonedSnapshotGen = _snapshotGen;
            _log.Error(LogCategory.Join, "the snapshot requested " + (int)(now - _snapshotRequestedAt) + " s ago did not arrive; abandoned");
            OnSnapshotFailed("saving the city took too long");
        }

        // ---------------------------------------------------------------- handshake

        private void Handle(Peer p, Envelope e)
        {
            switch (e.Kind)
            {
                case MessageKind.Hello:
                {
                    if (p.Hello != null) return;
                    if (_stopping)
                    {
                        Refuse(p, RefuseReason.HostShuttingDown, "The host is ending the session.");
                        return;
                    }
                    var theirProtocol = HelloMessage.PeekProtocolVersion(e.Body, e.BodyOffset, e.BodyCount);
                    if (theirProtocol != ProtocolVersion.Current)
                    {
                        // a peer that still has to prove the password learns no version of the host
                        if (NeedsPassword(p))
                        {
                            Refuse(p, RefuseReason.ProtocolVersion, "Skyline Together versions differ. Update both games to the same mod version.");
                        }
                        else
                        {
                            Refuse(p, RefuseReason.ProtocolVersion, "Skyline Together versions differ. Host: " + ProtocolVersion.Current + ". Update both games to the same mod version.",
                                new MismatchReport { Kind = "protocol", HostProtocol = ProtocolVersion.Current.ToString(System.Globalization.CultureInfo.InvariantCulture), ClientProtocol = theirProtocol.ToString(System.Globalization.CultureInfo.InvariantCulture) });
                        }
                        return;
                    }
                    p.Hello = HelloMessage.Decode(e.Body, e.BodyOffset, e.BodyCount);
                    if (!string.IsNullOrEmpty(p.Hello.DisplayName)) p.Name = p.Hello.DisplayName;
                    // only the transport's authenticated Steam ID is an identity; before, the hello's claim
                    // replaced it, so anyone could pose as (and replace the connection of) another player
                    if (p.Hello.SteamId != 0 && p.Hello.SteamId != p.SteamId)
                    {
                        _log.Info(LogCategory.Session, p.Name + " claims Steam ID " + p.Hello.SteamId + (p.SteamAuthenticated ? " but Steam authenticated " + p.SteamId : " without Steam authentication") + "; the claim is ignored");
                    }
                    if (!Screen(p)) return;
                    if (!ResolveDuplicate(p)) return;
                    BeginAuthentication(p);
                    return;
                }
                case MessageKind.Presence:
                    if (p.State == PlayerState.Live) RelayPresence(p, e);
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
                    // a token bucket per peer (bursts of 5, then ChatPerSecond); excess lines are dropped
                    double t = _clock.Seconds;
                    p.ChatTokens = Math.Min(5, p.ChatTokens + (t - p.ChatRefilledAt) * _options.ChatPerSecond);
                    p.ChatRefilledAt = t;
                    if (p.ChatTokens < 1) return;
                    p.ChatTokens -= 1;
                    ChatMessage chat = ChatMessage.Decode(e.Body, e.BodyOffset, e.BodyCount);
                    string text = (chat.Text ?? "").Trim();
                    if (text.Length > 0) DistributeChat(new ChatEntry { From = p.Id, Name = p.Name, Text = text });
                    return;
                }
                case MessageKind.BlobHave:
                {
                    if (p.Sender == null) return;
                    // an empty have-bitmap re-queues the whole snapshot; the receiver sends one per 0.5 s at most
                    double t = _clock.Seconds;
                    if (t - p.LastHave < 0.25) return;
                    p.LastHave = t;
                    ArraySegment<byte> bitmap = BlobCodec.DecodeHave(e.Body, e.BodyOffset, e.BodyCount, out uint blobId);
                    if (blobId == p.Sender.Manifest.BlobId) p.Sender.OnHave(bitmap.Array, bitmap.Offset, bitmap.Count);
                    return;
                }
                default:
                    // unknown and reserved kinds (ARCHITECTURE.md#reserved-wire-values) never reach a listener
                    if (p.State == PlayerState.Live && Limits.IsDeliverable(e.Kind)) DeliverApp(p.Id, e);
                    return;
            }
        }

        /// <summary>
        /// A Steam user who is already in the session connects again (their game crashed or the old connection is
        /// dying): the stale peer is replaced, unless it is evidently still loading the city, in which case the new
        /// attempt is refused with an explanation. Returns false when <paramref name="p"/> was refused.
        /// </summary>
        private bool ResolveDuplicate(Peer p)
        {
            if (!p.SteamAuthenticated || p.SteamId == 0) return true;
            Peer old = null;
            foreach (Peer o in _peers.Values)
            {
                if (o != p && o.Admitted && o.SteamAuthenticated && o.SteamId == p.SteamId) old = o;
            }
            if (old == null) return true;
            bool stillJoining = old.State != PlayerState.Live && _clock.Seconds - old.LastHeard < 15;
            if (stillJoining)
            {
                Refuse(p, RefuseReason.HostPreparing, "You are still joining this session from an earlier attempt (" + old.State.ToString().ToLowerInvariant()
                                                      + "). Wait for it to finish, or restart the game and join again.");
                return false;
            }
            _log.Info(LogCategory.Session, p.Name + " connected again; dropping the old connection (" + old.State + ")");
            Remove(old, 0, "replaced by a new connection", true);
            return true;
        }

        private void RelayPresence(Peer p, Envelope e)
        {
            double now = _clock.Seconds;
            if (now - p.LastPresence < _options.PresenceMinIntervalSeconds) return; // flood guard
            p.LastPresence = now;
            PresenceMessage m = PresenceMessage.Decode(e.Body, e.BodyOffset, e.BodyCount);
            m.From = p.Id; // never trust the sender's own claim
            if (!RolePermissions.Predicts(p.Role)) m.PreviewLength = 0; // viewers build nothing, so they show no preview
            _body.Reset();
            PresenceMessage.Write(_body, m);
            foreach (Peer o in _peers.Values)
            {
                if (o != p && o.State == PlayerState.Live) o.Channel.SendUnreliable(MessageKind.Presence, _body);
            }
            PresenceReceived?.Invoke(m);
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
                    OnPasswordResponse(p, payload);
                    return;
                case ControlKind.PlayerAction:
                    OnPlayerAction(p, payload);
                    return;
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
                    p.LiveSince = _clock.Seconds;
                    if (p.Timeline != null)
                    {
                        p.Timeline.Mark(JoinStage.Downloaded, p.LiveSince);
                        p.Timeline.Mark(JoinStage.Loaded, p.LiveSince);
                        p.Timeline.Mark(JoinStage.Live, p.LiveSince);
                        _log.Info(LogCategory.Join, p.Timeline.Describe());
                        _recentJoins.Add(p.Timeline.Clone());
                        if (_recentJoins.Count > RecentJoinCount) _recentJoins.RemoveAt(0);
                        JoinsCompleted++;
                    }
                    ConnectionTimeouts.Apply(p.Channel.Connection, joining: false, _log);
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
                    // the joiner reports its phase: while it still downloads the host keeps showing "downloading"
                    // (before, any report switched the player to "loading" at once)
                    if (p.State != PlayerState.Downloading && p.State != PlayerState.Loading) return;
                    _reader.Reset(payload, 0, payload.Length);
                    JoinPhase phase = SessionPayloads.ReadLoadProgress(_reader, out float fraction);
                    if (phase == JoinPhase.Downloading)
                    {
                        if (p.State == PlayerState.Downloading) p.DownloadProgress = fraction;
                    }
                    else
                    {
                        p.LoadProgress = fraction;
                        if (p.State != PlayerState.Loading) _log.Info(LogCategory.Join, p.Name + " downloaded the city and is loading it");
                        p.Timeline?.Mark(JoinStage.Downloaded, _clock.Seconds);
                        p.State = PlayerState.Loading;
                    }
                    _playersDirty = true;
                    return;
                }
                case ControlKind.Disconnecting:
                {
                    bool wasJoining = p.Admitted && p.State != PlayerState.Live;
                    Remove(p, 0, "left", false);
                    if (wasJoining) SystemMessage(p.Name + " cancelled joining.");
                    return;
                }
                case ControlKind.SpeedRequest:
                {
                    if (p.State != PlayerState.Live || !RolePermissions.Allows(p.Role, Permission.Speed)) return;
                    // a token bucket per peer (bursts of SpeedRequestBurst, then SpeedRequestsPerSecond); excess requests are dropped
                    double t = _clock.Seconds;
                    p.SpeedTokens = Math.Min(SpeedRequestBurst, p.SpeedTokens + Math.Max(0, t - p.SpeedRefilledAt) * _options.SpeedRequestsPerSecond);
                    p.SpeedRefilledAt = t;
                    if (p.SpeedTokens < 1)
                    {
                        p.SpeedRequestsDropped++;
                        if (p.SpeedRequestsDropped == 1 || p.SpeedRequestsDropped % 100 == 0)
                        {
                            _log.Warn(LogCategory.Session, p.Name + " asks for speed changes faster than " + _options.SpeedRequestsPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                                           + " per second; " + p.SpeedRequestsDropped + " dropped so far");
                        }
                        return;
                    }
                    p.SpeedTokens -= 1;
                    _reader.Reset(payload, 0, payload.Length);
                    SpeedRequested?.Invoke(p.Id, SessionPayloads.ReadSpeed(_reader));
                    return;
                }
                default:
                    return;
            }
        }

        // ---------------------------------------------------------------- screening, password, moderation

        /// <summary>Bans and the join policy, judged on the transport's identity before anything else is sent.</summary>
        private bool Screen(Peer p)
        {
            if (IsBanned(p))
            {
                Refuse(p, RefuseReason.Banned, "You are banned from this session.");
                return false;
            }
            if (_options.JoinPolicy == JoinPolicy.FriendsAndInvited && p.SteamAuthenticated && !p.Local && !FriendOrInvited(p.SteamId))
            {
                Refuse(p, RefuseReason.NotInvited, "This session is open to the host's Steam friends and invited players only.");
                return false;
            }
            return true;
        }

        private bool FriendOrInvited(ulong steamId)
        {
            Func<ulong, bool> check = _options.IsFriendOrInvited;
            if (check == null) return false;
            try
            {
                return check(steamId);
            }
            catch (Exception ex)
            {
                _log.Warn(LogCategory.Session, "the friends check failed; treating " + steamId + " as not invited", ex);
                return false;
            }
        }

        /// <summary>A password is set, or the connection is direct or has no Steam identity (those always need one).</summary>
        private bool NeedsPassword(Peer p)
        {
            return !string.IsNullOrEmpty(_options.Password) || (_options.PasswordForUnauthenticated && !p.Local && (p.Direct || !p.SteamAuthenticated));
        }

        /// <summary>The password attempt limiter's keys of a connection: its Steam ID, and its address when that means something.</summary>
        private static string[] AuthKeysOf(Peer p)
        {
            var keys = new List<string>(2);
            if (p.SteamAuthenticated) keys.Add("steam:" + p.SteamId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            string host = p.Direct || !p.SteamAuthenticated ? BanList.AddressHost(p.Address) : null;
            if (host != null) keys.Add("ip:" + host);
            if (keys.Count == 0) keys.Add("unknown"); // no identity at all: all such connections share one budget
            return keys.ToArray();
        }

        /// <summary>After the hello: the password challenge when one is needed, else straight to the compatibility checks.</summary>
        private void BeginAuthentication(Peer p)
        {
            if (!NeedsPassword(p))
            {
                p.Authenticated = true;
                TryAdmit(p);
                return;
            }
            if (string.IsNullOrEmpty(_options.Password))
            {
                Refuse(p, RefuseReason.NotInvited, "This host accepts direct connections only with a session password. Join through Steam instead.");
                return;
            }
            p.AuthKeys = AuthKeysOf(p);
            double now = _clock.Seconds;
            if (PasswordAttempts.IsLocked(p.AuthKeys, now, out double wait))
            {
                Refuse(p, RefuseReason.BadPassword, LockoutText(wait));
                return;
            }
            if (PendingChallenges(p.AuthKeys) >= Math.Max(1, _options.MaxPendingChallengesPerKey))
            {
                Refuse(p, RefuseReason.BadPassword, "Too many join attempts at once. Try again in a few seconds.");
                return;
            }
            p.AwaitingPassword = true;
            p.HostNonce = SessionCrypto.RandomBytes(16);
            _payload.Reset();
            PasswordChallengeMessage.Write(_payload, new PasswordChallengeMessage { HostNonce = p.HostNonce, Salt = _salt, SessionId = _sessionId });
            SendControl(p, ControlKind.PasswordChallenge, _payload);
        }

        private int PendingChallenges(string[] keys)
        {
            int n = 0;
            foreach (Peer o in _peers.Values)
            {
                if (!o.AwaitingPassword || o.AuthKeys == null) continue;
                foreach (string k in o.AuthKeys)
                {
                    if (Array.IndexOf(keys, k) < 0) continue;
                    n++;
                    break;
                }
            }
            return n;
        }

        private static string LockoutText(double seconds)
        {
            return "Too many wrong passwords. Try again in " + Math.Max(1, (int)Math.Ceiling(seconds)).ToString(System.Globalization.CultureInfo.InvariantCulture) + " s.";
        }

        private byte[] PasswordKey()
        {
            string password = _options.Password ?? "";
            if (_key == null || !string.Equals(_keyFor, password, StringComparison.Ordinal))
            {
                _key = SessionCrypto.DeriveKey(password, _salt);
                _keyFor = password;
            }
            return _key;
        }

        private void OnPasswordResponse(Peer p, byte[] payload)
        {
            if (!p.AwaitingPassword) return;
            p.AwaitingPassword = false; // one answer per challenge: the nonce is spent
            PasswordResponseMessage r = PasswordResponseMessage.Decode(payload, 0, payload.Length);
            double now = _clock.Seconds;
            // another connection of the same player or address may have locked it out meanwhile
            if (PasswordAttempts.IsLocked(p.AuthKeys, now, out double wait))
            {
                Refuse(p, RefuseReason.BadPassword, LockoutText(wait));
                return;
            }
            bool right = !string.IsNullOrEmpty(_options.Password) && r.ClientNonce != null && r.ClientNonce.Length >= SessionCrypto.MinNonceBytes
                         && SessionCrypto.FixedTimeEquals(SessionCrypto.PasswordMac(PasswordKey(), p.HostNonce, r.ClientNonce, _sessionId), r.Mac);
            if (!right)
            {
                double lockout = PasswordAttempts.OnFailure(p.AuthKeys, now);
                _log.Warn(LogCategory.Session, p.Name + ": wrong password (" + string.Join(", ", p.AuthKeys ?? new string[0]) + ")" + (lockout > 0 ? "; locked out for " + (int)Math.Ceiling(lockout) + " s" : ""));
                Refuse(p, RefuseReason.BadPassword, lockout > 0 ? "Wrong password. " + LockoutText(lockout) : "Wrong password.");
                return;
            }
            PasswordAttempts.OnSuccess(p.AuthKeys);
            p.Authenticated = true;
            TryAdmit(p);
        }

        /// <summary>A co-host's kick or role change, checked against the sender's role (<see cref="Moderation"/>).</summary>
        private void OnPlayerAction(Peer p, byte[] payload)
        {
            if (!p.Admitted || p.State != PlayerState.Live) return;
            _reader.Reset(payload, 0, payload.Length);
            PlayerActionKind action = SessionPayloads.ReadPlayerAction(_reader, out PeerId target, out SessionRole role);
            Peer t = FindAdmitted(target);
            SessionRole targetRole = target == PeerId.Host ? SessionRole.Host : t != null ? t.Role : SessionRole.Viewer;
            bool allowed = t != null && t != p && (action == PlayerActionKind.Kick ? Moderation.CanKick(p.Role, targetRole)
                : action == PlayerActionKind.SetRole && Moderation.CanSetRole(p.Role, targetRole, role));
            if (!allowed)
            {
                _log.Info(LogCategory.Session, p.Name + " (" + p.Role + ") may not " + action + " " + target + " (" + targetRole + "); ignored");
                return;
            }
            if (action == PlayerActionKind.Kick)
            {
                _log.Info(LogCategory.Session, p.Name + " (co-host) removes " + t.Name);
                Kick(target, "Removed by " + p.Name);
            }
            else
            {
                SetRole(target, role);
            }
        }

        private void TryAdmit(Peer p)
        {
            if (p.Hello == null || p.Manifest == null || p.Admitted || p.AwaitingPassword || !p.Authenticated) return;
            HandshakeManifest mineManifest = _info.BuildManifest() ?? new HandshakeManifest();
            HandshakeDigests mine = mineManifest.Digests;
            HandshakeDigests theirs = p.Hello.Digests;
            if (theirs.ModBuild != mine.ModBuild)
            {
                string hostBuild = BuildId.Format(mine.ModBuild), clientBuild = BuildId.Format(theirs.ModBuild);
                Refuse(p, RefuseReason.Mismatch, "Skyline Together builds differ (host build " + hostBuild + ", yours " + clientBuild + "). Both players need exactly the same mod version.",
                    new MismatchReport { Kind = "build", HostBuild = hostBuild, ClientBuild = clientBuild });
                return;
            }
            if (!string.Equals(theirs.GameVersion, mine.GameVersion, StringComparison.Ordinal))
            {
                Refuse(p, RefuseReason.Mismatch, "Game versions differ (host " + mine.GameVersion + ", you " + theirs.GameVersion + "). Update both games.",
                    new MismatchReport { Kind = "game", HostGameVersion = mine.GameVersion, ClientGameVersion = theirs.GameVersion });
                return;
            }
            // mods, DLC and content packs, asset packs and mod settings, each compared item by item only when its
            // digests differ (the hello's digests are the ones the client computed from these lists)
            HandshakeManifest theirsManifest = p.Manifest;
            theirsManifest.Digests = theirs;
            CompatibilityResult compat = CompatibilityCheck.Compare(mineManifest, theirsManifest,
                new CompatibilityOptions { RequireSameMods = _options.RequireSameMods, RequireSameAssets = _options.RequireSameAssets });
            if (compat.Refusal != null)
            {
                // the detail text is cut at 1024 characters; the report carries the complete lists for the UI
                Refuse(p, RefuseReason.Mismatch, compat.RefusalDetail, compat.Refusal);
                return;
            }
            // several handshakes may pass the password at once; each is checked here, after its own answer
            if (IsFull())
            {
                Refuse(p, RefuseReason.Full, "The session is full.");
                return;
            }
            Admit(p, compat.Notice);
        }

        /// <summary>
        /// Tells an admitted joiner what differs without blocking the join (<see cref="ControlKind.JoinNotice"/>), logs it
        /// and raises <see cref="JoinerDiffers"/>.
        /// </summary>
        private void SendJoinNotice(Peer p, MismatchReport notice)
        {
            _log.Info(LogCategory.Join, p.Name + " joins with differences: " + notice.Describe());
            byte[] encoded = MismatchReport.Encode(notice);
            if (encoded != null && encoded.Length <= ControlMessage.MaxPayloadBytes)
            {
                _payload.Reset();
                _payload.WriteBytes(encoded, 0, encoded.Length);
                SendControl(p, ControlKind.JoinNotice, _payload);
            }
            try
            {
                JoinerDiffers?.Invoke(p.Id, p.Name, notice);
            }
            catch (Exception ex)
            {
                _log.Error(LogCategory.Session, "a join-notice listener failed", ex);
            }
        }

        /// <summary>True when no further player fits (<see cref="HostOptions.MaxPlayers"/> counts the host).</summary>
        private bool IsFull()
        {
            int admitted = 0;
            foreach (Peer o in _peers.Values) if (o.Admitted) admitted++;
            return admitted + 1 >= _options.MaxPlayers;
        }

        private void Admit(Peer p, MismatchReport notice)
        {
            p.Id = AllocatePeerId();
            if (p.Id == PeerId.Host)
            {
                Refuse(p, RefuseReason.Full, "The session is full.");
                return;
            }
            p.Admitted = true;
            p.State = PlayerState.WaitingForSnapshot;
            double admittedAt = _clock.Seconds;
            p.AdmittedAt = admittedAt;
            p.Channel.MaxBodyBytes = int.MaxValue; // admitted: the kinds' own limits apply
            p.Timeline = new JoinTimeline { Who = p.Name };
            p.Timeline.Mark(JoinStage.Start, admittedAt);
            p.Timeline.Mark(JoinStage.Welcome, admittedAt);
            // the joiner's main thread will stall while its game loads the city (ARCHITECTURE.md#live-session)
            ConnectionTimeouts.Apply(p.Channel.Connection, joining: true, _log);
            _payload.Reset();
            SessionPayloads.WriteSessionInfo(_payload, new SessionInfo { SessionName = _options.SessionName, HostName = _options.HostName });
            _body.Reset();
            WelcomeMessage.Write(_body, new WelcomeMessage
            {
                SessionId = _sessionId,
                AssignedPeer = p.Id,
                Role = p.Role,
                Permissions = RolePermissions.Of(p.Role),
                SessionSettings = _payload.ToArray(),
                HostNonce = new byte[0],
                EnabledSysEntries = new uint[0],
                Compression = CompressionKind.ManagedLz4,
            });
            p.Channel.Send(MessageKind.Welcome, _body);
            _playersDirty = true;
            _log.Info(LogCategory.Session, "admitted " + p.Name + " as " + p.Id);
            if (notice != null) SendJoinNotice(p, notice);
            SystemMessage(p.Name + " is joining…");
            PeerJoining?.Invoke(p.Id);
            // nothing changed since the last request (no broadcast, simulation held or paused throughout):
            // that snapshot is this city too, and the peer's stream (from now on) has no gap behind it
            if (SnapshotReusable(admittedAt))
            {
                p.MinSnapshotGen = _snapshotGen;
                p.Timeline.SnapshotReused = true;
                p.Timeline.Mark(JoinStage.SnapshotRequested, _snapshotRequestedAt);
                if (_snapshotPending)
                {
                    _log.Info(LogCategory.Join, p.Name + " gets the snapshot that is being taken (nothing changed since it was requested)");
                    return;
                }
                _log.Info(LogCategory.Join, p.Name + " gets the snapshot requested " + (admittedAt - _snapshotRequestedAt).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                                            + " s ago (nothing changed since)");
                p.Timeline.Mark(JoinStage.SnapshotReady, admittedAt);
                p.Timeline.SaveSeconds = _snapshotSaveSeconds;
                StartTransfer(p);
                return;
            }
            // a snapshot already requested may have serialized the city before this admission, and this
            // peer's stream starts only now; it waits for the next request (OnSnapshotReady issues it)
            p.MinSnapshotGen = _snapshotGen + 1;
            if (_snapshot != null && !_snapshotPending)
            {
                // the replication stream for this peer starts now, so it needs a snapshot taken from now on
                _retired.Add(_snapshot);
                _snapshot = null;
            }
            RequestSnapshot();
        }

        /// <summary>
        /// The latest snapshot request is recent and the city cannot have changed since (see
        /// <see cref="HostOptions.SnapshotReuseSeconds"/>), so its snapshot also serves a peer admitted now: while it is
        /// being written, or once written while it is still kept (another player still waits for it or downloads it; an
        /// unused snapshot file is released at once, as before).
        /// </summary>
        private bool SnapshotReusable(double now)
        {
            if (!_snapshotUnchanged || _options.SnapshotReuseSeconds <= 0) return false;
            if (now - _snapshotRequestedAt > _options.SnapshotReuseSeconds) return false;
            return _snapshotPending || (_snapshot != null && _snapshotReadyGen == _snapshotGen);
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
            uint gen = ++_snapshotGen;
            _snapshotRequestedAt = _clock.Seconds;
            _snapshotUnchanged = true;
            foreach (Peer p in _peers.Values)
            {
                if (p.Admitted && p.State == PlayerState.WaitingForSnapshot && p.MinSnapshotGen <= gen) p.Timeline?.Mark(JoinStage.SnapshotRequested, _snapshotRequestedAt, overwrite: true);
            }
            _log.Info(LogCategory.Join, "taking a snapshot of the city");
            // callbacks of an abandoned (timed-out) request are released and otherwise ignored
            _snapshots.RequestSnapshot(ready => OnSnapshotReady(ready, gen), reason =>
            {
                if (gen > _abandonedSnapshotGen) OnSnapshotFailed(reason);
            });
        }

        private void OnSnapshotReady(SnapshotReady ready, uint gen)
        {
            if (gen <= _abandonedSnapshotGen)
            {
                _log.Info(LogCategory.Join, "an abandoned snapshot arrived late; released");
                _snapshots.Release(ready.Manifest.SnapshotId);
                return;
            }
            _snapshotPending = false;
            if (_disposed)
            {
                _snapshots.Release(ready.Manifest.SnapshotId);
                return;
            }
            _snapshot = ready;
            _snapshotReadyGen = gen;
            double took = _clock.Seconds - _snapshotRequestedAt;
            _snapshotSaveSeconds = ready.SaveSeconds > 0 ? ready.SaveSeconds : took;
            _log.Info(LogCategory.Join, "snapshot ready: " + new FileInfo(ready.FilePath).Length + " bytes, "
                                        + took.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " s after the request"
                                        + (ready.SaveSeconds > 0 ? " (save " + ready.SaveSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " s)" : "")
                                        + (ready.DigestKnown ? ", digest computed while writing" : ""));
            // a copy: StartTransfer removes the peer when the transfer cannot start
            bool later = false;
            foreach (Peer p in new List<Peer>(_peers.Values))
            {
                if (!p.Admitted || p.State != PlayerState.WaitingForSnapshot) continue;
                if (p.MinSnapshotGen > gen)
                {
                    later = true; // admitted while this snapshot was being taken
                    continue;
                }
                if (p.Timeline != null)
                {
                    p.Timeline.Mark(JoinStage.SnapshotReady, _clock.Seconds);
                    p.Timeline.SaveSeconds = _snapshotSaveSeconds;
                }
                StartTransfer(p);
            }
            if (later)
            {
                _retired.Add(_snapshot);
                _snapshot = null;
                RequestSnapshot();
            }
        }

        private void OnSnapshotFailed(string reason)
        {
            _snapshotPending = false;
            _snapshotUnchanged = false;
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
                SnapshotReady snapshot = _snapshot;
                p.Snapshot = snapshot;
                p.SenderStream = new FileStream(snapshot.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                p.Sender = new BlobSender(p.Channel.Outbox, _log);
                // the digest is computed at most once per snapshot (by the provider while writing, or here for
                // the first receiver) and reused for every later receiver of the same file
                bool known = snapshot.DigestKnown && snapshot.Size == p.SenderStream.Length;
                Hashing.Digest128? digest = known ? new Hashing.Digest128(snapshot.DigestLo, snapshot.DigestHi) : (Hashing.Digest128?)null;
                BlobManifestInfo info = p.Sender.Start(_nextBlobId++, p.SenderStream, "snapshot", BlobCodec.DefaultChunkBytes, digest);
                if (!known)
                {
                    snapshot.DigestKnown = true;
                    snapshot.DigestLo = info.Digest.Lo;
                    snapshot.DigestHi = info.Digest.Hi;
                    snapshot.Size = info.Size;
                }
                p.SnapshotSize = info.Size;
                if (p.Timeline != null)
                {
                    p.Timeline.SnapshotBytes = info.Size;
                    p.Timeline.DigestMilliseconds = p.Sender.DigestMilliseconds;
                    p.Timeline.DigestReused = known;
                    p.Timeline.Mark(JoinStage.TransferStarted, _clock.Seconds);
                }
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
                // the manifest and the first window of chunks leave now, not one frame later
                p.Channel.Pump();
                p.Sender.Pump();
                p.Channel.Pump();
                // and between frames from the background thread (thread-safe transports only)
                if (_options.BackgroundPump && p.Channel.Connection is IThreadSafeConnection)
                {
                    if (_pump == null) _pump = new BackgroundPump(_log, "Skyline Together snapshot pump");
                    BlobSender sender = p.Sender;
                    PeerOutbox outbox = p.Channel.Outbox;
                    p.PumpToken = _pump.Add(() =>
                    {
                        sender.Pump();
                        outbox.Pump();
                    }, p.Name + "'s snapshot");
                }
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
                    if (p.DownloadProgress >= 0f) return p.DownloadProgress; // what the joiner actually received
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

        private void Refuse(Peer p, RefuseReason reason, string detail, MismatchReport report = null)
        {
            _log.Info(LogCategory.Session, "refused " + p.Name + " (" + reason + "): " + detail);
            _body.Reset();
            byte[] encoded = MismatchReport.Encode(report);
            if (encoded != null && encoded.Length > RefuseMessage.MaxMismatchReportBytes) encoded = null;
            RefuseMessage.Write(_body, new RefuseMessage { Reason = reason, Detail = ProtocolText.Truncate(detail, RefuseMessage.MaxDetailChars), MismatchReport = encoded });
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
                if (p.State == PlayerState.Live || p.LeaveText != null) SystemMessage(p.Name + (p.LeaveText ?? " left"));
                PeerLeft?.Invoke(p.Id);
            }
        }

        private static void CloseSender(Peer p)
        {
            // the background pump stops calling this transfer before it is cancelled and its file closed
            p.PumpToken?.Dispose();
            p.PumpToken = null;
            p.Sender?.Cancel();
            p.Sender = null;
            p.SenderStream?.Dispose();
            p.SenderStream = null;
            p.Snapshot = null;
        }
    }
}
