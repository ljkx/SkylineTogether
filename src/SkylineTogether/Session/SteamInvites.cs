using System;
using System.Collections.Generic;
using System.Globalization;
using Colossal.PSI.Common;
using Game.SceneFlow;
using Game.UI.Localization;
using SkylineTogether.Core;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Diagnostics;
using SkylineTogether.Settings;
using Steamworks;
using ILog = SkylineTogether.Core.ILog;

namespace SkylineTogether.Session
{
    /// <summary>
    /// Steam friends integration (w6/invites). The session itself always runs over the P2P transport to the host's Steam
    /// ID; Steam only carries the invitation:
    /// <list type="bullet">
    /// <item>While hosting, a friends-only Steam lobby (member limit = max players) carries <see cref="LobbyInfo"/>; it is
    /// left when hosting stops. A client in a live session joins the host's lobby when it can find it, so it can invite
    /// too. Steam's invite dialog and "Join game" need such a lobby.</item>
    /// <item>Rich presence: <c>connect</c> (<see cref="InviteLink"/>; the friends list's "Join game"), <c>status</c> and the
    /// player group keys. The game's own <c>steam_display</c> stays untouched (its tokens belong to the game's Steam
    /// localization).</item>
    /// <item>Invites: the overlay dialog (<see cref="OpenInviteDialog"/>, with a check that the overlay exists and really
    /// opened) or a direct invite from our own friends list (<see cref="InviteFriend"/>, works without the overlay).</item>
    /// <item>Receiving: <see cref="GameLobbyJoinRequested_t"/>, <see cref="GameRichPresenceJoinRequested_t"/> and the
    /// command line (<c>+connect_lobby</c> or our connect string). In the main menu the join starts by itself; while a
    /// city is open a toast asks first (joining replaces the city).</item>
    /// </list>
    /// Steam callbacks arrive inside <c>SteamAPI.RunCallbacks</c> (the game's platform update, main thread); every entry
    /// point is guarded (<see cref="Features.Invites"/>).
    /// </summary>
    public sealed class SteamInvites : IDisposable
    {
        private const string ConnectKey = "connect";
        private const string StatusKey = "status";
        private const string GroupKey = "steam_player_group";
        private const string GroupSizeKey = "steam_player_group_size";
        private const string ConfirmToastId = "SkylineTogether.invite";

        private const double PresenceRefreshSeconds = 60;
        private const double LobbyDataSeconds = 2;
        private const double LobbyRetrySeconds = 30;
        private const int MaxLobbyAttempts = 3;
        private const double OverlayWatchSeconds = 3;
        private const double OverlayCheckSeconds = 1;
        private const double ResolveDataSeconds = 4;
        private const double ResolveTimeoutSeconds = 15;
        private const double ConfirmSeconds = 30;
        private const double InviteCooldownSeconds = 3;

        private enum EnterPurpose
        {
            None,
            Resolve,
            Member,
        }

        private readonly SessionManager _sessions;
        private readonly SkylineTogetherSettings _settings;
        private readonly ILog _log;

        // callbacks and call results: kept for the mod's lifetime and disposed in Dispose
        private Callback<GameRichPresenceJoinRequested_t> _richJoinRequested;
        private Callback<GameLobbyJoinRequested_t> _lobbyJoinRequested;
        private Callback<GameOverlayActivated_t> _overlayActivated;
        private Callback<LobbyDataUpdate_t> _lobbyDataUpdated;
        private CallResult<LobbyCreated_t> _lobbyCreated;
        private CallResult<LobbyEnter_t> _lobbyEntered;

        // the lobby this player is in (own while hosting, the host's as a client)
        private ulong _lobby;
        private bool _lobbyOwned;
        private bool _creating;
        private int _createAttempts;
        private double _createRetryAt;
        private string _lobbyDataSignature;
        private double _lobbyDataAt = double.NegativeInfinity;
        private int _lobbyLimit;
        private ulong _clientLobbyCandidate;
        private bool _clientLobbyTried;
        private EnterPurpose _entering;
        private ulong _enteringLobby;

        // an invite whose lobby is being read
        private ulong _resolveLobby;
        private string _resolveFriend;
        private double _resolveStarted;
        private bool _resolveEntering;

        // an invite waiting to be joined
        private ulong _pendingHost;
        private string _pendingFriend;
        private ulong _pendingLobby;
        private bool _confirmShown;
        private double _confirmUntil;
        private ulong _coldStartLobby;

        // overlay
        private double _overlayWatchUntil = -1;
        private bool _overlayNoResponse;
        private bool _overlayEnabled;
        private double _overlayCheckedAt = double.NegativeInfinity;

        // rich presence as last set
        private readonly Dictionary<string, string> _presence = new Dictionary<string, string>();
        private double _presenceAt = double.NegativeInfinity;

        private readonly Dictionary<ulong, double> _invitedAt = new Dictionary<ulong, double>();

        public SteamInvites(SessionManager sessions, SkylineTogetherSettings settings, ILog log)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _settings = settings;
            _log = log;
            Friends = new SteamFriendList(sessions, log);
            try
            {
                _richJoinRequested = Callback<GameRichPresenceJoinRequested_t>.Create(OnRichPresenceJoinRequested);
                _lobbyJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnLobbyJoinRequested);
                _overlayActivated = Callback<GameOverlayActivated_t>.Create(OnOverlayActivated);
                _lobbyDataUpdated = Callback<LobbyDataUpdate_t>.Create(OnLobbyDataUpdated);
                _lobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
                _lobbyEntered = CallResult<LobbyEnter_t>.Create(OnLobbyEntered);
            }
            catch (Exception ex)
            {
                _log.Warn(LogCategory.Net, "Steam invites are unavailable", ex);
                Guard.Disable(Features.Invites, "the Steam callbacks could not be registered: " + ex.Message);
            }
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                if (InviteLink.TryParseArgs(args, out ulong host))
                {
                    _pendingHost = host;
                    _log.Info(LogCategory.Session, "started from a Steam invite; joining " + Id(host) + " once the menu is ready");
                }
                else if (InviteLink.TryParseLobbyArgs(args, out ulong lobby))
                {
                    _coldStartLobby = lobby;
                    _log.Info(LogCategory.Session, "started from a Steam lobby invite (lobby " + Id(lobby) + "); reading it once Steam is ready");
                }
            }
            catch (Exception ex)
            {
                _log.Warn(LogCategory.Session, "reading the command line failed", ex);
            }
            sessions.SessionEnded += () => Guard.RunAlways(Features.Invites, "Steam invites at the session end", OnSessionEnded);
        }

        /// <summary>The invite list of the panel.</summary>
        public SteamFriendList Friends { get; }

        /// <summary>The Steam lobby this player is in for the current session (0 when none).</summary>
        public ulong LobbyId => _lobby;

        /// <summary>The Steam overlay is on (the invite dialog needs it).</summary>
        public static bool OverlayEnabled
        {
            get
            {
                try
                {
                    return SteamUtils.IsOverlayEnabled();
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// The overlay invite dialog can be used: Steam runs, the overlay is on, and the last dialog really opened (a dialog
        /// that never appeared turns this off until the overlay shows up again). Checked at most once a second.
        /// </summary>
        public bool OverlayAvailable
        {
            get
            {
                double now = _sessions.Clock.Seconds;
                if (now - _overlayCheckedAt >= OverlayCheckSeconds)
                {
                    _overlayCheckedAt = now;
                    _overlayEnabled = SessionManager.SteamAvailable && OverlayEnabled;
                }
                return _overlayEnabled && !_overlayNoResponse;
            }
        }

        /// <summary>The Steam ID friends join: the local player while hosting, the host while playing as a client; 0 otherwise.</summary>
        public ulong InviteTarget
        {
            get
            {
                if (_sessions.SoloHost || _sessions.IsReplay) return 0; // solo testing: nobody can join a test session or a replay
                if (_sessions.Host != null) return _sessions.LocalSteamId;
                SessionClient client = _sessions.Client;
                if (client == null || client.State != ClientState.Live) return 0;
                foreach (PlayerInfo p in client.Players)
                {
                    if (p.Id.IsHost) return p.SteamId;
                }
                return 0;
            }
        }

        public bool CanInvite => SessionManager.SteamAvailable && InviteTarget != 0;

        // ------------------------------------------------------------------------------------------------ invites

        /// <summary>
        /// Opens the Steam overlay's invite dialog for the current session (the lobby once it exists, else the connect
        /// string). Without the overlay, or when the dialog does not appear, a toast points to the friends list and the
        /// Steam ID instead.
        /// </summary>
        public void OpenInviteDialog()
        {
            ulong target = InviteTarget;
            if (target == 0)
            {
                SessionManager.Notify(LocaleSource.Resolve("st.toast.inviteNoSession"), ProgressState.Warning);
                return;
            }
            _overlayCheckedAt = double.NegativeInfinity;
            if (!SessionManager.SteamAvailable || !OverlayEnabled)
            {
                _log.Warn(LogCategory.Net, "the Steam overlay is not available (turned off, or the game was started without it); the invite dialog cannot open");
                SessionManager.Notify(LocaleSource.Resolve("st.toast.overlayUnavailable", "ID", Id(target)), ProgressState.Warning, null, 12f);
                return;
            }
            try
            {
                if (_lobby != 0)
                {
                    SteamFriends.ActivateGameOverlayInviteDialog(new CSteamID(_lobby));
                    _log.Info(LogCategory.Net, "opened the Steam invite dialog for lobby " + Id(_lobby));
                }
                else
                {
                    SteamFriends.ActivateGameOverlayInviteDialogConnectString(InviteLink.Format(target));
                    _log.Info(LogCategory.Net, "opened the Steam invite dialog with the connect string (no lobby yet)");
                }
                _overlayWatchUntil = _sessions.Clock.Seconds + OverlayWatchSeconds;
            }
            catch (Exception ex)
            {
                _log.Warn(LogCategory.Net, "opening the Steam invite dialog failed", ex);
                SessionManager.Notify(LocaleSource.Resolve("st.toast.overlayUnavailable", "ID", Id(target)), ProgressState.Warning, null, 12f);
            }
        }

        /// <summary>
        /// Invites one friend directly (no overlay needed): a Steam game invite with the connect string, or a lobby invite
        /// when that is refused. The friend accepts in their Steam chat.
        /// </summary>
        public void InviteFriend(string steamId)
        {
            if (!FriendList.TryParseId(steamId, out ulong id))
            {
                _log.Warn(LogCategory.Net, "ignored an invite for an invalid Steam ID");
                return;
            }
            ulong target = InviteTarget;
            if (target == 0)
            {
                SessionManager.Notify(LocaleSource.Resolve("st.toast.inviteNoSession"), ProgressState.Warning);
                return;
            }
            if (id == target || id == _sessions.LocalSteamId) return;
            double now = _sessions.Clock.Seconds;
            if (_invitedAt.TryGetValue(id, out double at) && now - at < InviteCooldownSeconds) return; // a double click
            _invitedAt[id] = now;
            var friend = new CSteamID(id);
            string name = FriendName(friend);
            bool sent = false;
            string how = "";
            try
            {
                sent = SteamFriends.InviteUserToGame(friend, InviteLink.Format(target));
                how = "game invite";
            }
            catch (Exception ex)
            {
                _log.Warn(LogCategory.Net, "the Steam game invite failed", ex);
            }
            if (!sent && _lobby != 0)
            {
                try
                {
                    sent = SteamMatchmaking.InviteUserToLobby(new CSteamID(_lobby), friend);
                    how = "lobby invite";
                }
                catch (Exception ex)
                {
                    _log.Warn(LogCategory.Net, "the Steam lobby invite failed", ex);
                }
            }
            if (sent)
            {
                _log.Info(LogCategory.Net, "sent a Steam " + how + " to " + Id(id));
                SessionManager.Notify(LocaleSource.Resolve("st.toast.inviteSent", "NAME", name), ProgressState.Complete, null, 4f);
            }
            else
            {
                _log.Warn(LogCategory.Net, "Steam refused the invite to " + Id(id));
                SessionManager.Notify(LocaleSource.Resolve("st.toast.inviteFailed", "NAME", name, "ID", Id(target)), ProgressState.Warning, null, 10f);
            }
        }

        /// <summary>
        /// w8/security: the "friends and invited only" join policy (<c>HostOptions.IsFriendOrInvited</c>): true for the
        /// host's Steam friends (the same immediate-friends list the panel shows) and for players this host invited from
        /// the panel during this session. Friends are read from Steam at most every 10 s.
        /// </summary>
        public bool IsFriendOrInvited(ulong steamId)
        {
            if (steamId == 0) return false;
            if (_invitedAt.ContainsKey(steamId)) return true;
            double now = _sessions.Clock.Seconds;
            if (now - _friendsReadAt > FriendsCacheSeconds || now < _friendsReadAt)
            {
                _friendsReadAt = now;
                _friendIds.Clear();
                try
                {
                    int count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
                    for (int i = 0; i < count; i++)
                    {
                        CSteamID id = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                        if (id.IsValid()) _friendIds.Add(id.m_SteamID);
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn(LogCategory.Net, "reading the Steam friends list failed", ex);
                }
            }
            return _friendIds.Contains(steamId);
        }

        private const double FriendsCacheSeconds = 10;
        private readonly HashSet<ulong> _friendIds = new HashSet<ulong>();
        private double _friendsReadAt = double.NegativeInfinity;

        // ---------------------------------------------------------------------------------------------- per frame

        /// <summary>Once per frame: rich presence, the lobby, invites being read, the overlay check and a pending join.</summary>
        public void Tick()
        {
            if (!Features.Invites.IsAvailable) return;
            double now = _sessions.Clock.Seconds;
            Guard.Run(Features.Invites, "Steam rich presence", () => UpdatePresence(now));
            Guard.Run(Features.Invites, "Steam lobby", () => UpdateLobby(now));
            Guard.Run(Features.Invites, "reading a Steam invite", () => UpdateResolve(now));
            if (_overlayWatchUntil > 0 && now > _overlayWatchUntil)
            {
                _overlayWatchUntil = -1;
                _overlayNoResponse = true;
                ulong target = InviteTarget;
                _log.Warn(LogCategory.Net, "the Steam invite dialog did not open (no overlay activation within " + OverlayWatchSeconds + " s); the overlay is probably not hooked into the game");
                SessionManager.Notify(LocaleSource.Resolve("st.toast.overlayNoResponse", "ID", Id(target != 0 ? target : _sessions.LocalSteamId)), ProgressState.Warning, null, 12f);
            }
            Guard.Run(Features.Invites, "joining from a Steam invite", () => UpdatePending(now));
        }

        public void Dispose()
        {
            try
            {
                LeaveLobby();
                ClearPresence();
            }
            catch (Exception)
            {
                // Steam already shut down
            }
            DisposeQuietly(_richJoinRequested);
            DisposeQuietly(_lobbyJoinRequested);
            DisposeQuietly(_overlayActivated);
            DisposeQuietly(_lobbyDataUpdated);
            DisposeQuietly(_lobbyCreated);
            DisposeQuietly(_lobbyEntered);
            _richJoinRequested = null;
            _lobbyJoinRequested = null;
            _overlayActivated = null;
            _lobbyDataUpdated = null;
            _lobbyCreated = null;
            _lobbyEntered = null;
            Friends.Dispose();
        }

        private static void DisposeQuietly(IDisposable d)
        {
            try
            {
                d?.Dispose();
            }
            catch (Exception)
            {
                // Steam already shut down
            }
        }

        private void OnSessionEnded()
        {
            LeaveLobby();
            ClearPresence();
            _createAttempts = 0;
            _createRetryAt = 0;
            _clientLobbyTried = false;
            _clientLobbyCandidate = 0;
            _overlayWatchUntil = -1;
            _invitedAt.Clear();
            Friends.RequestRefresh();
        }

        // ---------------------------------------------------------------------------------------- rich presence

        private void UpdatePresence(double now)
        {
            ulong target = InviteTarget;
            if (target == 0)
            {
                if (_presence.Count > 0) ClearPresence();
                return;
            }
            int players = 0;
            string status;
            if (_sessions.Host != null)
            {
                SessionHost host = _sessions.Host;
                players = host.GetPlayers().Count;
                status = "Skyline Together: hosting \"" + host.Options.SessionName + "\" (" + players.ToString(CultureInfo.InvariantCulture) + "/" + host.Options.MaxPlayers.ToString(CultureInfo.InvariantCulture) + ")";
            }
            else
            {
                SessionClient client = _sessions.Client;
                players = client?.Players.Count ?? 0;
                string session = client?.Session?.SessionName;
                status = "Skyline Together: playing in " + (string.IsNullOrEmpty(session) ? "a shared city" : "\"" + session + "\"") + " (" + players.ToString(CultureInfo.InvariantCulture) + " players)";
            }
            bool refresh = now - _presenceAt > PresenceRefreshSeconds;
            if (refresh) _presenceAt = now;
            SetPresence(ConnectKey, InviteLink.Format(target), refresh);
            SetPresence(StatusKey, status, refresh);
            SetPresence(GroupKey, Id(target), refresh);
            SetPresence(GroupSizeKey, Math.Max(1, players).ToString(CultureInfo.InvariantCulture), refresh);
        }

        private void SetPresence(string key, string value, bool force)
        {
            if (!force && _presence.TryGetValue(key, out string old) && old == value) return;
            _presence[key] = value;
            try
            {
                SteamFriends.SetRichPresence(key, value);
            }
            catch (Exception)
            {
                // Steam not running
            }
        }

        private void ClearPresence()
        {
            if (_presence.Count == 0) return;
            foreach (string key in new List<string>(_presence.Keys))
            {
                try
                {
                    SteamFriends.SetRichPresence(key, ""); // an empty value removes the key
                }
                catch (Exception)
                {
                    // Steam not running
                }
            }
            _presence.Clear();
            _presenceAt = double.NegativeInfinity;
        }

        // ------------------------------------------------------------------------------------------------- lobby

        private bool ShouldOwnLobby => _sessions.Host != null && !_sessions.SoloHost && !_sessions.IsReplay && SessionManager.SteamAvailable;

        private void UpdateLobby(double now)
        {
            if (ShouldOwnLobby)
            {
                if (_lobby != 0 && !_lobbyOwned) LeaveLobby(); // a client's membership from an earlier session
                if (_lobby == 0)
                {
                    if (!_creating && _createAttempts < MaxLobbyAttempts && now >= _createRetryAt) CreateLobby(now);
                    return;
                }
                if (now - _lobbyDataAt >= LobbyDataSeconds)
                {
                    _lobbyDataAt = now;
                    WriteLobbyData();
                }
                return;
            }
            if (_sessions.Host == null && _sessions.Client != null)
            {
                if (_lobby != 0 && _lobbyOwned) LeaveLobby();
                ulong host = InviteTarget; // non-zero once live
                if (host != 0 && _lobby == 0 && !_clientLobbyTried && _entering == EnterPurpose.None) JoinHostLobby(host);
                return;
            }
            if (_lobby != 0 && !_sessions.InSession) LeaveLobby();
        }

        private void CreateLobby(double now)
        {
            int max = Math.Max(2, Math.Min(16, _sessions.Host.Options.MaxPlayers));
            _createAttempts++;
            _createRetryAt = now + LobbyRetrySeconds;
            SteamAPICall_t call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, max);
            if (call == SteamAPICall_t.Invalid)
            {
                _log.Warn(LogCategory.Net, "Steam refused to create a lobby (attempt " + _createAttempts + "); invites use the connect string");
                return;
            }
            _creating = true;
            _lobbyCreated.Set(call);
            _log.Info(LogCategory.Net, "creating a friends-only Steam lobby for " + max + " players");
        }

        private void OnLobbyCreated(LobbyCreated_t result, bool ioFailure)
        {
            Guard.RunAlways(Features.Invites, "Steam lobby created", () =>
            {
                _creating = false;
                if (ioFailure || result.m_eResult != EResult.k_EResultOK || result.m_ulSteamIDLobby == 0)
                {
                    _log.Warn(LogCategory.Net, "creating the Steam lobby failed (" + (ioFailure ? "I/O failure" : result.m_eResult.ToString()) + "); invites use the connect string");
                    return;
                }
                ulong lobby = result.m_ulSteamIDLobby;
                if (!ShouldOwnLobby || _lobby != 0)
                {
                    SteamMatchmaking.LeaveLobby(new CSteamID(lobby)); // hosting stopped meanwhile
                    return;
                }
                _lobby = lobby;
                _lobbyOwned = true;
                _lobbyDataSignature = null;
                _lobbyLimit = 0;
                WriteLobbyData();
                SteamMatchmaking.SetLobbyJoinable(new CSteamID(lobby), true);
                _log.Info(LogCategory.Net, "Steam lobby " + Id(lobby) + " is open for invites");
            });
        }

        private void WriteLobbyData()
        {
            SessionHost host = _sessions.Host;
            if (host == null || _lobby == 0 || !_lobbyOwned) return;
            var lobby = new CSteamID(_lobby);
            int limit = Math.Max(2, Math.Min(16, host.Options.MaxPlayers));
            if (limit != _lobbyLimit && SteamMatchmaking.SetLobbyMemberLimit(lobby, limit)) _lobbyLimit = limit;
            var info = new LobbyInfo
            {
                HostSteamId = _sessions.LocalSteamId,
                SessionName = host.Options.SessionName ?? "",
                Protocol = ProtocolVersion.Current,
                Build = typeof(Mod).Assembly.GetName().Version?.ToString() ?? "",
                GameVersion = GameVersion(),
                PasswordRequired = !string.IsNullOrEmpty(host.Options.Password),
            };
            IReadOnlyList<KeyValuePair<string, string>> pairs = info.ToPairs();
            string signature = "";
            foreach (KeyValuePair<string, string> p in pairs) signature += p.Key + "=" + p.Value + "\n";
            if (signature == _lobbyDataSignature) return;
            bool ok = true;
            foreach (KeyValuePair<string, string> p in pairs) ok &= SteamMatchmaking.SetLobbyData(lobby, p.Key, p.Value);
            if (ok) _lobbyDataSignature = signature;
        }

        private static string GameVersion()
        {
            try
            {
                return Game.Version.current.shortVersion ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        /// <summary>A client in a live session joins the host's lobby (from the invite, or the host's game info) so it can invite too.</summary>
        private void JoinHostLobby(ulong host)
        {
            _clientLobbyTried = true;
            ulong lobby = _clientLobbyCandidate;
            if (lobby == 0)
            {
                try
                {
                    if (SteamFriends.GetFriendGamePlayed(new CSteamID(host), out FriendGameInfo_t game) && game.m_steamIDLobby.IsValid() && game.m_steamIDLobby.IsLobby()) lobby = game.m_steamIDLobby.m_SteamID;
                }
                catch (Exception)
                {
                    lobby = 0;
                }
            }
            if (lobby == 0) return; // the host is not a friend or has no lobby: invites use the connect string
            Enter(lobby, EnterPurpose.Member);
        }

        private void Enter(ulong lobby, EnterPurpose purpose)
        {
            SteamAPICall_t call = SteamMatchmaking.JoinLobby(new CSteamID(lobby));
            if (call == SteamAPICall_t.Invalid)
            {
                _log.Warn(LogCategory.Net, "Steam refused to enter lobby " + Id(lobby));
                if (purpose == EnterPurpose.Resolve) FailResolve();
                return;
            }
            _entering = purpose;
            _enteringLobby = lobby;
            _lobbyEntered.Set(call);
        }

        private void OnLobbyEntered(LobbyEnter_t result, bool ioFailure)
        {
            Guard.RunAlways(Features.Invites, "Steam lobby entered", () =>
            {
                EnterPurpose purpose = _entering;
                _entering = EnterPurpose.None;
                _enteringLobby = 0;
                ulong lobby = result.m_ulSteamIDLobby;
                bool ok = !ioFailure && result.m_EChatRoomEnterResponse == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess && lobby != 0;
                if (purpose == EnterPurpose.Resolve)
                {
                    LobbyInfo info = null;
                    if (ok)
                    {
                        LobbyInfo.TryRead(key => SteamMatchmaking.GetLobbyData(new CSteamID(lobby), key), out info);
                        SteamMatchmaking.LeaveLobby(new CSteamID(lobby)); // only read; a live client joins it again
                    }
                    if (lobby != _resolveLobby) return; // a newer invite replaced this one
                    if (info != null) Resolved(info, lobby);
                    else FailResolve();
                    return;
                }
                if (!ok) return;
                bool stillClient = _sessions.Host == null && _sessions.Client != null && _lobby == 0;
                if (purpose == EnterPurpose.Member && stillClient)
                {
                    _lobby = lobby;
                    _lobbyOwned = false;
                    _log.Info(LogCategory.Net, "joined the host's Steam lobby " + Id(lobby) + " (invites from this client use it)");
                }
                else
                {
                    SteamMatchmaking.LeaveLobby(new CSteamID(lobby));
                }
            });
        }

        private void LeaveLobby()
        {
            if (_lobby == 0) return;
            ulong lobby = _lobby;
            _lobby = 0;
            _lobbyOwned = false;
            _lobbyDataSignature = null;
            _lobbyLimit = 0;
            try
            {
                SteamMatchmaking.LeaveLobby(new CSteamID(lobby));
                _log.Info(LogCategory.Net, "left Steam lobby " + Id(lobby));
            }
            catch (Exception)
            {
                // Steam not running
            }
        }

        // -------------------------------------------------------------------------------------------- receiving

        private void OnRichPresenceJoinRequested(GameRichPresenceJoinRequested_t e)
        {
            Guard.Run(Features.Invites, "Steam join request", () =>
            {
                string friend = FriendName(e.m_steamIDFriend);
                if (InviteLink.TryParse(e.m_rgchConnect, out ulong host))
                {
                    _log.Info(LogCategory.Session, "Steam join request from " + friend + " for " + Id(host));
                    QueueJoin(host, friend, 0);
                    return;
                }
                if (InviteLink.TryParseLobby(e.m_rgchConnect, out ulong lobby))
                {
                    StartResolve(lobby, friend);
                    return;
                }
                _log.Warn(LogCategory.Session, "ignored a Steam join request without a Skyline Together link");
            });
        }

        private void OnLobbyJoinRequested(GameLobbyJoinRequested_t e)
        {
            Guard.Run(Features.Invites, "Steam lobby join request", () =>
            {
                string friend = FriendName(e.m_steamIDFriend);
                ulong lobby = e.m_steamIDLobby.m_SteamID;
                _log.Info(LogCategory.Session, "Steam lobby join request from " + friend + " (lobby " + Id(lobby) + ")");
                if (lobby != 0 && lobby == _lobby) return; // our own session's lobby
                // the friend's rich presence carries the host directly when the friend is in the session
                try
                {
                    if (InviteLink.TryParse(SteamFriends.GetFriendRichPresence(e.m_steamIDFriend, ConnectKey), out ulong host) && _sessions.InSession && InviteTarget == host) return;
                }
                catch (Exception)
                {
                    // fall through to the lobby data
                }
                StartResolve(lobby, friend, e.m_steamIDFriend);
            });
        }

        /// <summary>Finds the host of an invite's lobby: lobby data if Steam has it, else a data request, else entering the lobby.</summary>
        private void StartResolve(ulong lobby, string friend, CSteamID friendId = default)
        {
            if (lobby == 0) return;
            if (_sessions.InSession)
            {
                SessionManager.Notify(LocaleSource.Resolve("st.toast.inviteBusy", "NAME", friend), ProgressState.Warning);
                return;
            }
            var id = new CSteamID(lobby);
            if (LobbyInfo.TryRead(key => SteamMatchmaking.GetLobbyData(id, key), out LobbyInfo info))
            {
                _resolveLobby = lobby;
                _resolveFriend = friend;
                Resolved(info, lobby);
                return;
            }
            if (friendId.m_SteamID != 0)
            {
                try
                {
                    if (InviteLink.TryParse(SteamFriends.GetFriendRichPresence(friendId, ConnectKey), out ulong host))
                    {
                        _log.Info(LogCategory.Session, "the invite's host " + Id(host) + " comes from " + friend + "'s rich presence");
                        QueueJoin(host, friend, lobby);
                        return;
                    }
                }
                catch (Exception)
                {
                    // fall through
                }
            }
            _resolveLobby = lobby;
            _resolveFriend = friend;
            _resolveStarted = _sessions.Clock.Seconds;
            _resolveEntering = false;
            bool requested = false;
            try
            {
                requested = SteamMatchmaking.RequestLobbyData(id);
            }
            catch (Exception)
            {
                requested = false;
            }
            if (!requested)
            {
                _resolveEntering = true;
                Enter(lobby, EnterPurpose.Resolve);
            }
        }

        private void OnLobbyDataUpdated(LobbyDataUpdate_t e)
        {
            Guard.Run(Features.Invites, "Steam lobby data", () =>
            {
                if (_resolveLobby == 0 || e.m_ulSteamIDLobby != _resolveLobby || e.m_ulSteamIDMember != e.m_ulSteamIDLobby || _resolveEntering) return;
                var id = new CSteamID(_resolveLobby);
                if (e.m_bSuccess != 0 && LobbyInfo.TryRead(key => SteamMatchmaking.GetLobbyData(id, key), out LobbyInfo info))
                {
                    Resolved(info, _resolveLobby);
                    return;
                }
                _resolveEntering = true;
                Enter(_resolveLobby, EnterPurpose.Resolve); // not readable from outside: read it as a member
            });
        }

        private void UpdateResolve(double now)
        {
            if (_coldStartLobby != 0 && SessionManager.SteamAvailable)
            {
                ulong lobby = _coldStartLobby;
                _coldStartLobby = 0;
                StartResolve(lobby, null);
            }
            if (_resolveLobby == 0) return;
            if (!_resolveEntering && now - _resolveStarted > ResolveDataSeconds)
            {
                _resolveEntering = true;
                Enter(_resolveLobby, EnterPurpose.Resolve);
            }
            else if (now - _resolveStarted > ResolveTimeoutSeconds)
            {
                _log.Warn(LogCategory.Session, "reading the invite's lobby " + Id(_resolveLobby) + " timed out");
                FailResolve();
            }
        }

        private void Resolved(LobbyInfo info, ulong lobby)
        {
            string friend = _resolveFriend;
            _resolveLobby = 0;
            _resolveFriend = null;
            _resolveEntering = false;
            _log.Info(LogCategory.Session, "the invite's lobby " + Id(lobby) + " belongs to host " + Id(info.HostSteamId) + " (\"" + info.SessionName + "\", protocol " + info.Protocol
                                         + (info.PasswordRequired ? ", password required" : "") + ")");
            if (info.Protocol != 0 && info.Protocol != ProtocolVersion.Current)
            {
                _log.Warn(LogCategory.Session, "the host runs protocol " + info.Protocol + ", this game " + ProtocolVersion.Current + "; the host will refuse the join with the details");
            }
            QueueJoin(info.HostSteamId, string.IsNullOrEmpty(friend) ? info.SessionName : friend, lobby);
        }

        private void FailResolve()
        {
            string friend = _resolveFriend;
            _resolveLobby = 0;
            _resolveFriend = null;
            _resolveEntering = false;
            _log.Warn(LogCategory.Session, "could not read the Steam invite's lobby");
            SessionManager.Notify(LocaleSource.Resolve("st.toast.inviteUnreadable", "NAME", string.IsNullOrEmpty(friend) ? "?" : friend), ProgressState.Warning, null, 10f);
        }

        private void QueueJoin(ulong host, string friend, ulong lobby)
        {
            if (host == 0 || host == _sessions.LocalSteamId) return; // our own invite
            if (_sessions.InSession)
            {
                if (InviteTarget == host) return; // already in that session
                SessionManager.Notify(LocaleSource.Resolve("st.toast.inviteBusy", "NAME", string.IsNullOrEmpty(friend) ? Id(host) : friend), ProgressState.Warning);
                return;
            }
            _pendingHost = host;
            _pendingFriend = friend;
            _pendingLobby = lobby;
            _confirmShown = false;
        }

        private void UpdatePending(double now)
        {
            if (_pendingHost == 0) return;
            if (_sessions.InSession)
            {
                // something else started a session meanwhile: the invite is void
                DropPending("a session started before the invite could be joined");
                return;
            }
            if (!ReadyToJoin()) return;
            if (!InCity() && !_confirmShown)
            {
                AcceptPending(); // main menu: nothing to replace
                return;
            }
            if (!_confirmShown)
            {
                _confirmShown = true;
                _confirmUntil = now + ConfirmSeconds;
                string name = string.IsNullOrEmpty(_pendingFriend) ? Id(_pendingHost) : _pendingFriend;
                _log.Info(LogCategory.Session, "asking before joining " + Id(_pendingHost) + " (a city is open)");
                try
                {
                    Game.PSI.NotificationSystem.Pop(ConfirmToastId, (float)ConfirmSeconds, title: LocalizedString.Value(LocaleSource.Resolve("st.toast.title")),
                        text: LocalizedString.Value(LocaleSource.Resolve("st.toast.inviteConfirm", "NAME", name)), progressState: ProgressState.Warning,
                        onClicked: () => Guard.RunAlways(Features.Invites, "accepting a Steam invite", AcceptPending));
                }
                catch (Exception ex)
                {
                    _log.Warn(LogCategory.Ui, "showing the invite toast failed; joining needs the Join tab", ex);
                }
                return;
            }
            if (now > _confirmUntil) DropPending("the invite was not accepted within " + ConfirmSeconds + " s");
        }

        private void DropPending(string why)
        {
            _log.Info(LogCategory.Session, "dropped the Steam invite for " + Id(_pendingHost) + ": " + why);
            _pendingHost = 0;
            _pendingFriend = null;
            _pendingLobby = 0;
            _confirmShown = false;
            SessionManager.Unnotify(ConfirmToastId);
        }

        /// <summary>Joins the pending invite (directly from the main menu, or from the confirmation toast in a city).</summary>
        private void AcceptPending()
        {
            ulong host = _pendingHost;
            string friend = _pendingFriend;
            ulong lobby = _pendingLobby;
            _pendingHost = 0;
            _pendingFriend = null;
            _pendingLobby = 0;
            _confirmShown = false;
            SessionManager.Unnotify(ConfirmToastId);
            if (host == 0 || _sessions.InSession) return;
            _clientLobbyCandidate = lobby;
            _clientLobbyTried = false;
            Join(host, friend);
        }

        private bool ReadyToJoin()
        {
            GameManager gm = GameManager.instance;
            return !_sessions.InSession && SessionManager.SteamAvailable && gm != null && !gm.isGameLoading && gm.gameMode != Game.GameMode.None;
        }

        private static bool InCity()
        {
            GameManager gm = GameManager.instance;
            return gm != null && (gm.gameMode & Game.GameMode.GameOrEditor) != 0;
        }

        private void Join(ulong host, string friend)
        {
            if (_settings == null) return;
            _settings.JoinTarget = Id(host);
            _settings.JoinPassword = ""; // a password-protected session asks for it on the error screen
            _settings.ApplyAndSave();
            SessionManager.Notify(LocaleSource.Resolve("st.toast.inviteJoin", "NAME", string.IsNullOrEmpty(friend) ? _settings.JoinTarget : friend));
            _sessions.StartJoining();
        }

        // -------------------------------------------------------------------------------------------------- misc

        private void OnOverlayActivated(GameOverlayActivated_t e)
        {
            if (e.m_bActive == 0) return;
            _overlayWatchUntil = -1;
            if (_overlayNoResponse)
            {
                _overlayNoResponse = false;
                _log.Info(LogCategory.Net, "the Steam overlay works again");
            }
        }

        private static string Id(ulong id) => id.ToString(CultureInfo.InvariantCulture);

        private static string FriendName(CSteamID id)
        {
            try
            {
                string name = SteamFriends.GetFriendPersonaName(id);
                return string.IsNullOrWhiteSpace(name) ? Id(id.m_SteamID) : name;
            }
            catch (Exception)
            {
                return Id(id.m_SteamID);
            }
        }
    }
}
