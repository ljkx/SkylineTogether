using System;
using System.Globalization;
using Colossal.PSI.Common;
using Game.SceneFlow;
using SkylineTogether.Core;
using SkylineTogether.Core.Session;
using SkylineTogether.Settings;
using Steamworks;
using ILog = SkylineTogether.Core.ILog;

namespace SkylineTogether.Session
{
    /// <summary>
    /// Steam friends integration: while a session runs, the rich presence key <c>connect</c> carries
    /// <see cref="InviteLink"/> for the host, so friends can "Join Game" from their friends list; the panel opens the
    /// overlay's invite dialog with the same string; and a friend's invite or join click arrives here as
    /// <see cref="GameRichPresenceJoinRequested_t"/> (game running) or on the command line (Steam started the game), and
    /// joins automatically. Only the <c>connect</c> key is touched; the game's own <c>steam_display</c> stays.
    /// </summary>
    public sealed class SteamInvites : IDisposable
    {
        private const string ConnectKey = "connect";
        private const double PresenceRefreshSeconds = 60;

        private readonly SessionManager _sessions;
        private readonly SkylineTogetherSettings _settings;
        private readonly ILog _log;
        private Callback<GameRichPresenceJoinRequested_t> _joinRequested;
        private ulong _pendingHost;
        private string _pendingFriend;
        private string _presence = "";
        private double _presenceAt = double.NegativeInfinity;

        public SteamInvites(SessionManager sessions, SkylineTogetherSettings settings, ILog log)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _settings = settings;
            _log = log;
            try
            {
                _joinRequested = Callback<GameRichPresenceJoinRequested_t>.Create(OnJoinRequested);
            }
            catch (Exception ex)
            {
                _log.Warn(LogCategory.Net, "Steam join requests are unavailable", ex);
            }
            try
            {
                if (InviteLink.TryParseArgs(Environment.GetCommandLineArgs(), out ulong host))
                {
                    _pendingHost = host;
                    _log.Info(LogCategory.Session, "started from a Steam invite; joining " + host.ToString(CultureInfo.InvariantCulture) + " once the menu is ready");
                }
            }
            catch (Exception ex)
            {
                _log.Warn(LogCategory.Session, "reading the command line failed", ex);
            }
            sessions.SessionEnded += () => SetPresence("");
        }

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

        /// <summary>The Steam ID friends join: the local player while hosting, the host while playing as a client; 0 otherwise.</summary>
        public ulong InviteTarget
        {
            get
            {
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

        /// <summary>Opens the Steam overlay's invite dialog for the current session.</summary>
        public void OpenInviteDialog()
        {
            ulong target = InviteTarget;
            if (target == 0) return;
            if (!OverlayEnabled)
            {
                SessionManager.Notify(LocaleSource.Resolve("st.toast.overlayDisabled", "ID", target.ToString(CultureInfo.InvariantCulture)), ProgressState.Warning);
                return;
            }
            try
            {
                SteamFriends.ActivateGameOverlayInviteDialogConnectString(InviteLink.Format(target));
            }
            catch (Exception ex)
            {
                _log.Warn(LogCategory.Net, "opening the Steam invite dialog failed", ex);
                SessionManager.Notify(LocaleSource.Resolve("st.toast.overlayDisabled", "ID", target.ToString(CultureInfo.InvariantCulture)), ProgressState.Warning);
            }
        }

        /// <summary>Once per frame: keeps the rich presence current and starts a pending invite join when possible.</summary>
        public void Tick()
        {
            double now = _sessions.Clock.Seconds;
            ulong target = InviteTarget;
            string wanted = target != 0 ? InviteLink.Format(target) : "";
            if (wanted != _presence || (wanted.Length > 0 && now - _presenceAt > PresenceRefreshSeconds))
            {
                _presenceAt = now;
                SetPresence(wanted);
            }
            if (_pendingHost != 0 && ReadyToJoin())
            {
                ulong host = _pendingHost;
                string friend = _pendingFriend;
                _pendingHost = 0;
                _pendingFriend = null;
                Join(host, friend);
            }
        }

        public void Dispose()
        {
            SetPresence("");
            try
            {
                _joinRequested?.Dispose();
            }
            catch (Exception)
            {
                // Steam already shut down
            }
            _joinRequested = null;
        }

        private bool ReadyToJoin()
        {
            GameManager gm = GameManager.instance;
            return !_sessions.InSession && SessionManager.SteamAvailable && gm != null && !gm.isGameLoading && gm.gameMode != Game.GameMode.None;
        }

        private void OnJoinRequested(GameRichPresenceJoinRequested_t e)
        {
            try
            {
                string friend = FriendName(e.m_steamIDFriend);
                if (!InviteLink.TryParse(e.m_rgchConnect, out ulong host))
                {
                    _log.Warn(LogCategory.Session, "ignored a Steam join request without a Skyline Together link");
                    return;
                }
                _log.Info(LogCategory.Session, "Steam join request from " + friend + " for " + host.ToString(CultureInfo.InvariantCulture));
                if (_sessions.InSession)
                {
                    if (InviteTarget == host) return; // already in that session
                    SessionManager.Notify(LocaleSource.Resolve("st.toast.inviteBusy", "NAME", friend), ProgressState.Warning);
                    return;
                }
                _pendingHost = host;
                _pendingFriend = friend;
            }
            catch (Exception ex)
            {
                _log.Error(LogCategory.Session, "handling a Steam join request failed", ex);
            }
        }

        private void Join(ulong host, string friend)
        {
            if (_settings == null) return;
            _settings.JoinTarget = host.ToString(CultureInfo.InvariantCulture);
            _settings.JoinPassword = ""; // a password-protected session asks for it on the error screen
            _settings.ApplyAndSave();
            SessionManager.Notify(LocaleSource.Resolve("st.toast.inviteJoin", "NAME", string.IsNullOrEmpty(friend) ? _settings.JoinTarget : friend));
            _sessions.StartJoining();
        }

        private void SetPresence(string connect)
        {
            if (connect == _presence && connect.Length == 0) return;
            _presence = connect;
            try
            {
                SteamFriends.SetRichPresence(ConnectKey, connect); // an empty value removes the key
            }
            catch (Exception)
            {
                // Steam not running
            }
        }

        private static string FriendName(CSteamID id)
        {
            try
            {
                string name = SteamFriends.GetFriendPersonaName(id);
                return string.IsNullOrWhiteSpace(name) ? id.m_SteamID.ToString(CultureInfo.InvariantCulture) : name;
            }
            catch (Exception)
            {
                return id.m_SteamID.ToString(CultureInfo.InvariantCulture);
            }
        }
    }
}
