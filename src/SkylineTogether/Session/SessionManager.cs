using System;
using System.Collections.Generic;
using System.Globalization;
using Colossal.Serialization.Entities;
using Game;
using Game.PSI;
using Game.SceneFlow;
using Game.Simulation;
using SkylineTogether.Core;
using SkylineTogether.Core.Diagnostics;
using SkylineTogether.Core.Session;
using SkylineTogether.Diagnostics;
using SkylineTogether.Net;
using SkylineTogether.Settings;
using SkylineTogether.Sync;
using Steamworks;
using Unity.Entities;
using ILog = SkylineTogether.Core.ILog;

namespace SkylineTogether.Session
{
    public enum SessionMode
    {
        None,
        Hosting,
        Joining,
        Client,
        /// <summary>A client session ended unexpectedly in the host's city: rejoining or waiting for the player's choice.</summary>
        Recovering
    }

    /// <summary>
    /// The mod's session controller (main thread): starts hosting or joining from the settings, ticks the session every
    /// frame from <see cref="SessionDriver"/>, ends it when the player leaves the city, and keeps what the UI shows
    /// (status, players, chat). Replication plugs in through <see cref="HostStarted"/> / <see cref="ClientStarted"/>.
    /// <para>
    /// Recovery (ARCH §7.8, §12.2): when a client's session ends unexpectedly while it is in the host's city, the city
    /// is held (paused, no autosave) instead of quietly turning into a single-player game; a lost connection is retried
    /// automatically with backoff through the normal join path (fresh snapshot), everything else asks the player:
    /// reconnect, return to the main menu, or keep the city as an offline copy (<see cref="RejoinController"/>).
    /// </para>
    /// </summary>
    public sealed class SessionManager
    {
        public const int VirtualPort = 0;
        public const int DefaultDirectPort = 27015;
        private const int MaxChatLines = 100;

        private static readonly System.Diagnostics.Stopwatch SteamClock = System.Diagnostics.Stopwatch.StartNew();
        private static bool _steamAvailable;
        private static double _steamCheckedAt = double.NegativeInfinity;

        private readonly ILog _log;
        private readonly SkylineTogetherSettings _settings;
        private readonly MainThreadQueue _main;
        private readonly GameInfoProvider _info = new GameInfoProvider();
        private readonly SnapshotService _snapshots;
        private readonly SnapshotLoader _loader;
        private readonly Core.Testing.StopwatchClock _clock = new Core.Testing.StopwatchClock();
        private readonly List<ChatEntry> _chat = new List<ChatEntry>();
        private readonly RejoinController _rejoin = new RejoinController();
        private int _chatVersion;
        private SpeedState _speedRequest;
        private bool _hasSpeedRequest;
        private int _playersVersion;
        private int _recoveryVersion;
        private Endpoint _lastEndpoint;
        private string _lastPassword = "";
        private IReadOnlyList<PlayerInfo> _lastPlayers = new PlayerInfo[0];
        private ComponentSystemBase _heldAutoSave;
        private bool _holdingCity;
        private bool _disposed;

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
            Guard.CriticalTripped += OnCriticalTripped;
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
                if (_rejoin.Recovering) return SessionMode.Recovering;
                if (Client == null) return SessionMode.None;
                return Client.State == ClientState.Live ? SessionMode.Client : SessionMode.Joining;
            }
        }

        public bool InSession => Host != null || Client != null || _rejoin.Recovering;

        /// <summary>The recovery state (automatic rejoin, the player's choice).</summary>
        public RejoinController Recovery => _rejoin;

        /// <summary>Bumped when the recovery state changes (UI refresh key).</summary>
        public int RecoveryVersion => _recoveryVersion;

        /// <summary>True while the local city must stay paused: a client session ended unexpectedly and recovery runs.</summary>
        public bool HoldCity => _rejoin.Recovering;

        /// <summary>The players of the session that ended (shown while recovering).</summary>
        public IReadOnlyList<PlayerInfo> LastPlayers => _lastPlayers;

        public bool CanHost => !InSession && SteamAvailable && BlockingReason() == null && GameManager.instance != null && GameManager.instance.gameMode == GameMode.Game && !GameManager.instance.isGameLoading;

        public bool CanJoin => !InSession && SteamAvailable && BlockingReason() == null && !string.IsNullOrWhiteSpace(_settings.JoinTarget) && (GameManager.instance == null || !GameManager.instance.isGameLoading);

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

        /// <summary>Steam is running with a logged-in user (checked at most once a second; the UI asks every frame).</summary>
        public static bool SteamAvailable
        {
            get
            {
                double now = SteamClock.Elapsed.TotalSeconds;
                if (now - _steamCheckedAt < 1.0) return _steamAvailable;
                _steamCheckedAt = now;
                try
                {
                    _steamAvailable = SteamAPI.IsSteamRunning() && SteamUser.GetSteamID().IsValid();
                }
                catch (Exception)
                {
                    _steamAvailable = false;
                }
                return _steamAvailable;
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

        /// <summary>
        /// The first critical feature (other than Steam, which <see cref="SteamAvailable"/> covers) whose self-test failed
        /// or that turned itself off: hosting and joining are refused with this reason (ARCH §12.2 "critical probes refuse
        /// hosting and joining"). Null when everything needed is available.
        /// </summary>
        public static string BlockingReason()
        {
            IReadOnlyList<FeatureHealth> features = Features.Registry.Features; // indexed: no enumerator allocation per frame
            for (int i = 0; i < features.Count; i++)
            {
                FeatureHealth f = features[i];
                if (f.Critical && f != Features.Steam && f.ProbeFailed) return f.Title + ": " + f.UnavailableReason;
            }
            return null;
        }

        // ---------------------------------------------------------------- actions

        public void StartHosting()
        {
            if (!CanHost)
            {
                string why;
                if (InSession) why = "Already in a session. Leave it first.";
                else if (!SteamAvailable) why = "Steam is not available. Start the game through Steam and log in.";
                else if (BlockingReason() != null) why = "Skyline Together cannot host in this game version: " + BlockingReason() + ". Please send us the diagnostics file.";
                else why = "Load or start a city first, then host it.";
                Notify(why);
                return;
            }
            SessionHost created = null;
            try
            {
                Features.Registry.RearmAll();
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
                if (!RaiseStarted(HostStarted, host, "host"))
                {
                    StopHost("The host's game could not start the session (see its diagnostics).");
                    Notify("Could not start hosting: part of Skyline Together failed to start. Please send us the diagnostics file.");
                    return;
                }
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
                if (_rejoin.Recovering) why = "Still recovering the last session. Use Reconnect, or choose to stay offline first.";
                else if (InSession) why = "Already in a session. Leave it first.";
                else if (!SteamAvailable) why = "Steam is not available. Start the game through Steam and log in.";
                else if (BlockingReason() != null) why = "Skyline Together cannot join in this game version: " + BlockingReason() + ". Please send us the diagnostics file.";
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
            _rejoin.Reset();
            BeginJoin(endpoint, _settings.JoinPassword ?? "", rejoin: false);
        }

        /// <summary>
        /// The one join path (first join and every rejoin): a new transport and client, the replication layer attaches
        /// through <see cref="ClientStarted"/>, then connect. A rejoin keeps the chat and says which attempt this is.
        /// </summary>
        private void BeginJoin(Endpoint endpoint, string password, bool rejoin)
        {
            _lastEndpoint = endpoint;
            _lastPassword = password ?? "";
            try
            {
                Features.Registry.RearmAll();
                var transport = new SteamSocketsTransport(endpoint.SteamId != 0 ? TransportKind.SteamP2P : TransportKind.SteamIp, _log);
                var client = new SessionClient(transport, _info, _loader, new ClientOptions { PlayerName = PlayerName, SteamId = LocalSteamId, Password = _lastPassword }, _clock, _log);
                client.ChatReceived += AddChat;
                client.PlayersChanged += () => _playersVersion++;
                client.ToastReceived += Notify;
                client.StateChanged += () => OnClientStateChanged(client);
                Client = client;
                LastEndReason = null;
                if (rejoin)
                {
                    AddChat(new ChatEntry { IsSystem = true, Text = "Reconnecting (attempt " + _rejoin.Attempt + " of " + _rejoin.MaxAttempts + ")…" });
                }
                else
                {
                    _chat.Clear();
                    _chatVersion++;
                }
                _recoveryVersion++;
                if (!RaiseStarted(ClientStarted, client, "client"))
                {
                    client.Abort("Part of Skyline Together failed to start the session on this game.");
                    EndClient();
                    return;
                }
                client.Connect(endpoint);
            }
            catch (Exception ex)
            {
                _log.Error(LogCategory.Session, rejoin ? "rejoining failed" : "joining failed", ex);
                if (!rejoin) Notify("Could not join: " + ex.Message);
                // review fix: listeners saw ClientStarted, so they must also see SessionEnded (and the loader is reset)
                if (Client != null)
                {
                    SessionClient failed = Client;
                    Client = null;
                    failed.Dispose();
                    _loader.Dispose();
                    _playersVersion++;
                    RaiseSessionEnded();
                }
                // an attempt that could not even start counts as unreachable (the next one follows the backoff)
                if (rejoin) OnRecoveryEnded(ClientEndKind.Unreachable, "Could not start the connection: " + ex.Message, false);
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
            if (_rejoin.Recovering) StayOffline();
        }

        /// <summary>Recovery: reconnect now (from the waiting or the choice state).</summary>
        public void Reconnect()
        {
            if (!_rejoin.Recovering || _lastEndpoint == null) return;
            if (Client != null) return; // an attempt is already running
            _rejoin.ReconnectNow(_clock.Seconds);
            _recoveryVersion++;
        }

        /// <summary>Recovery: stop a running attempt and ask the player.</summary>
        public void CancelReconnect()
        {
            if (!_rejoin.Recovering) return;
            if (Client != null)
            {
                Client.Leave();
                EndClient();
            }
            _rejoin.AskPlayer("Reconnecting was cancelled.");
            HoldCityOn();
            _recoveryVersion++;
        }

        /// <summary>Recovery: keep the city as an offline copy (single player from here on).</summary>
        public void StayOffline()
        {
            if (Client != null)
            {
                Client.Leave();
                EndClient();
            }
            if (!_rejoin.Recovering && !_holdingCity) return;
            _rejoin.Reset();
            ReleaseCity();
            _recoveryVersion++;
            AddChat(new ChatEntry { IsSystem = true, Text = "You are playing an offline copy of the host's city. Nothing you do here reaches the session." });
            Notify("You are playing an offline copy of the host's city. Saving it creates your own copy.");
        }

        /// <summary>Recovery (or any time): leave the session and return to the main menu.</summary>
        public void ReturnToMainMenu()
        {
            if (Host != null) StopHost("The host ended the session.");
            if (Client != null)
            {
                Client.Leave();
                EndClient();
            }
            _rejoin.Reset();
            ReleaseCity();
            _recoveryVersion++;
            try
            {
                World world = World.DefaultGameObjectInjectionWorld;
                Game.City.CityConfigurationSystem city = world?.GetExistingSystemManaged<Game.City.CityConfigurationSystem>();
                if (city != null)
                {
                    city.overrideLoadedOptions = false;
                    city.overrideThemeName = null;
                }
                System.Threading.Tasks.Task<bool> task = GameManager.instance.MainMenu();
                task.ContinueWith(t =>
                {
                    if (t.IsFaulted) _log.Error(LogCategory.Session, "returning to the main menu failed", t.Exception);
                    else if (!t.Result) _log.Warn(LogCategory.Session, "the game did not return to the main menu");
                });
            }
            catch (Exception ex)
            {
                _log.Error(LogCategory.Session, "returning to the main menu failed", ex);
                Notify("Could not return to the main menu: " + ex.Message);
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

        /// <summary>
        /// A save started on this game (seen right before <c>SaveGameSystem</c> runs). On a client it saves a local copy
        /// of the host's city: allowed (the game offers no way to refuse it without patching), but the player is told
        /// once per save what it is. The host's own saves and autosaves are normal (net ids are not written into them).
        /// </summary>
        public void OnLocalSave()
        {
            if (Client == null || Host != null) return;
            _log.Info(LogCategory.Session, "the player saved the game while connected (" + Client.State + ")");
            Notify("You saved a copy of the host's city on your computer. It is your own copy: the session goes on in the host's city, and the host keeps the real save.");
        }

        // ---------------------------------------------------------------- frame

        public void Tick()
        {
            if (_disposed) return;
            _main.Drain();
            if (!Features.Session.IsAvailable && Host == null && Client == null && !_rejoin.Recovering) return;
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
                if (Client == null && _rejoin.Recovering)
                {
                    GameManager game = GameManager.instance;
                    bool canStart = game != null && !game.isGameLoading && !_loader.IsLoading;
                    if (_rejoin.Tick(_clock.Seconds, canStart))
                    {
                        _recoveryVersion++;
                        BeginJoin(_lastEndpoint, _lastPassword, rejoin: true);
                    }
                }
            }
            catch (Exception ex)
            {
                Guard.Report(Features.Session, "session tick", ex);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            Leave();
            _rejoin.Reset();
            ReleaseCity();
            _disposed = true;
            Guard.CriticalTripped -= OnCriticalTripped;
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
            if (client.State != ClientState.Live) return;
            bool rejoined = _rejoin.Recovering;
            _rejoin.OnLive();
            _recoveryVersion++;
            // the rejoined session's own gate and clock take over from the recovery hold (SystemSwitch counts holders)
            ReleaseCity();
            if (rejoined)
            {
                AddChat(new ChatEntry { IsSystem = true, Text = "Reconnected to \"" + client.Session?.SessionName + "\"." });
                Notify("Reconnected to \"" + client.Session?.SessionName + "\".");
            }
            else
            {
                Notify("Joined \"" + client.Session?.SessionName + "\". Have fun!");
            }
        }

        private void EndClient()
        {
            SessionClient client = Client;
            if (client == null) return;
            Client = null;
            ClientEndKind kind = client.EndKind;
            string reason = client.EndReason;
            bool entered = client.EnteredCity;
            if (client.Players != null && client.Players.Count > 0) _lastPlayers = new List<PlayerInfo>(client.Players);
            LastEndReason = reason;
            try
            {
                client.Dispose();
            }
            catch (Exception ex)
            {
                _log.Warn(LogCategory.Session, "disposing the client failed", ex);
            }
            _loader.Dispose();
            _playersVersion++;
            // the follower state (gate, clock, movers, icon filter, bindings) is reset for every end; a rejoin builds it
            // again from a fresh snapshot
            RaiseSessionEnded();
            OnRecoveryEnded(kind, reason, entered);
        }

        /// <summary>A client session ended: decides between recovery (hold, retry, ask) and a normal end.</summary>
        private void OnRecoveryEnded(ClientEndKind kind, string reason, bool entered)
        {
            bool wasRecovering = _rejoin.Recovering;
            bool recovering = _rejoin.OnClientEnded(kind, reason, entered, _clock.Seconds);
            _recoveryVersion++;
            if (!recovering)
            {
                if (wasRecovering) ReleaseCity();
                if (!string.IsNullOrEmpty(reason)) AddChat(new ChatEntry { IsSystem = true, Text = reason });
                Notify(reason ?? "Disconnected.");
                return;
            }
            HoldCityOn();
            if (!string.IsNullOrEmpty(reason)) AddChat(new ChatEntry { IsSystem = true, Text = reason });
            switch (_rejoin.Phase)
            {
                case RejoinPhase.Waiting:
                    if (!wasRecovering)
                    {
                        Notify(reason + " Reconnecting automatically; the city is paused until then.");
                    }
                    break;
                case RejoinPhase.NeedsChoice:
                    if (kind == ClientEndKind.HostEnded)
                    {
                        Notify("The session has ended: " + reason + " Open the Skyline Together panel to return to the main menu or to keep an offline copy.");
                    }
                    else if (kind == ClientEndKind.LocalFault)
                    {
                        Notify(reason + " Open the Skyline Together panel to reconnect.");
                        DiagnosticsDump.Write();
                    }
                    else
                    {
                        Notify((wasRecovering ? "Could not reconnect: " : "") + reason + " Open the Skyline Together panel to choose what to do.");
                    }
                    break;
            }
        }

        /// <summary>The city stays paused and does not autosave while it is the host's city without a session.</summary>
        private void HoldCityOn()
        {
            if (_holdingCity) return;
            _holdingCity = true;
            try
            {
                World world = World.DefaultGameObjectInjectionWorld;
                ComponentSystemBase autoSave = world?.GetExistingSystemManaged<AutoSaveSystem>();
                if (autoSave != null)
                {
                    SystemSwitch.TurnOff(autoSave);
                    _heldAutoSave = autoSave;
                }
                SimulationSystem simulation = world?.GetExistingSystemManaged<SimulationSystem>();
                if (simulation != null) simulation.selectedSpeed = 0f;
            }
            catch (Exception ex)
            {
                Guard.Report(Features.Session, "holding the city", ex);
            }
            _log.Info(LogCategory.Session, "recovery: the host's city is held (paused, no autosave)");
        }

        private void ReleaseCity()
        {
            if (!_holdingCity) return;
            _holdingCity = false;
            ComponentSystemBase autoSave = _heldAutoSave;
            _heldAutoSave = null;
            try
            {
                if (autoSave != null) SystemSwitch.Restore(autoSave, true);
            }
            catch (Exception)
            {
                // the world may be gone
            }
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
        /// A critical feature turned itself off after repeated errors (<see cref="Guard"/>): the session cannot go on
        /// safely. The host ends it for everyone; a client ends its own and is offered a rejoin (fresh snapshot). Runs at
        /// the next driver update, never inside the failing call.
        /// </summary>
        private void OnCriticalTripped(FeatureHealth feature)
        {
            _main.Post(() =>
            {
                if (Host != null)
                {
                    _log.Error(LogCategory.Session, "critical failure in " + feature.Title + "; ending the session");
                    DiagnosticsDump.Write();
                    StopHost("The host's game hit an error (" + feature.Title + "), so the session ended. The host can start it again.");
                    Notify("Skyline Together hit repeated errors in " + feature.Title + " and ended the session. Diagnostics were written; please send them to us.");
                }
                else if (Client != null)
                {
                    _log.Error(LogCategory.Session, "critical failure in " + feature.Title + "; leaving the session");
                    Client.Abort("This game hit repeated errors in " + feature.Title + ", so it left the session. Reconnect to get a fresh copy of the city.");
                    EndClient();
                }
                else if (feature != Features.Session)
                {
                    Notify("Skyline Together hit repeated errors in " + feature.Title + ". Please write diagnostics and send them to us.");
                }
            });
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
                    Guard.Report(Features.Session, "a session-end listener (" + d.Method.DeclaringType?.Name + "." + d.Method.Name + ")", ex);
                }
            }
        }

        /// <summary>
        /// Every start listener runs even when one throws. A failing replication listener (critical) makes the start
        /// fail: the caller ends the session at once instead of running it half attached.
        /// </summary>
        private bool RaiseStarted<T>(Action<T> handlers, T session, string what)
        {
            if (handlers == null) return true;
            bool ok = true;
            foreach (Delegate d in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<T>)d)(session);
                }
                catch (Exception ex)
                {
                    Type owner = d.Method.DeclaringType;
                    string name = owner?.Name + "." + d.Method.Name;
                    bool critical = owner == typeof(SyncController) || (owner != null && owner.DeclaringType == typeof(SyncController));
                    Guard.Report(critical ? Features.Replication : Features.Session, "a " + what + "-start listener (" + name + ")", ex);
                    if (critical) ok = false;
                }
            }
            return ok;
        }

        private void OnGamePreload(Purpose purpose, GameMode mode)
        {
            try
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
                if (_rejoin.Recovering || _holdingCity)
                {
                    // the player loads another city (or the main menu) instead of recovering: recovery is over
                    _log.Info(LogCategory.Session, "another city is loading (" + purpose + ", " + mode + "); recovery stopped");
                    if (Client != null)
                    {
                        Client.Leave();
                        EndClient();
                    }
                    _rejoin.Reset();
                    ReleaseCity();
                    _recoveryVersion++;
                }
            }
            catch (Exception ex)
            {
                Guard.Report(Features.Session, "game preload handler", ex);
            }
        }

        private void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            try
            {
                _playersVersion++;
                // the second probe stage: world-dependent checks once a city exists (ARCH §13.4)
                if (mode == GameMode.Game) Probes.RunCityLoaded(_log);
            }
            catch (Exception ex)
            {
                Guard.Report(Features.Session, "game loaded handler", ex);
            }
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
