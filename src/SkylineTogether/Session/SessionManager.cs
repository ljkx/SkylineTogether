using System;
using System.Collections.Generic;
using System.Globalization;
using Colossal.Serialization.Entities;
using Game;
using Game.PSI;
using Game.SceneFlow;
using SkylineTogether.Core;
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
        private int _chatVersion;
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

        public IReadOnlyList<ChatEntry> Chat => _chat;

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
                Notify("Load or start a city first, then host it.");
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
                _chat.Clear();
                _chatVersion++;
                AddChat(new ChatEntry { IsSystem = true, Text = "You are hosting \"" + options.SessionName + "\". Friends can join with your Steam ID " + LocalSteamId.ToString(CultureInfo.InvariantCulture)
                                                               + (transport.ListeningIpPort > 0 ? " or your IP address and port " + transport.ListeningIpPort : "") + "." });
                HostStarted?.Invoke(host);
                Notify("Hosting started. Share your Steam ID: " + LocalSteamId.ToString(CultureInfo.InvariantCulture));
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
                Notify("Could not start hosting: " + ex.Message);
            }
        }

        public void StartJoining()
        {
            if (!CanJoin)
            {
                // review fix: say why (a joiner whose game is still loading used to be told "Already in a session")
                string why;
                if (InSession) why = "Already in a session. Leave it first.";
                else if (!SteamAvailable) why = "Steam is not available. Start the game through Steam and log in.";
                else if (string.IsNullOrWhiteSpace(_settings.JoinTarget)) why = "Enter the host's Steam ID or address first.";
                else if (GameManager.instance != null && GameManager.instance.isGameLoading) why = "Your game is still loading. Wait until it has finished, then join.";
                else why = "Joining is not possible right now.";
                Notify(why);
                return;
            }
            Endpoint endpoint;
            try
            {
                endpoint = ParseTarget(_settings.JoinTarget);
            }
            catch (ArgumentException ex)
            {
                Notify(ex.Message);
                return;
            }
            try
            {
                var transport = new SteamSocketsTransport(endpoint.SteamId != 0 ? TransportKind.SteamP2P : TransportKind.SteamIp, _log);
                var client = new SessionClient(transport, _info, _loader, new ClientOptions { PlayerName = PlayerName, SteamId = LocalSteamId, Password = _settings.JoinPassword ?? "" }, _clock, _log);
                client.ChatReceived += AddChat;
                client.PlayersChanged += () => _playersVersion++;
                client.ToastReceived += Notify;
                client.StateChanged += () => OnClientStateChanged(client);
                Client = client;
                LastEndReason = null;
                _chat.Clear();
                _chatVersion++;
                ClientStarted?.Invoke(client);
                client.Connect(endpoint);
            }
            catch (Exception ex)
            {
                _log.Error(LogCategory.Session, "joining failed", ex);
                Notify("Could not join: " + ex.Message);
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
            if (client.State == ClientState.Live) Notify("Joined \"" + client.Session?.SessionName + "\". Have fun!");
        }

        private void EndClient()
        {
            SessionClient client = Client;
            if (client == null) return;
            Client = null;
            LastEndReason = client.EndReason;
            client.Dispose();
            _loader.Dispose();
            _playersVersion++;
            if (!string.IsNullOrEmpty(LastEndReason)) AddChat(new ChatEntry { IsSystem = true, Text = LastEndReason });
            Notify(LastEndReason ?? "Disconnected.");
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
            if (_chat.Count > MaxChatLines) _chat.RemoveRange(0, _chat.Count - MaxChatLines);
            _chatVersion++;
        }

        public static void Notify(string text)
        {
            try
            {
                NotificationSystem.Push("SkylineTogether." + Guid.NewGuid().ToString("N").Substring(0, 6), title: "Skyline Together", text: text);
            }
            catch (Exception)
            {
                // the notification UI may not exist yet (very early in boot)
            }
            Mod.Log?.Info(LogCategory.Ui, "notification: " + text);
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
