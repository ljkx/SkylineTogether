using System;
using System.Collections.Generic;
using SkylineTogether.Core;
using SkylineTogether.Core.Session;
using SkylineTogether.Diagnostics;
using Steamworks;
using ILog = SkylineTogether.Core.ILog;

namespace SkylineTogether.Session
{
    /// <summary>
    /// The invite list: the player's Steam friends (<see cref="EFriendFlags.k_EFriendFlagImmediate"/>) with name, persona
    /// state, whether they play Cities: Skylines II and whether they are already in the session, as the JSON of
    /// <see cref="FriendList.ToJson"/>. Built only when the UI asks (the binding polls only while the panel observes it):
    /// at most every <see cref="RefreshSeconds"/>, sooner after a <see cref="PersonaStateChange_t"/> (at most once per
    /// <see cref="MinRebuildSeconds"/>), when the session's players changed, and at once after <see cref="RequestRefresh"/>.
    /// Main thread only.
    /// </summary>
    public sealed class SteamFriendList : IDisposable
    {
        public const double RefreshSeconds = 10;
        public const double MinRebuildSeconds = 1;

        private readonly SessionManager _sessions;
        private readonly ILog _log;
        private Callback<PersonaStateChange_t> _personaChanged; // kept so it can be disposed with the mod
        private string _json = "[]";
        private double _builtAt = double.NegativeInfinity;
        private bool _dirty = true;
        private bool _forced;
        private int _playersVersion = -1;
        private readonly HashSet<ulong> _inSession = new HashSet<ulong>();
        private readonly List<FriendEntry> _rows = new List<FriendEntry>();

        public SteamFriendList(SessionManager sessions, ILog log)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _log = log;
            try
            {
                _personaChanged = Callback<PersonaStateChange_t>.Create(OnPersonaStateChange);
            }
            catch (Exception ex)
            {
                _log?.Warn(LogCategory.Net, "Steam persona updates are unavailable; the friends list refreshes every " + RefreshSeconds + " s", ex);
            }
        }

        /// <summary>Rows in the last build (diagnostics).</summary>
        public int Count => _rows.Count;

        /// <summary>The next <see cref="Json"/> rebuilds the list ("refreshFriends").</summary>
        public void RequestRefresh() => _forced = true;

        /// <summary>The binding value; rebuilt when due (see the class summary), otherwise the cached string.</summary>
        public string Json(double now)
        {
            int players = _sessions.PlayersVersion;
            bool due = _forced || now - _builtAt >= RefreshSeconds || players != _playersVersion || (_dirty && now - _builtAt >= MinRebuildSeconds);
            if (!due) return _json;
            _forced = false;
            _dirty = false;
            _builtAt = now;
            _playersVersion = players;
            _json = Build();
            return _json;
        }

        public void Dispose()
        {
            try
            {
                _personaChanged?.Dispose();
            }
            catch (Exception)
            {
                // Steam already shut down
            }
            _personaChanged = null;
        }

        private void OnPersonaStateChange(PersonaStateChange_t e)
        {
            // also fires for the local player and non-friends; the rebuild is throttled, so no filter is needed
            _dirty = true;
        }

        private string Build()
        {
            _rows.Clear();
            if (!SessionManager.SteamAvailable) return "[]";
            CollectSessionPlayers();
            int count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            for (int i = 0; i < count && _rows.Count < FriendList.MaxEntries; i++)
            {
                CSteamID id = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                if (!id.IsValid()) continue;
                uint app = 0;
                if (SteamFriends.GetFriendGamePlayed(id, out FriendGameInfo_t game) && game.m_gameID.IsSteamApp()) app = game.m_gameID.AppID().m_AppId;
                string name = SteamFriends.GetFriendPersonaName(id);
                _rows.Add(new FriendEntry
                {
                    Id = id.m_SteamID,
                    Name = string.IsNullOrEmpty(name) ? id.m_SteamID.ToString(System.Globalization.CultureInfo.InvariantCulture) : name,
                    State = FriendList.Classify((int)SteamFriends.GetFriendPersonaState(id), app),
                    InSession = _inSession.Contains(id.m_SteamID),
                });
            }
            FriendList.Sort(_rows);
            return FriendList.ToJson(_rows);
        }

        private void CollectSessionPlayers()
        {
            _inSession.Clear();
            IReadOnlyList<PlayerInfo> players = null;
            if (_sessions.Host != null) players = _sessions.Host.GetPlayers();
            else if (_sessions.Client != null) players = _sessions.Client.Players;
            if (players == null) return;
            foreach (PlayerInfo p in players)
            {
                if (p != null && p.SteamId != 0) _inSession.Add(p.SteamId);
            }
        }
    }
}
