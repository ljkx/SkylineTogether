using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Colossal.Logging;
using SkylineTogether.Core;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Session;
using SkylineTogether.Session;

namespace SkylineTogether.Diagnostics
{
    /// <summary>
    /// "Write diagnostics" (ARCHITECTURE.md#diagnostics-dump, plain-text subset): session state, players, link numbers, replication counters,
    /// versions, the mod list and the recent log lines, written as a text file next to the mod's log. Passwords and
    /// chat messages are never included; paths in the user's folder are written as %USERPROFILE%
    /// (<see cref="Core.Diagnostics.PathRedactor"/>). Main thread only.
    /// </summary>
    public static class DiagnosticsDump
    {
        /// <summary>
        /// Writes the dump and returns its path for showing (the user's folder as %USERPROFILE%, which Explorer's address
        /// bar opens); null (and a logged error) when writing failed.
        /// </summary>
        public static string Write()
        {
            string text;
            try
            {
                text = Build();
            }
            catch (Exception ex)
            {
                text = "building the diagnostics failed: " + ex + Environment.NewLine;
            }
            text = Core.Diagnostics.PathRedactor.Redact(text);
            try
            {
                string folder = Folder();
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "SkylineTogether-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
                File.WriteAllText(path, text, new UTF8Encoding(false));
                string shown = Core.Diagnostics.PathRedactor.Redact(path);
                Mod.Log?.Info(LogCategory.Diagnostics, "diagnostics written to " + shown);
                return shown;
            }
            catch (Exception ex)
            {
                Mod.Log?.Error(LogCategory.Diagnostics, "writing the diagnostics failed", ex);
                return null;
            }
        }

        private static string Folder()
        {
            string log = (Mod.Log as GameLog)?.LogPath;
            if (!string.IsNullOrEmpty(log))
            {
                string dir = Path.GetDirectoryName(log);
                if (!string.IsNullOrEmpty(dir)) return dir;
            }
            return LogManager.kDefaultLogPath;
        }

        private static string Build()
        {
            var sb = new StringBuilder(16 * 1024);
            SessionManager s = Mod.Sessions;
            Line(sb, "Skyline Together diagnostics, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
            Line(sb, "");
            Section(sb, "Versions");
            Line(sb, "mod assembly: " + typeof(Mod).Assembly.GetName().Version + ", protocol " + ProtocolVersion.Current);
            Safe(sb, () =>
            {
                var info = new GameInfoProvider();
                HandshakeDigests d = info.GetDigests();
                Line(sb, "build: " + GameInfoProvider.BuildText + " (SHA-256 of SkylineTogether.dll, as the installer prints it; from " + GameInfoProvider.BuildSourceDetail + ")");
                Line(sb, "game: " + d.GameVersion + " (" + d.GameFullVersion + "), locale " + d.Locale);
            });
            Safe(sb, () =>
            {
                // local Mods folder or Paradox Mods, and every other copy the game found (two copies conflict)
                List<InstallInfo.InstalledCopy> copies = InstallInfo.Copies();
                Line(sb, "installed: " + copies.Count + " cop" + (copies.Count == 1 ? "y" : "ies") + ", " + InstallInfo.UiModuleCount() + " UI module(s)" + (copies.Count > 1 ? " - INSTALLED MORE THAN ONCE, keep one copy" : ""));
                foreach (InstallInfo.InstalledCopy c in copies) Line(sb, "  " + c.Describe());
            });
            Line(sb, "unity: " + UnityEngine.Application.unityVersion + ", os: " + Environment.OSVersion);

            // ARCHITECTURE.md#probes-and-self-test: what the self-test found and which features are off (and why)
            Section(sb, "Self-test and features");
            Safe(sb, () =>
            {
                foreach (string l in Probes.ReportLines()) Line(sb, l);
                Core.Diagnostics.FeatureHealth blocked = SessionManager.BlockingFeature();
                Line(sb, "hosting/joining blocked: " + (blocked != null ? blocked.Id + " (" + blocked.UnavailableReason + ")" : "no"));
                Line(sb, "vanilla systems held off by the mod: " + Sync.SystemSwitch.HeldCount);
            });

            Section(sb, "Session");
            if (s == null)
            {
                Line(sb, "the session manager is not running");
            }
            else
            {
                Line(sb, "mode: " + s.Mode + ", steam available: " + SessionManager.SteamAvailable + ", can host: " + s.CanHost + ", can join: " + s.CanJoin);
                Line(sb, "last end reason: " + (s.LastEndReason ?? "-"));
                Safe(sb, () =>
                {
                    Core.Session.RejoinController r = s.Recovery;
                    Line(sb, "recovery: " + r.Phase + ", attempt " + r.Attempt + "/" + r.MaxAttempts + ", started by " + r.StartKind + ", can reconnect " + r.CanReconnect
                             + ", rejoins so far " + r.Rejoins + ", city held " + s.HoldCity + (string.IsNullOrEmpty(r.Reason) ? "" : ", reason: " + r.Reason));
                });
                Safe(sb, () => Line(sb, "game mode: " + Game.SceneFlow.GameManager.instance?.gameMode + ", loading: " + Game.SceneFlow.GameManager.instance?.isGameLoading));
                if (s.Host != null)
                {
                    SessionHost h = s.Host;
                    Line(sb, "hosting \"" + h.Options.SessionName + "\", max " + h.Options.MaxPlayers + " players, password " + (string.IsNullOrEmpty(h.Options.Password) ? "no" : "yes")
                             + ", same mods required " + h.Options.RequireSameMods + ", same assets required " + h.Options.RequireSameAssets
                             + ", join anyway with different mods allowed " + h.Options.AllowModMismatchOverride + ", direct port " + (h.Options.Listen.AllowIp ? h.Options.Listen.IpPort.ToString(CultureInfo.InvariantCulture) : "off")
                             + ", join policy " + h.Options.JoinPolicy + ", bans " + (h.Options.Bans?.Count ?? 0) + ", password lockouts tracked " + h.PasswordAttempts.Tracked
                             + " (wrong answers " + h.PasswordAttempts.TotalFailures + ")");
                    Line(sb, "speed: " + h.Speed + ", holding the simulation: " + h.HoldSimulation + ", live peers: " + h.LivePeerCount);
                    NetLine(sb, h.GetNetStats());
                    Line(sb, "connections:");
                    foreach (string p in h.DescribePeers()) Line(sb, "  " + p);
                    // stage timings of the last completed joins (oldest first)
                    Safe(sb, () =>
                    {
                        IReadOnlyList<Core.Session.JoinTimeline> joins = h.RecentJoins;
                        Line(sb, "recent joins (" + joins.Count + "):");
                        foreach (Core.Session.JoinTimeline t in joins) Line(sb, "  " + t.Describe());
                    });
                }
                if (s.Client != null)
                {
                    SessionClient c = s.Client;
                    Line(sb, "client state: " + c.State + ", join progress " + c.JoinProgress.ToString("0.00", CultureInfo.InvariantCulture) + ", local peer " + c.LocalPeer
                             + ", session \"" + (c.Session?.SessionName ?? "?") + "\" hosted by " + (c.Session?.HostName ?? "?"));
                    Line(sb, "speed: " + c.Speed + (c.EndReason != null ? ", end reason: " + c.EndReason + " (" + c.EndKind + ")" : "") + ", entered the city " + c.EnteredCity + ", was live " + c.WasLive);
                    NetLine(sb, c.GetNetStats());
                    // this join's stage timings (and whether the backlog is still being applied)
                    Safe(sb, () => Line(sb, c.Timeline.Describe() + (c.Timeline.CatchingUp ? " (catching up, " + c.Timeline.BacklogRemaining + " batch(es) left)" : "")));
                }
                IReadOnlyList<PlayerInfo> players = s.Host != null ? s.Host.GetPlayers() : s.Client?.Players;
                if (players != null)
                {
                    Line(sb, "players (" + players.Count + "):");
                    foreach (PlayerInfo p in players)
                    {
                        Line(sb, "  " + p.Id + " \"" + p.Name + "\" " + p.Role + " " + p.State + " ping " + p.PingMs + " ms, progress " + p.Progress.ToString("0.00", CultureInfo.InvariantCulture));
                    }
                }
                Line(sb, "system messages (last 20):");
                int skip = 0;
                var system = new List<ChatEntry>();
                foreach (ChatEntry e in s.Chat)
                {
                    if (e.IsSystem) system.Add(e);
                }
                if (system.Count > 20) skip = system.Count - 20;
                for (int i = skip; i < system.Count; i++) Line(sb, "  " + system[i].Text);
            }

            Section(sb, "Replication");
            Safe(sb, () =>
            {
                var sync = Mod.Sync;
                if (sync == null)
                {
                    Line(sb, "not running");
                    return;
                }
                Line(sb, "host: " + sync.IsHost + ", live client: " + sync.IsLiveClient + ", bound entities: " + sync.BoundCount);
                Line(sb, "status: " + sync.Status);
                Line(sb, "last apply: " + sync.LastApply);
                // id tables and quarantine, epochs, deferred queues, per-component ms/frame and bytes/s per channel
                var details = new List<string>();
                sync.Describe(details);
                foreach (string d in details) Line(sb, "  " + d);
            });

            Section(sb, "Presence");
            Safe(sb, () =>
            {
                var presence = Mod.Presence;
                if (presence == null)
                {
                    Line(sb, "not running");
                    return;
                }
                Line(sb, "active: " + presence.Active + ", remote players shown: " + presence.Table.Entries.Count);
                foreach (var t in presence.Tags) Line(sb, "  peer " + t.Peer + " \"" + t.Name + "\" " + (t.OnScreen ? "on screen" : "off screen") + (string.IsNullOrEmpty(t.Activity) ? "" : ", " + t.Activity));
                // tool previews
                Line(sb, "building previews: " + (Features.Previews.IsAvailable ? "on" : "off (" + Features.Previews.UnavailableReason + ")") + ", shown here: " + (Mod.Settings == null || Mod.Settings.ShowBuildingPreviews)
                         + ", sent: last " + presence.LastPreviewShapes + " shapes in " + presence.LastPreviewBytes + " bytes" + (presence.LastPreviewTruncated ? " (truncated)" : "")
                         + ", received malformed: " + presence.Table.BadPreviews + ", unknown version: " + presence.Table.UnknownPreviewVersions);
                foreach (var e in presence.Table.Entries) Line(sb, "  peer " + e.Peer.Value + " preview: " + e.Preview.Count + " shapes" + (e.Preview.Truncated ? " (truncated)" : ""));
            });

            Section(sb, "Settings (passwords omitted)");
            Safe(sb, () =>
            {
                var st = Mod.Settings;
                if (st == null) return;
                Line(sb, "player name: \"" + st.PlayerName + "\", session name: \"" + st.SessionName + "\", max players " + st.MaxPlayers + ", same mods " + st.RequireSameMods + ", same assets " + st.RequireSameAssets
                         + ", join with different mods " + st.AllowJoinWithDifferentMods);
                Line(sb, "direct connections: " + st.AllowDirectConnections + " on port " + st.DirectPort + ", join target set: " + !string.IsNullOrWhiteSpace(st.JoinTarget)
                         + ", network HUD: " + st.ShowNetworkHud);
            });

            Section(sb, "Mods");
            Safe(sb, () =>
            {
                IReadOnlyList<ModEntry> mods = new GameInfoProvider().Mods();
                Line(sb, mods.Count + " other code mods loaded:");
                foreach (ModEntry m in mods)
                {
                    // mod compatibility: a joiner without a mod that stores city data is always refused
                    string cityData = m.StoresCityData ? "; stores city data, a joiner needs it: " + (m.CityDataDetail ?? "") : "";
                    string conflict = Core.Session.CityDataRule.IsOldMultiplayerMod(m) ? "; the old multiplayer mod: nobody can join while it is enabled" : "";
                    Line(sb, "  " + m.Name + " " + m.Version + " (" + Core.Session.ModCompatibility.Classify(m) + cityData + conflict + ")");
                }
            });

            // mod compatibility: which adapters run and what they switch
            Section(sb, "Mod compatibility");
            Safe(sb, () =>
            {
                foreach (string l in Compat.ModCompat.Describe().Split((char)10))
                {
                    if (l.Length > 0) Line(sb, l);
                }
                IReadOnlyDictionary<string, int> missing = Mod.Sync?.MissingAssets;
                if (missing != null && missing.Count > 0)
                {
                    Line(sb, missing.Count + " missing assets (objects using them were left out):");
                    foreach (KeyValuePair<string, int> m in missing) Line(sb, "  " + (Mod.Sync?.ExplainMissing(m.Key) ?? m.Key) + " (" + m.Value + " records)"); // source and advice
                }
            });

            Section(sb, "Recent log lines");
            string[] lines = (Mod.Log as GameLog)?.RecentLines() ?? new string[0];
            foreach (string l in lines) Line(sb, l);
            return sb.ToString();
        }

        private static void NetLine(StringBuilder sb, SessionNetStats n)
        {
            Line(sb, "link: ping " + (n.PingMs >= 0 ? n.PingMs + " ms" : "?") + ", in " + Kb(n.InBytesPerSecond) + " KB/s, out " + Kb(n.OutBytesPerSecond) + " KB/s, queued " + n.QueuedBytes
                     + " bytes, connections " + n.Connections);
        }

        private static string Kb(float bytesPerSecond) => (bytesPerSecond / 1024f).ToString("0.0", CultureInfo.InvariantCulture);

        private static void Section(StringBuilder sb, string title)
        {
            sb.AppendLine();
            sb.Append("== ").AppendLine(title);
        }

        private static void Line(StringBuilder sb, string text) => sb.AppendLine(text);

        private static void Safe(StringBuilder sb, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Line(sb, "(failed: " + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }
    }
}
