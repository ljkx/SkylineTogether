using System;
using System.Collections.Generic;
using System.Globalization;
using Colossal.PSI.Common;
using Colossal.Serialization.Entities;
using Game;
using Game.PSI;
using Game.SceneFlow;
using Game.UI.Localization;
using SkylineTogether.Core;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Net;
using SkylineTogether.Settings;
using Steamworks;
using ILog = SkylineTogether.Core.ILog;

namespace SkylineTogether.Session
{
    public enum SessionMode
    {
        None,
        Hosting,
        Joining,
        Client
    }

    /// <summary>Why the last client session ended, for the joiner's error screen (kept until dismissed or the next join).</summary>
    public sealed class SessionEndInfo
    {
        /// <summary>Changes for every new end (the UI hides a screen it dismissed by this number).</summary>
        public int Seq;
        public ClientEndKind Kind;
        public RefuseReason Refuse;
        /// <summary>The reason as text (often the host's English detail).</summary>
        public string Text;
        public MismatchReport Report;
        /// <summary>What the player tried to join (Steam ID or address).</summary>
        public string Target;
    }

    /// <summary>
    /// The mod's session controller (main thread): starts hosting or joining from the settings, ticks the session every
    /// frame from <see cref="SessionDriver"/>, ends it when the player leaves the city, and keeps what the UI shows
    /// (status, players, chat). Replication plugs in through <see cref="HostStarted"/> / <see cref="ClientStarted"/>.
    /// </summary>
    public sealed class SessionManager
    {
        public const int VirtualPort = 0;
        public const int DefaultDirectPort = 27015;
        private const int MaxChatLines = 100;

        private readonly ILog _log;
        private readonly SkylineTogetherSettings _settings;
        private readonly MainThreadQueue _main;
        private readonly GameInfoProvider _info = new GameInfoProvider();
        private readonly SnapshotService _snapshots;
        private readonly SnapshotLoader _loader;
        private readonly Core.Testing.StopwatchClock _clock = new Core.Testing.StopwatchClock();
        private readonly List<ChatEntry> _chat = new List<ChatEntry>();
        private readonly List<long> _chatSeq = new List<long>();
        private static long _chatCounter;
        private int _chatVersion;
        private int _endSeq;
        private string _joinTarget;
        private SpeedState _speedRequest;
        private bool _hasSpeedRequest;
        private int _playersVersion;

        public SessionManager(SkylineTogetherSettings settings, ILog log)
        {
            _settings = settings;
            _log = log;
            _main = new MainThreadQueue(log);
            _snapshots = new SnapshotService(_main, log);
            _loader = new SnapshotLoader(_main, log);
            _snapshots.CleanUp();
            _loader.CleanUp();
            GameManager.instance.onGamePreload += OnGamePreload;
            GameManager.instance.onGameLoadingComplete += OnGameLoadingComplete;
        }

        public SessionHost Host { get; private set; }

        public SessionClient Client { get; private set; }

        public ILog Log => _log;

        public IClock Clock => _clock;

        public MainThreadQueue Main => _main;

        public SessionMode Mode
        {
            get
            {
                if (Host != null) return SessionMode.Hosting;
                if (Client == null) return SessionMode.None;
                return Client.State == ClientState.Live ? SessionMode.Client : SessionMode.Joining;
            }
        }

        public bool InSession => Mode != SessionMode.None;

        public bool CanHost => !InSession && SteamAvailable && GameManager.instance != null && GameManager.instance.gameMode == GameMode.Game && !GameManager.instance.isGameLoading;

        public bool CanJoin => !InSession && SteamAvailable && !string.IsNullOrWhiteSpace(_settings.JoinTarget) && (GameManager.instance == null || !GameManager.instance.isGameLoading);

        /// <summary>Why the last session ended (shown until the next one starts).</summary>
        public string LastEndReason { get; private set; }

        /// <summary>The joiner's last session end worth a screen (refusal, error, kick), or null.</summary>
        public SessionEndInfo LastEnd { get; private set; }

        public IReadOnlyList<ChatEntry> Chat => _chat;

        /// <summary>A number per chat line that never repeats, also across sessions (the chat overlay fades by it).</summary>
        public long ChatSeqAt(int index) => index >= 0 && index < _chatSeq.Count ? _chatSeq[index] : -1;

        /// <summary>The Steam ID or address of the current or last join.</summary>
        public string JoinTarget => _joinTarget ?? "";

        public int ChatVersion => _chatVersion;

        public int PlayersVersion => _playersVersion;

        public ulong LocalSteamId
        {
            get
            {
                try
                {
                    return SteamUser.GetSteamID().m_SteamID;
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }

        public int DirectPort => _settings.AllowDirectConnections ? _settings.DirectPort : 0;

        public static bool SteamAvailable
        {
            get
            {
                try
                {
                    return SteamAPI.IsSteamRunning() && SteamUser.GetSteamID().IsValid();
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>A host session started (the replication layer attaches to it).</summary>
        public event Action<SessionHost> HostStarted;

        /// <summary>A client session started.</summary>
        public event Action<SessionClient> ClientStarted;

        /// <summary>The current session ended (host or client).</summary>
        public event Action SessionEnded;

        public string PlayerName
        {
            get
            {
                string name = (_settings.PlayerName ?? "").Trim();
                if (name.Length > 0) return name;
                try
                {
                    name = SteamFriends.GetPersonaName();
                }
                catch (Exception)
                {
                    name = null;
                }
                return string.IsNullOrWhiteSpace(name) ? "Player" : name;
            }
        }

        // ---------------------------------------------------------------- actions

        public void StartHosting()
        {
            if (!CanHost)
            {
                Notify(LocaleSource.Resolve("st.toast.hostNeedsCity"), ProgressState.Warning);
                return;
            }
            SessionHost created = null;
            try
            {
                string name = PlayerName;
                var transport = new SteamSocketsTransport(TransportKind.SteamP2P, _log);
                var options = new HostOptions
                {
                    HostName = name,
                    HostSteamId = LocalSteamId,
                    SessionName = string.IsNullOrWhiteSpace(_settings.SessionName) ? name + "'s city" : _settings.SessionName.Trim(),
                    Password = _settings.HostPassword ?? "",
                    MaxPlayers = Math.Max(2, Math.Min(16, _settings.MaxPlayers)),
                    RequireSameMods = _settings.RequireSameMods,
                    Listen = new ListenOptions { VirtualPort = VirtualPort, AllowIp = _settings.AllowDirectConnections, IpPort = _settings.DirectPort, MaxConnections = Math.Max(2, _settings.MaxPlayers) },
                };
                var host = new SessionHost(transport, _info, _snapshots, options, _clock, _log);
                created = host;
                host.ChatReceived += AddChat;
                host.PlayersChanged += () => _playersVersion++;
                host.SpeedRequested += (peer, speed) =>
                {
                    _speedRequest = speed;
                    _hasSpeedRequest = true;
                };
                host.Start();
                Host = host;
                LastEndReason = null;
                LastEnd = null;
                ClearChat();
                string steamId = LocalSteamId.ToString(CultureInfo.InvariantCulture);
                AddChat(new ChatEntry
                {
                    IsSystem = true,
                    Text = LocaleSource.Resolve("st.chat.hostingStarted", "SESSION", options.SessionName, "ID", steamId)
                           + (transport.ListeningIpPort > 0 ? " " + LocaleSource.Resolve("st.chat.hostingDirect", "PORT", transport.ListeningIpPort.ToString(CultureInfo.InvariantCulture)) : ""),
                });
                HostStarted?.Invoke(host);
                Notify(LocaleSource.Resolve("st.toast.hostingStarted", "SESSION", options.SessionName, "ID", steamId), ProgressState.Complete);
            }
            catch (Exception ex)
            {
                _log.Error(LogCategory.Session, "hosting failed", ex);
                if (Host == null)
                {
                    // review fix: Start() failed (no listen socket), so the transport's Steam callback and poll group leaked
                    try
                    {
                        created?.Dispose();
                    }
                    catch (Exception)
                    {
                        // best effort
                    }
                }
                StopHost(null);
                Notify(LocaleSource.Resolve("st.toast.hostFailed", "ERROR", ex.Message), ProgressState.Failed);
            }
        }

        public void StartJoining()
        {
            if (!CanJoin)
            {
                // review fix: say why (a joiner whose game is still loading used to be told "Already in a session")
                string why;
                if (InSession) why = LocaleSource.Resolve("st.toast.join.inSession");
                else if (!SteamAvailable) why = LocaleSource.Resolve("st.toast.join.noSteam");
                else if (string.IsNullOrWhiteSpace(_settings.JoinTarget)) why = LocaleSource.Resolve("st.toast.join.noTarget");
                else if (GameManager.instance != null && GameManager.instance.isGameLoading) why = LocaleSource.Resolve("st.toast.join.loading");
                else why = LocaleSource.Resolve("st.toast.join.other");
                Notify(why, ProgressState.Warning);
                return;
            }
            Endpoint endpoint;
            try
            {
                endpoint = ParseTarget(_settings.JoinTarget);
            }
            catch (ArgumentException ex)
            {
                Notify(ex.Message, ProgressState.Warning);
                return;
            }
            try
            {
                var transport = new SteamSocketsTransport(endpoint.SteamId != 0 ? TransportKind.SteamP2P : TransportKind.SteamIp, _log);
                var client = new SessionClient(transport, _info, _loader, new ClientOptions { PlayerName = PlayerName, SteamId = LocalSteamId, Password = _settings.JoinPassword ?? "" }, _clock, _log);
                client.ChatReceived += AddChat;
                client.PlayersChanged += () => _playersVersion++;
                client.ToastReceived += text => Notify(text);
                client.StateChanged += () => OnClientStateChanged(client);
                Client = client;
                LastEndReason = null;
                LastEnd = null;
                _joinTarget = _settings.JoinTarget;
                ClearChat();
                ClientStarted?.Invoke(client);
                client.Connect(endpoint);
            }
            catch (Exception ex)
            {
                _log.Error(LogCategory.Session, "joining failed", ex);
                Notify(LocaleSource.Resolve("st.toast.joinFailed", "ERROR", ex.Message), ProgressState.Failed);
                // review fix: listeners saw ClientStarted, so they must also see SessionEnded (and the loader is reset)
                if (Client != null)
                {
                    Client.Dispose();
                    Client = null;
                    _loader.Dispose();
                    _playersVersion++;
                    RaiseSessionEnded();
                }
            }
        }

        public void Leave()
        {
            if (Host != null) StopHost("The host ended the session.");
            if (Client != null)
            {
                Client.Leave();
                EndClient();
            }
        }

        /// <summary>Joins the last target again (the error screen's retry), with a new password unless it is <paramref name="keep"/>.</summary>
        public void RetryJoin(string password, string keep)
        {
            if (password != keep)
            {
                _settings.JoinPassword = password ?? "";
                _settings.ApplyAndSave();
            }
            LastEnd = null;
            StartJoining();
        }

        /// <summary>The player closed the error screen.</summary>
        public void ClearLastEnd()
        {
            LastEnd = null;
        }

        public void SendChat(string text)
        {
            if (Host != null) Host.SendChat(text);
            else Client?.SendChat(text);
        }

        /// <summary>The latest speed a client asked for, once (host only).</summary>
        public bool TakeSpeedRequest(out SpeedState speed)
        {
            speed = _speedRequest;
            bool had = _hasSpeedRequest;
            _hasSpeedRequest = false;
            return had;
        }

        public void Kick(byte peer) => Host?.Kick(new PeerId(peer), "Removed by the host");

        /// <summary>gameplay branch: the host toggles a player between builder and viewer (ARCH §5.6).</summary>
        public void ToggleRole(byte peer)
        {
            SessionHost host = Host;
            if (host == null || peer == 0) return;
            var id = new PeerId(peer);
            host.SetRole(id, RolePermissions.Toggle(host.GetRole(id)));
        }

        // ---------------------------------------------------------------- frame

        public void Tick()
        {
            _main.Drain();
            try
            {
                SteamSocketsTransport.PumpDeferredCloses(); // presence branch: listen sockets of ended sessions linger briefly
                Host?.Tick();
                if (Client != null)
                {
                    if (Client.State == ClientState.Loading) Client.ReportLoadProgress(_loader.Progress);
                    Client.Tick();
                    if (Client != null && Client.State == ClientState.Ended) EndClient();
                }
            }
            catch (Exception ex)
            {
                _log.Error(LogCategory.Session, "session tick failed", ex);
            }
        }

        public void Dispose()
        {
            Leave();
            if (GameManager.instance != null)
            {
                GameManager.instance.onGamePreload -= OnGamePreload;
                GameManager.instance.onGameLoadingComplete -= OnGameLoadingComplete;
            }
        }

        // ---------------------------------------------------------------- internals

        private void OnClientStateChanged(SessionClient client)
        {
            if (client != Client) return;
            _playersVersion++;
            // the "joined" toast comes from SessionToasts (it replaces the join progress toast)
        }

        private void EndClient()
        {
            SessionClient client = Client;
            if (client == null) return;
            Client = null;
            LastEndReason = client.EndReason;
            LastEnd = client.EndKind == ClientEndKind.Left || client.EndKind == ClientEndKind.None ? null : new SessionEndInfo
            {
                Seq = ++_endSeq,
                Kind = client.EndKind,
                Refuse = client.RefusedFor,
                Text = client.EndReason,
                Report = client.EndReport,
                Target = _joinTarget,
            };
            client.Dispose();
            _loader.Dispose();
            _playersVersion++;
            if (!string.IsNullOrEmpty(LastEndReason)) AddChat(new ChatEntry { IsSystem = true, Text = LastEndReason });
            Notify(string.IsNullOrEmpty(LastEndReason) ? LocaleSource.Resolve("st.toast.disconnected") : LastEndReason,
                client.EndKind == ClientEndKind.Left ? ProgressState.Complete : ProgressState.Warning);
            RaiseSessionEnded();
        }

        private void StopHost(string reason)
        {
            SessionHost host = Host;
            if (host == null) return;
            Host = null;
            try
            {
                host.Stop(reason);
            }
            catch (Exception ex)
            {
                _log.Warn(LogCategory.Session, "stopping the host failed", ex);
            }
            _snapshots.ReleaseAll(); // review fix: no snapshot temp file outlives the session
            LastEndReason = reason;
            _playersVersion++;
            RaiseSessionEnded();
        }

        /// <summary>
        /// Review fix: every listener gets the end of the session even when an earlier one throws. Several of them turn
        /// vanilla systems back on (follower gate, follower clock, mover gate); a plain multicast invoke stopped at the
        /// first exception and left the game with its simulation systems disabled.
        /// </summary>
        private void RaiseSessionEnded()
        {
            Action ended = SessionEnded;
            if (ended == null) return;
            foreach (Delegate d in ended.GetInvocationList())
            {
                try
                {
                    ((Action)d)();
                }
                catch (Exception ex)
                {
                    _log.Error(LogCategory.Session, "a session-end listener failed (" + d.Method.DeclaringType?.Name + "." + d.Method.Name + ")", ex);
                }
            }
        }

        private void OnGamePreload(Purpose purpose, GameMode mode)
        {
            // our own join load replaces the city on purpose; any other load leaves the session
            if (Client != null && Client.State == ClientState.Loading && _loader.IsLoading) return;
            if (Host != null)
            {
                _log.Info(LogCategory.Session, "the host is leaving the city (" + purpose + ", " + mode + "); ending the session");
                StopHost("The host left the city.");
            }
            if (Client != null && Client.State != ClientState.Connecting && Client.State != ClientState.Handshaking
                && Client.State != ClientState.WaitingForSnapshot && Client.State != ClientState.Downloading)
            {
                _log.Info(LogCategory.Session, "leaving the city (" + purpose + ", " + mode + "); leaving the session");
                Client.Leave();
                EndClient();
            }
        }

        private void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            _playersVersion++;
        }

        private void AddChat(ChatEntry entry)
        {
            _chat.Add(entry);
            _chatSeq.Add(++_chatCounter);
            if (_chat.Count > MaxChatLines)
            {
                _chat.RemoveRange(0, _chat.Count - MaxChatLines);
                _chatSeq.RemoveRange(0, _chatSeq.Count - MaxChatLines);
            }
            _chatVersion++;
        }

        private void ClearChat()
        {
            _chat.Clear();
            _chatSeq.Clear();
            _chatVersion++;
        }

        /// <summary>
        /// A toast in the game's notification list (top left), removed again after <paramref name="seconds"/>. Title and
        /// text are values, not localization ids (review fix: plain strings became ids, and the toasts never went away).
        /// </summary>
        public static void Notify(string text, ProgressState? state = null, string id = null, float seconds = 7f)
        {
            try
            {
                string identifier = id ?? "SkylineTogether." + Guid.NewGuid().ToString("N").Substring(0, 8);
                NotificationSystem.Pop(identifier, seconds, title: LocalizedString.Value(LocaleSource.Resolve("st.toast.title")), text: LocalizedString.Value(text ?? ""),
                    progressState: state, progress: state == ProgressState.Complete ? 100 : (int?)null);
            }
            catch (Exception)
            {
                // the notification UI may not exist yet (very early in boot)
            }
            Mod.Log?.Info(LogCategory.Ui, "notification: " + text);
        }

        /// <summary>A toast that stays (with progress) until <see cref="Notify"/> or <see cref="Unnotify"/> uses the same id.</summary>
        public static void NotifyProgress(string id, string text, int? percent)
        {
            try
            {
                NotificationSystem.Push(id, title: LocalizedString.Value(LocaleSource.Resolve("st.toast.title")), text: LocalizedString.Value(text ?? ""),
                    progressState: percent.HasValue ? ProgressState.Progressing : ProgressState.Indeterminate, progress: percent);
            }
            catch (Exception)
            {
                // the notification UI may not exist yet
            }
        }

        public static void Unnotify(string id)
        {
            try
            {
                if (NotificationSystem.Exist(id)) NotificationSystem.Pop(id);
            }
            catch (Exception)
            {
                // the notification UI may not exist
            }
        }

        /// <summary>Parses a Steam ID (digits) or <c>host[:port]</c>.</summary>
        public static Endpoint ParseTarget(string target)
        {
            string t = (target ?? "").Trim();
            if (t.Length == 0) throw new ArgumentException("Enter the host's Steam ID or IP address.");
            if (t.Length >= 15 && ulong.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out ulong steamId))
            {
                return new Endpoint { SteamId = steamId, VirtualPort = VirtualPort };
            }
            string host = t;
            int port = DefaultDirectPort;
            int colon = t.LastIndexOf(':');
            if (colon > 0 && t.IndexOf(':') == colon)
            {
                host = t.Substring(0, colon);
                if (!int.TryParse(t.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port) || port <= 0 || port > 65535)
                {
                    throw new ArgumentException("\"" + t + "\" has an invalid port.");
                }
            }
            else if (t.StartsWith("[", StringComparison.Ordinal) && t.Contains("]:"))
            {
                int end = t.IndexOf("]:", StringComparison.Ordinal);
                host = t.Substring(1, end - 1);
                if (!int.TryParse(t.Substring(end + 2), NumberStyles.None, CultureInfo.InvariantCulture, out port)) throw new ArgumentException("\"" + t + "\" has an invalid port.");
            }
            return new Endpoint { Host = host, Port = port };
        }
    }
}
