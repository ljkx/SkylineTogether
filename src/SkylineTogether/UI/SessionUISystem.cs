using System;
using System.Globalization;
using System.Text;
using Colossal.PSI.Common;
using Colossal.UI.Binding;
using Game;
using Game.SceneFlow;
using Game.UI;
using SkylineTogether.Core;
using SkylineTogether.Core.Session;
using SkylineTogether.Session;
using SkylineTogether.Settings;

namespace SkylineTogether.UI
{
    /// <summary>
    /// Bindings of the Skyline Together UI module (group <c>skylineTogether</c>): one JSON <c>state</c> value with
    /// everything the panel shows, rebuilt only when something changed; <c>join</c> with the joiner's progress (stage,
    /// bytes, speed, ETA; at most 4 times a second while joining); <c>net</c> and <c>presence</c>; and triggers for the
    /// panel's actions. Every name here is also checked by tools/ui_smoke.mjs.
    /// </summary>
    public sealed class SessionUISystem : UISystemBase
    {
        public const string Group = "skylineTogether";

        /// <summary>The password value the panel sends when its field was left untouched.</summary>
        public const string KeepPassword = "\u0001keep";

        private string _state = "{}";
        private string _key = "";

        protected override void OnCreate()
        {
            base.OnCreate();
            AddUpdateBinding(new GetterValueBinding<string>(Group, "state", BuildState));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "join", BuildJoin));
            AddBinding(new TriggerBinding(Group, "host", () => Mod.Sessions?.StartHosting()));
            AddBinding(new TriggerBinding<string, string>(Group, "join", (target, password) =>
            {
                if (Mod.Settings == null || Mod.Sessions == null) return;
                Mod.Settings.JoinTarget = (target ?? "").Trim();
                Mod.Settings.JoinPassword = password ?? "";
                Mod.Settings.ApplyAndSave();
                Mod.Sessions.StartJoining();
            }));
            AddBinding(new TriggerBinding(Group, "leave", () => Mod.Sessions?.Leave()));
            AddBinding(new TriggerBinding<string>(Group, "chat", text => Mod.Sessions?.SendChat(text)));
            AddBinding(new TriggerBinding<int>(Group, "kick", peer => Mod.Sessions?.Kick((byte)Math.Max(0, Math.Min(255, peer)))));
            // gameplay branch: builder ↔ viewer
            AddBinding(new TriggerBinding<int>(Group, "toggleRole", peer => Mod.Sessions?.ToggleRole((byte)Math.Max(0, Math.Min(255, peer)))));
            AddBinding(new TriggerBinding<string, string, string>(Group, "saveHostSettings", (name, session, password) =>
            {
                if (Mod.Settings == null) return;
                Mod.Settings.PlayerName = (name ?? "").Trim();
                Mod.Settings.SessionName = (session ?? "").Trim();
                // review fix: the panel's password field starts empty ("unchanged"), and saving it (also on Join)
                // used to wipe the stored host password; the panel now sends KeepPassword for an untouched field
                if (password != KeepPassword)
                {
                    Mod.Settings.HostPassword = password ?? "";
                    // the host reads its options live: a new password applies to the next joiner
                    if (Mod.Sessions?.Host != null) Mod.Sessions.Host.Options.Password = Mod.Settings.HostPassword;
                }
                Mod.Settings.ApplyAndSave();
            }));
            AddBinding(new TriggerBinding<int, bool, bool, int>(Group, "saveHostOptions", (maxPlayers, allowDirect, requireSameMods, port) =>
            {
                if (Mod.Settings == null) return;
                Mod.Settings.MaxPlayers = Math.Max(2, Math.Min(16, maxPlayers));
                Mod.Settings.AllowDirectConnections = allowDirect; // listen sockets: applies the next time the player hosts
                Mod.Settings.RequireSameMods = requireSameMods;
                if (port >= 1024 && port <= 65535) Mod.Settings.DirectPort = port;
                SessionHost host = Mod.Sessions?.Host;
                if (host != null)
                {
                    host.Options.MaxPlayers = Mod.Settings.MaxPlayers;
                    host.Options.RequireSameMods = requireSameMods;
                }
                Mod.Settings.ApplyAndSave();
            }));
            AddBinding(new TriggerBinding<string>(Group, "retryJoin", password => Mod.Sessions?.RetryJoin(password, KeepPassword)));
            AddBinding(new TriggerBinding(Group, "dismissEnd", () => Mod.Sessions?.ClearLastEnd()));
            AddBinding(new TriggerBinding(Group, "copySteamId", () =>
            {
                ulong id = Mod.Invites != null && Mod.Invites.InviteTarget != 0 ? Mod.Invites.InviteTarget : Mod.Sessions?.LocalSteamId ?? 0;
                if (id != 0) Copy(id.ToString(CultureInfo.InvariantCulture));
            }));
            AddBinding(new TriggerBinding<string>(Group, "copyText", text => Copy(text ?? "")));
            AddBinding(new TriggerBinding(Group, "inviteFriends", () => Mod.Invites?.OpenInviteDialog()));

            // presence branch: network line, remote players' name tags and activities, diagnostics
            AddUpdateBinding(new GetterValueBinding<string>(Group, "net", BuildNet));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "presence", BuildPresence));
            AddBinding(new TriggerBinding(Group, "writeDiagnostics", SkylineTogetherSettings.WriteDiagnostics));
            AddBinding(new TriggerBinding(Group, "toggleNetHud", () =>
            {
                if (Mod.Settings == null) return;
                Mod.Settings.ShowNetworkHud = !Mod.Settings.ShowNetworkHud;
                Mod.Settings.ApplyAndSave();
            }));
        }

        private static void Copy(string text)
        {
            try
            {
                UnityEngine.GUIUtility.systemCopyBuffer = text;
                SessionManager.Notify(LocaleSource.Resolve("st.toast.copied"), ProgressState.Complete, "SkylineTogether.copied", 3f);
            }
            catch (Exception ex)
            {
                Mod.Log?.Warn(LogCategory.Ui, "copying to the clipboard failed", ex);
            }
        }

        private string _net = "{}";
        private double _netAt = double.NegativeInfinity;
        private string _presence = "{\"tags\":[]}";
        private int _presenceVersion = -1;
        private double _presenceAt = double.NegativeInfinity;

        /// <summary>The network HUD line, refreshed twice a second: <c>{show, text}</c>.</summary>
        private string BuildNet()
        {
            SessionManager s = Mod.Sessions;
            bool show = Mod.Settings != null && Mod.Settings.ShowNetworkHud;
            if (s == null) return _net;
            double now = s.Clock.Seconds;
            if (now - _netAt < 0.5) return _net;
            _netAt = now;
            string text = "";
            if (s.Host != null || s.Client != null)
            {
                SessionNetStats n = s.Host != null ? s.Host.GetNetStats() : s.Client.GetNetStats();
                text = (n.PingMs >= 0 ? "ping " + n.PingMs.ToString(CultureInfo.InvariantCulture) + " ms · " : "")
                       + "in " + (n.InBytesPerSecond / 1024f).ToString("0.0", CultureInfo.InvariantCulture) + " KB/s · out "
                       + (n.OutBytesPerSecond / 1024f).ToString("0.0", CultureInfo.InvariantCulture) + " KB/s"
                       + (n.QueuedBytes > 64 * 1024 ? " · queued " + (n.QueuedBytes / 1024).ToString(CultureInfo.InvariantCulture) + " KB" : "");
            }
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
            double now = s.Clock.Seconds;
            if (p.TagsVersion == _presenceVersion || now - _presenceAt < 0.05) return _presence;
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

        // ------------------------------------------------------------------------------------------- join progress

        private const string NoJoin = "{\"active\":false}";
        private string _join = NoJoin;
        private double _joinAt = double.NegativeInfinity;
        private long _lastBytes;
        private double _lastBytesAt;
        private double _rate;
        private SessionClient _rateClient;

        /// <summary>
        /// The joiner's progress, at most 4 times a second while joining:
        /// <c>{active, stage, session, target, done, total, bps, eta, load, progress}</c> (bytes, bytes per second, seconds).
        /// </summary>
        private string BuildJoin()
        {
            SessionManager s = Mod.Sessions;
            SessionClient c = s?.Client;
            if (c == null || c.State == ClientState.Live || c.State == ClientState.Ended)
            {
                _rateClient = null;
                _join = NoJoin;
                return _join;
            }
            double now = s.Clock.Seconds;
            if (now - _joinAt < 0.25 && _rateClient == c) return _join;
            long done = c.DownloadedBytes;
            if (_rateClient != c)
            {
                _rateClient = c;
                _rate = 0;
                _lastBytes = done;
                _lastBytesAt = now;
            }
            else if (now > _lastBytesAt)
            {
                double sample = Math.Max(0, done - _lastBytes) / (now - _lastBytesAt);
                _rate = _rate <= 0 ? sample : 0.75 * _rate + 0.25 * sample; // smoothed, so the ETA does not jump
                _lastBytes = done;
                _lastBytesAt = now;
            }
            _joinAt = now;
            long total = c.DownloadTotalBytes;
            double eta = _rate > 1 && total > done ? (total - done) / _rate : -1;
            var sb = new StringBuilder(256);
            sb.Append('{');
            Prop(sb, "active", true).Append(',');
            Prop(sb, "stage", StageOf(c.State)).Append(',');
            Prop(sb, "session", c.Session?.SessionName ?? "").Append(',');
            Prop(sb, "target", s.JoinTarget).Append(',');
            Prop(sb, "done", done).Append(',');
            Prop(sb, "total", total).Append(',');
            Prop(sb, "bps", (long)_rate).Append(',');
            Prop(sb, "eta", (long)Math.Round(eta)).Append(',');
            Prop(sb, "load", c.State == ClientState.Loading ? Math.Max(0f, c.JoinProgress * 2f - 1f) : 0f).Append(',');
            Prop(sb, "progress", c.JoinProgress);
            sb.Append('}');
            _join = sb.ToString();
            return _join;
        }

        private static string StageOf(ClientState state)
        {
            switch (state)
            {
                case ClientState.Connecting: return "connecting";
                case ClientState.Handshaking: return "handshaking";
                case ClientState.WaitingForSnapshot: return "waiting";
                case ClientState.Downloading: return "downloading";
                case ClientState.Loading: return "loading";
                case ClientState.Live: return "live";
                default: return "";
            }
        }

        // --------------------------------------------------------------------------------------------------- state

        private string BuildState()
        {
            SessionManager s = Mod.Sessions;
            if (s == null) return "{}";
            SessionClient client = s.Client;
            // review fix: since movers are bound, the bound count in the sync line changes nearly every frame, which
            // re-serialized the whole state (players, 100 chat lines) and re-rendered the panel every frame; the line
            // is refreshed twice a second instead
            double now = s.Clock.Seconds;
            if (now - _syncAt >= 0.5 || now < _syncAt)
            {
                _syncAt = now;
                _sync = Mod.Sync?.Status ?? "";
                // cheap values that can change without an event, sampled at the same rate
                _inGame = GameManager.instance != null && GameManager.instance.gameMode == GameMode.Game;
                _steam = SessionManager.SteamAvailable;
                _inviteTarget = Mod.Invites?.InviteTarget ?? 0;
            }
            string key = s.Mode + "|" + s.PlayersVersion + "|" + s.ChatVersion + "|" + s.CanHost + "|" + s.CanJoin + "|" + (s.LastEndReason ?? "") + "|" + _sync
                         + "|" + (s.Host != null ? s.Host.Speed.ToString() : s.Client != null ? s.Client.Speed.ToString() : "")
                         + "|" + (client != null ? client.State + ":" + ((int)(client.JoinProgress * 100)).ToString(CultureInfo.InvariantCulture) : "")
                         + "|" + SkylineTogetherSettings.Version + "|" + (s.LastEnd?.Seq ?? 0) + "|" + _inGame + "|" + _steam + "|" + _inviteTarget
                         + "|" + (s.Host != null && s.Host.HoldSimulation);
            if (key == _key) return _state;
            _key = key;
            _state = Serialize(s, _sync, _inGame, _steam, _inviteTarget);
            return _state;
        }

        private string _sync = "";
        private double _syncAt = double.NegativeInfinity;
        private bool _inGame;
        private bool _steam;
        private ulong _inviteTarget;

        private static string RoleOf(SessionRole role)
        {
            switch (role)
            {
                case SessionRole.Host: return "host";
                case SessionRole.CoHost: return "cohost";
                case SessionRole.Builder: return "builder";
                case SessionRole.Viewer: return "viewer";
                default: return "";
            }
        }

        private static string Serialize(SessionManager s, string sync, bool inGame, bool steam, ulong inviteTarget)
        {
            var sb = new StringBuilder(2048);
            SkylineTogetherSettings settings = Mod.Settings;
            sb.Append('{');
            Prop(sb, "mode", s.Mode.ToString()).Append(',');
            Prop(sb, "canHost", s.CanHost).Append(',');
            Prop(sb, "canJoin", s.CanJoin || (!s.InSession && steam)).Append(',');
            Prop(sb, "steam", steam).Append(',');
            Prop(sb, "steamId", s.LocalSteamId.ToString(CultureInfo.InvariantCulture)).Append(',');
            Prop(sb, "directPort", s.DirectPort).Append(',');
            Prop(sb, "playerName", settings?.PlayerName ?? "").Append(',');
            Prop(sb, "sessionNameSetting", settings?.SessionName ?? "").Append(',');
            Prop(sb, "hasPassword", !string.IsNullOrEmpty(settings?.HostPassword)).Append(',');
            Prop(sb, "joinTarget", settings?.JoinTarget ?? "").Append(',');
            Prop(sb, "keepPassword", KeepPassword).Append(',');
            Prop(sb, "lastEnd", s.LastEndReason ?? "").Append(',');
            Prop(sb, "inGame", inGame).Append(',');
            Prop(sb, "maxPlayers", settings?.MaxPlayers ?? 8).Append(',');
            Prop(sb, "allowDirect", settings != null && settings.AllowDirectConnections).Append(',');
            Prop(sb, "requireSameMods", settings == null || settings.RequireSameMods).Append(',');
            Prop(sb, "portSetting", settings?.DirectPort ?? SessionManager.DefaultDirectPort).Append(',');
            Prop(sb, "canInvite", steam && inviteTarget != 0).Append(',');
            Prop(sb, "inviteSteamId", inviteTarget != 0 ? inviteTarget.ToString(CultureInfo.InvariantCulture) : "").Append(',');

            string status = "";
            string sessionName = "";
            float progress = 0f;
            bool hold = false;
            if (s.Host != null)
            {
                sessionName = s.Host.Options.SessionName;
                hold = s.Host.HoldSimulation;
                status = hold ? LocaleSource.Resolve("st.ui.session.holding") : "Hosting";
                // presence branch: name who is joining and how far they are (the player list shows it per player too)
                if (hold)
                {
                    foreach (PlayerInfo p in s.Host.GetPlayers())
                    {
                        if (p.State == PlayerState.Live) continue;
                        string phase = p.State == PlayerState.Downloading ? "st.toast.playerDownloading" : p.State == PlayerState.Loading ? "st.toast.playerLoading" : "st.toast.playerJoining";
                        status += " " + LocaleSource.Resolve(phase, "NAME", p.Name ?? "") + (p.State == PlayerState.Downloading || p.State == PlayerState.Loading
                            ? " (" + ((int)(p.Progress * 100)).ToString(CultureInfo.InvariantCulture) + " %)" : "") + ".";
                    }
                }
            }
            else if (s.Client != null)
            {
                SessionClient c = s.Client;
                sessionName = c.Session?.SessionName ?? "";
                progress = c.JoinProgress;
                switch (c.State)
                {
                    case ClientState.Connecting: status = LocaleSource.Resolve("st.ui.join.stage.connecting") + "…"; break;
                    case ClientState.Handshaking: status = LocaleSource.Resolve("st.ui.join.stage.handshaking") + "…"; break;
                    case ClientState.WaitingForSnapshot: status = LocaleSource.Resolve("st.ui.join.stage.waiting") + "…"; break;
                    case ClientState.Downloading: status = LocaleSource.Resolve("st.ui.join.stage.downloading") + "…"; break;
                    case ClientState.Loading: status = LocaleSource.Resolve("st.ui.join.stage.loading") + "…"; break;
                    case ClientState.Live: status = LocaleSource.Resolve("st.ui.session.connected"); break;
                    default: status = c.EndReason ?? ""; break;
                }
            }
            Prop(sb, "status", status).Append(',');
            Prop(sb, "hold", hold).Append(',');
            Prop(sb, "stage", s.Client != null ? StageOf(s.Client.State) : "").Append(',');
            Prop(sb, "sync", sync ?? "").Append(',');
            SpeedState speed = s.Host != null ? s.Host.Speed : s.Client != null ? s.Client.Speed : default(SpeedState);
            Prop(sb, "speed", speed.Held ? "paused while a player joins" : speed.Paused ? "paused" : speed.Speed + "×").Append(',');
            Prop(sb, "speedValue", speed.Speed).Append(',');
            Prop(sb, "speedPaused", speed.Paused).Append(',');
            Prop(sb, "speedHeld", speed.Held).Append(',');
            Prop(sb, "sessionName", sessionName).Append(',');
            Prop(sb, "progress", progress).Append(',');

            sb.Append("\"end\":");
            WriteEnd(sb, s.LastEnd);
            sb.Append(',');

            sb.Append("\"players\":[");
            var players = s.Host != null ? s.Host.GetPlayers() : s.Client?.Players;
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
                    Prop(sb, "you", s.Host != null ? p.Id.IsHost : s.Client != null && p.Id == s.Client.LocalPeer).Append(',');
                    Prop(sb, "state", p.State.ToString()).Append(',');
                    Prop(sb, "ping", p.PingMs).Append(',');
                    Prop(sb, "progress", p.Progress).Append(',');
                    // the same colour as the player's cursor and name tag (PresenceController palette)
                    Prop(sb, "color", Presence.PresenceController.CssColor(Presence.PresenceController.ColorOf(p.Id))).Append(',');
                    Prop(sb, "role", p.Id.IsHost ? "host" : RoleOf(p.Role));
                    sb.Append('}');
                }
            }
            sb.Append("],\"chat\":[");
            PeerId local = s.Client != null ? s.Client.LocalPeer : PeerId.Host;
            for (int i = 0; i < s.Chat.Count; i++)
            {
                ChatEntry e = s.Chat[i];
                if (i > 0) sb.Append(',');
                sb.Append('{');
                Prop(sb, "name", e.Name ?? "").Append(',');
                Prop(sb, "text", e.Text ?? "").Append(',');
                Prop(sb, "system", e.IsSystem).Append(',');
                Prop(sb, "seq", s.ChatSeqAt(i)).Append(',');
                Prop(sb, "you", !e.IsSystem && e.From == local).Append(',');
                Prop(sb, "color", e.IsSystem ? "" : Presence.PresenceController.CssColor(Presence.PresenceController.ColorOf(e.From)));
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>
        /// The error screen's model: <c>{seq, kind, refuse, text, target, reportKind, missing, extra, versions, hostGame,
        /// yourGame, hostProto, yourProto}</c>, or null.
        /// </summary>
        private static void WriteEnd(StringBuilder sb, SessionEndInfo end)
        {
            if (end == null)
            {
                sb.Append("null");
                return;
            }
            sb.Append('{');
            Prop(sb, "seq", end.Seq).Append(',');
            Prop(sb, "kind", KindOf(end.Kind)).Append(',');
            Prop(sb, "refuse", end.Kind == ClientEndKind.Refused ? end.Refuse.ToString() : "").Append(',');
            Prop(sb, "text", end.Text ?? "").Append(',');
            Prop(sb, "target", end.Target ?? "").Append(',');
            MismatchReport r = end.Report;
            Prop(sb, "reportKind", r?.Kind ?? "").Append(',');
            Prop(sb, "hostGame", r?.HostGameVersion ?? "").Append(',');
            Prop(sb, "yourGame", r?.ClientGameVersion ?? "").Append(',');
            Prop(sb, "hostProto", r?.HostProtocol ?? "").Append(',');
            Prop(sb, "yourProto", r?.ClientProtocol ?? "").Append(',');
            sb.Append("\"missing\":[");
            if (r != null)
            {
                for (int i = 0; i < r.Missing.Count && i < MaxListed; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('{');
                    Prop(sb, "name", r.Missing[i].Name ?? "").Append(',');
                    Prop(sb, "version", r.Missing[i].Version ?? "").Append(',');
                    Prop(sb, "id", r.Missing[i].PlatformId ?? "");
                    sb.Append('}');
                }
            }
            sb.Append("],\"extra\":[");
            if (r != null)
            {
                for (int i = 0; i < r.Extra.Count && i < MaxListed; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('{');
                    Prop(sb, "name", r.Extra[i].Name ?? "").Append(',');
                    Prop(sb, "version", r.Extra[i].Version ?? "").Append(',');
                    Prop(sb, "id", r.Extra[i].PlatformId ?? "");
                    sb.Append('}');
                }
            }
            sb.Append("],\"versions\":[");
            if (r != null)
            {
                for (int i = 0; i < r.Versions.Count && i < MaxListed; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('{');
                    Prop(sb, "name", r.Versions[i].Name ?? "").Append(',');
                    Prop(sb, "host", r.Versions[i].HostVersion ?? "").Append(',');
                    Prop(sb, "you", r.Versions[i].ClientVersion ?? "");
                    sb.Append('}');
                }
            }
            sb.Append("]}");
        }

        private const int MaxListed = 200;

        private static string KindOf(ClientEndKind kind)
        {
            switch (kind)
            {
                case ClientEndKind.Left: return "left";
                case ClientEndKind.Refused: return "refused";
                case ClientEndKind.PasswordRequired: return "passwordRequired";
                case ClientEndKind.Kicked: return "kicked";
                case ClientEndKind.SessionEnded: return "sessionEnded";
                case ClientEndKind.Timeout: return "timeout";
                case ClientEndKind.ConnectionLost: return "connectionLost";
                case ClientEndKind.Failed: return "failed";
                default: return "";
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

        private static StringBuilder Prop(StringBuilder sb, string name, long value)
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
