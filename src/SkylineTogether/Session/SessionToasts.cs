using System.Collections.Generic;
using System.Globalization;
using Colossal.PSI.Common;
using SkylineTogether.Core;
using SkylineTogether.Core.Session;
using SkylineTogether.Settings;

namespace SkylineTogether.Session
{
    /// <summary>
    /// Session events as toasts in the game's notification list: players joining (with a progress bar on the host),
    /// joined and left, and the joiner's own progress until the city is in sync. Main thread, ticked every frame.
    /// </summary>
    public sealed class SessionToasts
    {
        private const string JoinId = "SkylineTogether.joining";
        private const string PlayerIdPrefix = "SkylineTogether.player.";
        private const double PollSeconds = 0.5;

        private sealed class Known
        {
            public string Name;
            public PlayerState State;
            public int Percent;
        }

        private readonly SessionManager _sessions;
        private readonly Dictionary<byte, Known> _known = new Dictionary<byte, Known>();
        private int _version = -1;
        private double _polledAt = double.NegativeInfinity;
        private bool _baseline;
        private ClientState _clientState = ClientState.Idle;
        private int _clientPercent = -1;

        public SessionToasts(SessionManager sessions)
        {
            _sessions = sessions;
            sessions.HostStarted += _ => Reset();
            sessions.ClientStarted += _ => Reset();
            sessions.SessionEnded += OnSessionEnded;
        }

        public void Tick()
        {
            TickOwnJoin();
            double now = _sessions.Clock.Seconds;
            if (_sessions.PlayersVersion == _version && now - _polledAt < PollSeconds) return;
            _version = _sessions.PlayersVersion;
            _polledAt = now;
            IReadOnlyList<PlayerInfo> players;
            PeerId local;
            bool hosting = _sessions.Host != null;
            if (hosting)
            {
                players = _sessions.Host.GetPlayers();
                local = PeerId.Host;
            }
            else if (_sessions.Client != null && _sessions.Client.State == ClientState.Live)
            {
                players = _sessions.Client.Players;
                local = _sessions.Client.LocalPeer;
            }
            else
            {
                return;
            }
            var seen = new HashSet<byte>();
            foreach (PlayerInfo p in players)
            {
                if (p.Id == local) continue;
                seen.Add(p.Id.Value);
                int percent = (int)(p.Progress * 100);
                if (!_known.TryGetValue(p.Id.Value, out Known k))
                {
                    _known[p.Id.Value] = new Known { Name = p.Name, State = p.State, Percent = percent };
                    if (!_baseline) continue; // players already there when we arrived
                    if (p.State == PlayerState.Live) SessionManager.Notify(LocaleSource.Resolve("st.toast.playerJoined", "NAME", p.Name ?? ""), ProgressState.Complete, PlayerIdPrefix + p.Id.Value);
                    else if (hosting) Progress(p, percent);
                    continue;
                }
                bool changed = k.State != p.State || (p.State != PlayerState.Live && percent / 5 != k.Percent / 5);
                if (!changed) continue;
                PlayerState was = k.State;
                k.State = p.State;
                k.Percent = percent;
                k.Name = p.Name;
                if (p.State == PlayerState.Live && was != PlayerState.Live)
                {
                    SessionManager.Notify(LocaleSource.Resolve("st.toast.playerJoined", "NAME", p.Name ?? ""), ProgressState.Complete, PlayerIdPrefix + p.Id.Value);
                }
                else if (hosting && p.State != PlayerState.Live)
                {
                    Progress(p, percent);
                }
            }
            var gone = new List<byte>();
            foreach (KeyValuePair<byte, Known> k in _known)
            {
                if (!seen.Contains(k.Key)) gone.Add(k.Key);
            }
            foreach (byte id in gone)
            {
                Known k = _known[id];
                _known.Remove(id);
                SessionManager.Notify(LocaleSource.Resolve(k.State == PlayerState.Live ? "st.toast.playerLeft" : "st.toast.playerStoppedJoining", "NAME", k.Name ?? ""),
                    ProgressState.Warning, PlayerIdPrefix + id.ToString(CultureInfo.InvariantCulture));
            }
            _baseline = true;
        }

        private static void Progress(PlayerInfo p, int percent)
        {
            string key = p.State == PlayerState.Downloading ? "st.toast.playerDownloading" : p.State == PlayerState.Loading ? "st.toast.playerLoading" : "st.toast.playerJoining";
            bool hasPercent = p.State == PlayerState.Downloading || p.State == PlayerState.Loading;
            SessionManager.NotifyProgress(PlayerIdPrefix + p.Id.Value, LocaleSource.Resolve(key, "NAME", p.Name ?? ""), hasPercent ? percent : (int?)null);
        }

        /// <summary>The joiner's own progress (the overlay may be hidden while the game loads the city).</summary>
        private void TickOwnJoin()
        {
            SessionClient c = _sessions.Client;
            if (c == null) return;
            int percent = (int)(c.JoinProgress * 100);
            if (c.State == _clientState && (c.State != ClientState.Downloading && c.State != ClientState.Loading || percent / 5 == _clientPercent / 5)) return;
            ClientState was = _clientState;
            _clientState = c.State;
            _clientPercent = percent;
            string session = c.Session?.SessionName ?? "";
            switch (c.State)
            {
                case ClientState.Connecting:
                case ClientState.Handshaking:
                    SessionManager.NotifyProgress(JoinId, LocaleSource.Resolve("st.toast.join.connecting"), null);
                    break;
                case ClientState.WaitingForSnapshot:
                    SessionManager.NotifyProgress(JoinId, LocaleSource.Resolve("st.toast.join.waiting", "SESSION", session), null);
                    break;
                case ClientState.Downloading:
                    SessionManager.NotifyProgress(JoinId, LocaleSource.Resolve("st.toast.join.downloading", "SESSION", session), percent);
                    break;
                case ClientState.Loading:
                    SessionManager.NotifyProgress(JoinId, LocaleSource.Resolve("st.toast.join.loadingCity", "SESSION", session), percent);
                    break;
                case ClientState.Live:
                    if (was != ClientState.Live) SessionManager.Notify(LocaleSource.Resolve("st.toast.joined", "SESSION", session), ProgressState.Complete, JoinId);
                    break;
                default:
                    SessionManager.Unnotify(JoinId);
                    break;
            }
        }

        private void OnSessionEnded()
        {
            foreach (byte id in _known.Keys) SessionManager.Unnotify(PlayerIdPrefix + id.ToString(CultureInfo.InvariantCulture));
            SessionManager.Unnotify(JoinId);
            Reset();
        }

        private void Reset()
        {
            _known.Clear();
            _version = -1;
            _baseline = false;
            _clientState = ClientState.Idle;
            _clientPercent = -1;
        }
    }
}
