using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Colossal.UI.Binding;
using Game.UI;
using SkylineTogether.Core;
using SkylineTogether.Core.Session;
using SkylineTogether.Diagnostics;
using SkylineTogether.Session;

namespace SkylineTogether.UI
{
    /// <summary>
    /// Bindings of the Skyline Together panel (group <c>skylineTogether</c>): one JSON <c>state</c> value with
    /// everything the panel shows, rebuilt only when something changed, and triggers for the panel's actions. Every
    /// getter and trigger is guarded (<see cref="Guard"/>, feature <see cref="Features.Ui"/>); the change check runs
    /// every frame without allocating, so the panel costs nothing in single player while nothing changes.
    /// </summary>
    public sealed class SessionUISystem : UISystemBase
    {
        public const string Group = "skylineTogether";

        /// <summary>The password value the panel sends when its field was left untouched.</summary>
        public const string KeepPassword = "\u0001keep";

        private static GetterValueBinding<string> _stateBinding;

        private string _state = "{}";
        private StateKey _key;
        private bool _hasKey;
        private string _net = "{}";
        private double _netAt = double.NegativeInfinity;
        private string _presence = "{\"tags\":[]}";
        private int _presenceVersion = -1;
        private double _presenceAt = double.NegativeInfinity;
        private string _sync = "";
        private double _syncAt = double.NegativeInfinity;
        // cached delegates: a method group passed per call would allocate every frame
        private Func<string> _buildState, _buildNet, _buildPresence;
        private double _stateRetry, _netRetry, _presenceRetry;

        /// <summary>Observers of the state binding (the UI module subscribes when it loaded; P-UI probe).</summary>
        public static int StateObservers
        {
            get
            {
                try
                {
                    return _stateBinding?.observerCount ?? 0;
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }

        /// <summary>Everything the state JSON depends on, compared field by field every frame (no allocation).</summary>
        private struct StateKey : IEquatable<StateKey>
        {
            public SessionMode Mode;
            public int Players, Chat, Recovery, Probes, ClientState, Progress, RecoverySeconds;
            public bool CanHost, CanJoin, Steam;
            public string LastEnd, Sync;
            public SpeedState Speed;

            public bool Equals(StateKey o)
            {
                return Mode == o.Mode && Players == o.Players && Chat == o.Chat && Recovery == o.Recovery && Probes == o.Probes && ClientState == o.ClientState
                       && Progress == o.Progress && RecoverySeconds == o.RecoverySeconds && CanHost == o.CanHost && CanJoin == o.CanJoin && Steam == o.Steam
                       && ReferenceEquals(LastEnd, o.LastEnd) && ReferenceEquals(Sync, o.Sync) && Speed.Equals(o.Speed);
            }

            public override bool Equals(object obj) => obj is StateKey k && Equals(k);

            public override int GetHashCode() => (int)Mode ^ Players ^ (Chat << 8) ^ (Recovery << 16);
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            _buildState = BuildState;
            _buildNet = BuildNet;
            _buildPresence = BuildPresence;
            _stateBinding = new GetterValueBinding<string>(Group, "state", () => Guard.GetRetrying(Features.Ui, "panel state", _buildState, _state, ref _stateRetry));
            AddUpdateBinding(_stateBinding);
            AddBinding(new TriggerBinding(Group, "host", () => Trigger("host", () => Mod.Sessions?.StartHosting())));
            AddBinding(new TriggerBinding<string, string>(Group, "join", (target, password) => Trigger("join", () =>
            {
                if (Mod.Settings == null || Mod.Sessions == null) return;
                Mod.Settings.JoinTarget = (target ?? "").Trim();
                Mod.Settings.JoinPassword = password ?? "";
                Mod.Settings.ApplyAndSave();
                Mod.Sessions.StartJoining();
            })));
            AddBinding(new TriggerBinding(Group, "leave", () => Trigger("leave", () => Mod.Sessions?.Leave())));
            AddBinding(new TriggerBinding<string>(Group, "chat", text => Trigger("chat", () => Mod.Sessions?.SendChat(text))));
            AddBinding(new TriggerBinding<int>(Group, "kick", peer => Trigger("kick", () => Mod.Sessions?.Kick((byte)Math.Max(0, Math.Min(255, peer))))));
            AddBinding(new TriggerBinding<string, string, string>(Group, "saveHostSettings", (name, session, password) => Trigger("save settings", () =>
            {
                if (Mod.Settings == null) return;
                Mod.Settings.PlayerName = (name ?? "").Trim();
                Mod.Settings.SessionName = (session ?? "").Trim();
                // review fix: the panel's password field starts empty ("unchanged"), and saving it (also on Join)
                // used to wipe the stored host password; the panel now sends KeepPassword for an untouched field
                if (password != KeepPassword) Mod.Settings.HostPassword = password ?? "";
                Mod.Settings.ApplyAndSave();
            })));

            // recovery after an unexpected end of a client session
            AddBinding(new TriggerBinding(Group, "reconnect", () => Trigger("reconnect", () => Mod.Sessions?.Reconnect())));
            AddBinding(new TriggerBinding(Group, "cancelReconnect", () => Trigger("cancel reconnect", () => Mod.Sessions?.CancelReconnect())));
            AddBinding(new TriggerBinding(Group, "stayOffline", () => Trigger("stay offline", () => Mod.Sessions?.StayOffline())));
            AddBinding(new TriggerBinding(Group, "mainMenu", () => Trigger("main menu", () => Mod.Sessions?.ReturnToMainMenu())));

            // presence branch: network line, remote players' name tags and activities, diagnostics
            AddUpdateBinding(new GetterValueBinding<string>(Group, "net", () => Guard.GetRetrying(Features.Ui, "network line", _buildNet, _net, ref _netRetry)));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "presence", () => Guard.GetRetrying(Features.Ui, "name tags", _buildPresence, _presence, ref _presenceRetry)));
            AddBinding(new TriggerBinding(Group, "writeDiagnostics", () => Trigger("write diagnostics", SkylineTogether.Settings.SkylineTogetherSettings.WriteDiagnostics)));
            AddBinding(new TriggerBinding(Group, "toggleNetHud", () => Trigger("network line toggle", () =>
            {
                if (Mod.Settings == null) return;
                Mod.Settings.ShowNetworkHud = !Mod.Settings.ShowNetworkHud;
                Mod.Settings.ApplyAndSave();
            })));
        }

        protected override void OnDestroy()
        {
            _stateBinding = null;
            base.OnDestroy();
        }

        /// <summary>A panel action; triggers run even when the panel feature tripped (they are the way out of a session).</summary>
        private static void Trigger(string what, Action action) => Guard.RunAlways(Features.Ui, "panel action \"" + what + "\"", action);

        /// <summary>The network HUD line, refreshed twice a second: <c>{show, text}</c>.</summary>
        private string BuildNet()
        {
            SessionManager s = Mod.Sessions;
            if (s == null) return _net;
            double now = s.Clock.Seconds;
            if (now - _netAt < 0.5) return _net;
            _netAt = now;
            bool show = Mod.Settings != null && Mod.Settings.ShowNetworkHud;
            if (s.Host == null && s.Client == null)
            {
                // single player: the same value every time, no string building
                string idle = show ? "{\"show\":true,\"text\":\"\"}" : "{\"show\":false,\"text\":\"\"}";
                _net = idle;
                return _net;
            }
            SessionNetStats n = s.Host != null ? s.Host.GetNetStats() : s.Client.GetNetStats();
            string text = (n.PingMs >= 0 ? "ping " + n.PingMs.ToString(CultureInfo.InvariantCulture) + " ms · " : "")
                          + "in " + (n.InBytesPerSecond / 1024f).ToString("0.0", CultureInfo.InvariantCulture) + " KB/s · out "
                          + (n.OutBytesPerSecond / 1024f).ToString("0.0", CultureInfo.InvariantCulture) + " KB/s"
                          + (n.QueuedBytes > 64 * 1024 ? " · queued " + (n.QueuedBytes / 1024).ToString(CultureInfo.InvariantCulture) + " KB" : "");
            var sb = new StringBuilder(128);
            sb.Append('{');
            Prop(sb, "show", show).Append(',');
            Prop(sb, "text", text);
            sb.Append('}');
            _net = sb.ToString();
            return _net;
        }

        /// <summary>
        /// Remote players for the name tags and the panel, at most 20 times a second:
        /// <c>{tags: [{id, name, color, x, y, on, activity}]}</c> with x, y as fractions of the screen.
        /// </summary>
        private string BuildPresence()
        {
            Presence.PresenceController p = Mod.Presence;
            SessionManager s = Mod.Sessions;
            if (p == null || s == null) return _presence;
            if (p.TagsVersion == _presenceVersion) return _presence;
            double now = s.Clock.Seconds;
            if (now - _presenceAt < 0.05) return _presence;
            _presenceVersion = p.TagsVersion;
            _presenceAt = now;
            var sb = new StringBuilder(256);
            sb.Append("{\"tags\":[");
            bool first = true;
            foreach (Presence.PresenceTag t in p.Tags)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('{');
                Prop(sb, "id", (int)t.Peer).Append(',');
                Prop(sb, "name", t.Name ?? "").Append(',');
                Prop(sb, "color", t.Color ?? "#ffffff").Append(',');
                Prop(sb, "x", t.ScreenX).Append(',');
                Prop(sb, "y", t.ScreenY).Append(',');
                Prop(sb, "on", t.OnScreen).Append(',');
                Prop(sb, "activity", t.Activity ?? "");
                sb.Append('}');
            }
            sb.Append("]}");
            _presence = sb.ToString();
            return _presence;
        }

        private string BuildState()
        {
            SessionManager s = Mod.Sessions;
            if (s == null) return "{}";
            SessionClient client = s.Client;
            double now = s.Clock.Seconds;
            // review fix: since movers are bound, the bound count in the sync line changes nearly every frame, which
            // re-serialized the whole state (players, 100 chat lines) and re-rendered the panel every frame; the line
            // is refreshed twice a second instead (and only during a session)
            if (s.Host != null || client != null)
            {
                if (now - _syncAt >= 0.5 || now < _syncAt)
                {
                    _syncAt = now;
                    string sync = Mod.Sync?.Status ?? "";
                    if (sync != _sync) _sync = sync;
                }
            }
            else if (_sync.Length != 0)
            {
                _sync = "";
            }
            var key = new StateKey
            {
                Mode = s.Mode,
                Players = s.PlayersVersion,
                Chat = s.ChatVersion,
                Recovery = s.RecoveryVersion,
                Probes = Features.Registry.Version,
                ClientState = client != null ? (int)client.State : -1,
                Progress = client != null ? (int)(client.JoinProgress * 100) : 0,
                RecoverySeconds = s.Recovery.Phase == RejoinPhase.Waiting ? (int)Math.Ceiling(s.Recovery.SecondsUntilAttempt(now)) : -1,
                CanHost = s.CanHost,
                CanJoin = s.CanJoin,
                Steam = SessionManager.SteamAvailable,
                LastEnd = s.LastEndReason,
                Sync = _sync,
                Speed = s.Host != null ? s.Host.Speed : client != null ? client.Speed : default(SpeedState),
            };
            if (_hasKey && key.Equals(_key)) return _state;
            _key = key;
            _hasKey = true;
            _state = Serialize(s, _sync, now);
            return _state;
        }

        private static string Serialize(SessionManager s, string sync, double now)
        {
            var sb = new StringBuilder(2048);
            sb.Append('{');
            Prop(sb, "mode", s.Mode.ToString()).Append(',');
            Prop(sb, "canHost", s.CanHost).Append(',');
            Prop(sb, "canJoin", s.CanJoin || (!s.InSession && SessionManager.SteamAvailable && SessionManager.BlockingReason() == null)).Append(',');
            Prop(sb, "steam", SessionManager.SteamAvailable).Append(',');
            Prop(sb, "steamId", s.LocalSteamId.ToString(CultureInfo.InvariantCulture)).Append(',');
            Prop(sb, "directPort", s.DirectPort).Append(',');
            Prop(sb, "playerName", Mod.Settings?.PlayerName ?? "").Append(',');
            Prop(sb, "sessionNameSetting", Mod.Settings?.SessionName ?? "").Append(',');
            Prop(sb, "hasPassword", !string.IsNullOrEmpty(Mod.Settings?.HostPassword)).Append(',');
            Prop(sb, "joinTarget", Mod.Settings?.JoinTarget ?? "").Append(',');
            Prop(sb, "keepPassword", KeepPassword).Append(',');
            Prop(sb, "lastEnd", s.LastEndReason ?? "").Append(',');
            // the self-test (ARCH §13.4): one line, and what blocks hosting/joining if anything
            Prop(sb, "probes", Probes.Summary ?? "").Append(',');
            Prop(sb, "blocked", SessionManager.BlockingReason() ?? "").Append(',');

            string status = "";
            string sessionName = "";
            float progress = 0f;
            SessionClient c = s.Client;
            if (s.Host != null)
            {
                sessionName = s.Host.Options.SessionName;
                status = s.Host.HoldSimulation ? "A player is joining – the game is paused until they are in." : "Hosting";
                // presence branch: name who is joining and how far they are (the player list shows it per player too)
                if (s.Host.HoldSimulation)
                {
                    foreach (PlayerInfo p in s.Host.GetPlayers())
                    {
                        if (p.State == PlayerState.Live) continue;
                        string phase = p.State == PlayerState.Downloading ? "downloading the city" : p.State == PlayerState.Loading ? "loading the city" : "waiting for the host's save";
                        status += " " + p.Name + ": " + phase + (p.State == PlayerState.Downloading || p.State == PlayerState.Loading
                            ? " " + ((int)(p.Progress * 100)).ToString(CultureInfo.InvariantCulture) + " %" : "") + ".";
                    }
                }
            }
            else if (c != null)
            {
                sessionName = c.Session?.SessionName ?? "";
                progress = c.JoinProgress;
                status = ClientStatus(c);
            }
            Prop(sb, "status", status).Append(',');
            Prop(sb, "sync", sync ?? "").Append(',');
            SpeedState speed = s.Host != null ? s.Host.Speed : c != null ? c.Speed : default(SpeedState);
            Prop(sb, "speed", speed.Held ? "paused while a player joins" : speed.Paused ? "paused" : speed.Speed + "×").Append(',');
            Prop(sb, "sessionName", sessionName).Append(',');
            Prop(sb, "progress", progress).Append(',');

            // recovery: {phase, attempt, max, seconds, reason, canReconnect, hostEnded, attempt status}
            RejoinController r = s.Recovery;
            sb.Append("\"recovery\":{");
            Prop(sb, "phase", r.Phase.ToString()).Append(',');
            Prop(sb, "attempt", r.Attempt).Append(',');
            Prop(sb, "max", r.MaxAttempts).Append(',');
            Prop(sb, "seconds", (int)Math.Ceiling(r.SecondsUntilAttempt(now))).Append(',');
            Prop(sb, "reason", r.Reason ?? "").Append(',');
            Prop(sb, "canReconnect", r.CanReconnect).Append(',');
            Prop(sb, "hostEnded", r.StartKind == ClientEndKind.HostEnded || r.StartKind == ClientEndKind.Kicked).Append(',');
            Prop(sb, "attemptStatus", r.Phase == RejoinPhase.Attempting && c != null ? ClientStatus(c) : "");
            sb.Append("},");

            sb.Append("\"players\":[");
            IReadOnlyList<PlayerInfo> players = s.Host != null ? s.Host.GetPlayers() : c != null && c.Players.Count > 0 ? c.Players : r.Recovering ? s.LastPlayers : c?.Players;
            if (players != null)
            {
                bool first = true;
                foreach (PlayerInfo p in players)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('{');
                    Prop(sb, "id", (int)p.Id.Value).Append(',');
                    Prop(sb, "name", p.Name ?? "").Append(',');
                    Prop(sb, "host", p.Id.IsHost).Append(',');
                    Prop(sb, "you", s.Host != null ? p.Id.IsHost : c != null && p.Id == c.LocalPeer).Append(',');
                    Prop(sb, "state", c == null && s.Host == null ? "Offline" : p.State.ToString()).Append(',');
                    Prop(sb, "ping", p.PingMs).Append(',');
                    Prop(sb, "progress", p.Progress);
                    sb.Append('}');
                }
            }
            sb.Append("],\"chat\":[");
            bool firstLine = true;
            foreach (ChatEntry e in s.Chat)
            {
                if (!firstLine) sb.Append(',');
                firstLine = false;
                sb.Append('{');
                Prop(sb, "name", e.Name ?? "").Append(',');
                Prop(sb, "text", e.Text ?? "").Append(',');
                Prop(sb, "system", e.IsSystem);
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string ClientStatus(SessionClient c)
        {
            switch (c.State)
            {
                case ClientState.Connecting: return "Connecting to the host…";
                case ClientState.Handshaking: return "Checking versions and mods…";
                case ClientState.WaitingForSnapshot: return "The host is saving the city…";
                case ClientState.Downloading: return "Downloading the city…";
                case ClientState.Loading: return "Loading the city…";
                case ClientState.Live: return "Connected";
                default: return c.EndReason ?? "";
            }
        }

        private static StringBuilder Prop(StringBuilder sb, string name, string value)
        {
            Str(sb, name);
            sb.Append(':');
            return Str(sb, value);
        }

        private static StringBuilder Prop(StringBuilder sb, string name, bool value)
        {
            Str(sb, name);
            return sb.Append(':').Append(value ? "true" : "false");
        }

        private static StringBuilder Prop(StringBuilder sb, string name, int value)
        {
            Str(sb, name);
            return sb.Append(':').Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static StringBuilder Prop(StringBuilder sb, string name, float value)
        {
            Str(sb, name);
            if (float.IsNaN(value) || float.IsInfinity(value)) value = 0f;
            return sb.Append(':').Append(value.ToString("0.###", CultureInfo.InvariantCulture));
        }

        private static StringBuilder Str(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"');
        }
    }
}
