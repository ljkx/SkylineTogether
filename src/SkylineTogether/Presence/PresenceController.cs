using System;
using System.Collections.Generic;
using SkylineTogether.Core;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Session;
using ILog = SkylineTogether.Core.ILog;

namespace SkylineTogether.Presence
{
    /// <summary>
    /// Remote presence (ARCH K15, §11.4), session side: keeps the <see cref="PresenceTable"/> fed from the host or
    /// client session, sends the local player's presence through it, and holds the tags the UI shows. The drawing and
    /// the local raycast live in <see cref="PresenceSystem"/>. Main thread only.
    /// </summary>
    public sealed class PresenceController
    {
        /// <summary>Marker colours by peer id (the host is 0).</summary>
        private static readonly uint[] Palette =
        {
            0xF2B84BFF, 0x3FB6E8FF, 0x5FCF7AFF, 0xEF6A6AFF, 0xB58CF2FF, 0xF28CC8FF, 0x4BE0D2FF, 0xC9E04BFF,
        };

        private readonly SessionManager _sessions;
        private readonly ILog _log;
        private readonly Dictionary<byte, string> _names = new Dictionary<byte, string>();
        private int _namesVersion = -1;
        private List<PresenceTag> _tags = new List<PresenceTag>();

        public PresenceController(SessionManager sessions, ILog log)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _log = log;
            sessions.HostStarted += OnHostStarted;
            sessions.ClientStarted += OnClientStarted;
            sessions.SessionEnded += OnSessionEnded;
        }

        public PresenceTable Table { get; } = new PresenceTable();

        public PresenceThrottle Throttle { get; } = new PresenceThrottle();

        /// <summary>Raised when a session starts or ends (the prefab rank index is rebuilt).</summary>
        public event Action SessionChanged;

        /// <summary>True while presence flows: hosting, or a live client.</summary>
        public bool Active => _sessions.Host != null || (_sessions.Client != null && _sessions.Client.State == ClientState.Live);

        /// <summary>The local player's peer id (host: 0).</summary>
        public PeerId LocalPeer => _sessions.Client != null ? _sessions.Client.LocalPeer : PeerId.Host;

        /// <summary>Tags of the remote players as the UI shows them, replaced as a whole by <see cref="PresenceSystem"/> when
        /// they changed noticeably.</summary>
        public IReadOnlyList<PresenceTag> Tags => _tags;

        /// <summary>Changes whenever <see cref="Tags"/> is replaced.</summary>
        public int TagsVersion { get; private set; }

        public double Now => _sessions.Clock.Seconds;

        public static uint ColorOf(PeerId peer) => Palette[peer.Value % Palette.Length];

        public static string CssColor(uint rgba) => "#" + (rgba >> 8).ToString("x6", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Sends the local presence (host: to every live client; client: to the host, which relays it).</summary>
        public void Send(PresenceMessage m)
        {
            try
            {
                if (_sessions.Host != null) _sessions.Host.SendPresence(m);
                else _sessions.Client?.SendPresence(m);
            }
            catch (Exception ex)
            {
                Diagnostics.Guard.Report(Diagnostics.Features.Presence, "sending presence", ex);
            }
        }

        /// <summary>
        /// Replaces the tags when they differ noticeably from the ones the UI shows (<see cref="PresenceTagDiff"/>); the
        /// version (and with it the UI binding) changes only then. polish: the projection runs every frame, a push only
        /// when a tag moved by a few pixels or changed. <paramref name="tags"/> is copied, so the caller may reuse it.
        /// </summary>
        public void SetTags(List<PresenceTag> tags)
        {
            if (tags != null && !PresenceTagDiff.Differs(_tags, tags)) return;
            _tags = tags == null ? new List<PresenceTag>() : new List<PresenceTag>(tags);
            TagsVersion++;
        }

        /// <summary>The display name of a player, from the session's player list.</summary>
        public string NameOf(PeerId peer)
        {
            if (_namesVersion != _sessions.PlayersVersion)
            {
                _namesVersion = _sessions.PlayersVersion;
                _names.Clear();
                IReadOnlyList<PlayerInfo> players = _sessions.Host != null ? _sessions.Host.GetPlayers() : _sessions.Client?.Players;
                if (players != null)
                {
                    foreach (PlayerInfo p in players) _names[p.Id.Value] = p.Name;
                    if (_sessions.Client != null) Table.RetainOnly(PeersOf(players));
                }
            }
            return _names.TryGetValue(peer.Value, out string name) ? name : (peer.IsHost ? "Host" : "Player " + peer.Value);
        }

        private static List<PeerId> PeersOf(IReadOnlyList<PlayerInfo> players)
        {
            var list = new List<PeerId>(players.Count);
            foreach (PlayerInfo p in players) list.Add(p.Id);
            return list;
        }

        private void OnHostStarted(SessionHost host)
        {
            Reset();
            host.PresenceReceived += m => Table.Update(m, Now, PeerId.Host);
            host.PeerLeft += peer => Table.Remove(peer);
        }

        private void OnClientStarted(SessionClient client)
        {
            Reset();
            client.PresenceReceived += m => Table.Update(m, Now, client.LocalPeer);
            client.BecameLive += () => SessionChanged?.Invoke(); // the client's prefab set is final only after its load
        }

        private void OnSessionEnded() => Reset();

        private void Reset()
        {
            Table.Clear();
            Throttle.Reset();
            _names.Clear();
            _namesVersion = -1;
            SetTags(null);
            SessionChanged?.Invoke();
        }
    }
}
