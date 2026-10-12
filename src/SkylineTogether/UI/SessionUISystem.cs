using System;
using System.Globalization;
using System.Text;
using Colossal.UI.Binding;
using Game.UI;
using SkylineTogether.Core;
using SkylineTogether.Core.Session;
using SkylineTogether.Session;

namespace SkylineTogether.UI
{
    /// <summary>
    /// Bindings of the Skyline Together panel (group <c>skylineTogether</c>): one JSON <c>state</c> value with
    /// everything the panel shows, rebuilt only when something changed, and triggers for the panel's actions.
    /// </summary>
    public sealed class SessionUISystem : UISystemBase
    {
        public const string Group = "skylineTogether";

        private string _state = "{}";
        private string _key = "";

        protected override void OnCreate()
        {
            base.OnCreate();
            AddUpdateBinding(new GetterValueBinding<string>(Group, "state", BuildState));
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
            AddBinding(new TriggerBinding<string, string, string>(Group, "saveHostSettings", (name, session, password) =>
            {
                if (Mod.Settings == null) return;
                Mod.Settings.PlayerName = (name ?? "").Trim();
                Mod.Settings.SessionName = (session ?? "").Trim();
                // review fix: the panel's password field starts empty ("unchanged"), and saving it (also on Join)
                // used to wipe the stored host password; the panel now sends KeepPassword for an untouched field
                if (password != KeepPassword) Mod.Settings.HostPassword = password ?? "";
                Mod.Settings.ApplyAndSave();
            }));

            // presence branch: network line, remote players' name tags and activities, diagnostics
            AddUpdateBinding(new GetterValueBinding<string>(Group, "net", BuildNet));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "presence", BuildPresence));
            AddBinding(new TriggerBinding(Group, "writeDiagnostics", SkylineTogether.Settings.SkylineTogetherSettings.WriteDiagnostics));
            AddBinding(new TriggerBinding(Group, "toggleNetHud", () =>
            {
                if (Mod.Settings == null) return;
                Mod.Settings.ShowNetworkHud = !Mod.Settings.ShowNetworkHud;
                Mod.Settings.ApplyAndSave();
            }));
        }

        /// <summary>The password value the panel sends when its field was left untouched.</summary>
        public const string KeepPassword = "\u0001keep";

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

        private string BuildState()
        {
            SessionManager s = Mod.Sessions;
            if (s == null) return "{}";
            SessionClient client = s.Client;
            string key = s.Mode + "|" + s.PlayersVersion + "|" + s.ChatVersion + "|" + s.CanHost + "|" + s.CanJoin + "|" + (s.LastEndReason ?? "") + "|" + (Mod.Sync?.Status ?? "")
                         + "|" + (s.Host != null ? s.Host.Speed.ToString() : s.Client != null ? s.Client.Speed.ToString() : "")
                         + "|" + (client != null ? client.State + ":" + ((int)(client.JoinProgress * 100)).ToString(CultureInfo.InvariantCulture) : "");
            if (key == _key) return _state;
            _key = key;
            _state = Serialize(s);
            return _state;
        }

        private static string Serialize(SessionManager s)
        {
            var sb = new StringBuilder(2048);
            sb.Append('{');
            Prop(sb, "mode", s.Mode.ToString()).Append(',');
            Prop(sb, "canHost", s.CanHost).Append(',');
            Prop(sb, "canJoin", s.CanJoin || (!s.InSession && SessionManager.SteamAvailable)).Append(',');
            Prop(sb, "steam", SessionManager.SteamAvailable).Append(',');
            Prop(sb, "steamId", s.LocalSteamId.ToString(CultureInfo.InvariantCulture)).Append(',');
            Prop(sb, "directPort", s.DirectPort).Append(',');
            Prop(sb, "playerName", Mod.Settings?.PlayerName ?? "").Append(',');
            Prop(sb, "sessionNameSetting", Mod.Settings?.SessionName ?? "").Append(',');
            Prop(sb, "hasPassword", !string.IsNullOrEmpty(Mod.Settings?.HostPassword)).Append(',');
            Prop(sb, "joinTarget", Mod.Settings?.JoinTarget ?? "").Append(',');
            Prop(sb, "keepPassword", KeepPassword).Append(',');
            Prop(sb, "lastEnd", s.LastEndReason ?? "").Append(',');

            string status = "";
            string sessionName = "";
            float progress = 0f;
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
            else if (s.Client != null)
            {
                SessionClient c = s.Client;
                sessionName = c.Session?.SessionName ?? "";
                progress = c.JoinProgress;
                switch (c.State)
                {
                    case ClientState.Connecting: status = "Connecting to the host…"; break;
                    case ClientState.Handshaking: status = "Checking versions and mods…"; break;
                    case ClientState.WaitingForSnapshot: status = "The host is saving the city…"; break;
                    case ClientState.Downloading: status = "Downloading the city…"; break;
                    case ClientState.Loading: status = "Loading the city…"; break;
                    case ClientState.Live: status = "Connected"; break;
                    default: status = c.EndReason ?? ""; break;
                }
            }
            Prop(sb, "status", status).Append(',');
            Prop(sb, "sync", Mod.Sync?.Status ?? "").Append(',');
            SpeedState speed = s.Host != null ? s.Host.Speed : s.Client != null ? s.Client.Speed : default(SpeedState);
            Prop(sb, "speed", speed.Held ? "paused while a player joins" : speed.Paused ? "paused" : speed.Speed + "×").Append(',');
            Prop(sb, "sessionName", sessionName).Append(',');
            Prop(sb, "progress", progress).Append(',');

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
