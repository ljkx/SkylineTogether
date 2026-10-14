using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SkylineTogether.Core;
using SkylineTogether.Core.Diagnostics;
using SkylineTogether.Core.Session;
using SkylineTogether.Diagnostics;
using SkylineTogether.Session;

namespace SkylineTogether.UI
{
    /// <summary>
    /// The in-game performance overlay next to the network HUD (toggled from the panel's diagnostics, for this
    /// game session only): the shared frame budget per feature (average and 95th percentile against its cap), bytes per
    /// second per peer and the apply queue depth. JSON for the "perf" binding, rebuilt at most twice a second and only
    /// while shown:
    /// <c>{show, frame: {ms, budget, over}, features: [{n, p, avg, p95, cap, clip}], peers: [{name, in, out, ping, queued, deferred}], queue}</c>.
    /// Main thread only.
    /// </summary>
    internal static class PerfOverlay
    {
        private const string Hidden = "{\"show\":false}";
        private static string _json = Hidden;
        private static double _at = double.NegativeInfinity;
        private static readonly List<PeerId> PeerScratch = new List<PeerId>();
        private static readonly StringBuilder Sb = new StringBuilder(1024);

        public static bool Shown { get; private set; }

        public static void Toggle()
        {
            Shown = !Shown;
            _at = double.NegativeInfinity;
        }

        public static string Json()
        {
            SessionManager s = Mod.Sessions;
            if (!Shown || s == null || (s.Host == null && s.Client == null)) return _json = Hidden;
            double now = s.Clock.Seconds;
            if (now - _at < 0.5) return _json;
            _at = now;
            StringBuilder sb = Sb;
            sb.Length = 0;
            CultureInfo ci = CultureInfo.InvariantCulture;
            FrameBudgetService budget = Perf.Budget;
            sb.Append("{\"show\":true,\"frame\":{\"ms\":").Append(budget.TotalAverageMs().ToString("0.00", ci))
              .Append(",\"budget\":").Append(budget.FrameMs.ToString("0.#", ci))
              .Append(",\"over\":").Append(budget.OverBudgetFrames.ToString(ci)).Append("},\"features\":[");
            bool first = true;
            IReadOnlyList<FrameBudgetService.Feature> features = budget.Features;
            for (int i = 0; i < features.Count; i++)
            {
                FrameBudgetService.FeatureStats f = budget.GetStats(features[i]);
                if (f.Frames == 0) continue; // never ran in this session (another role, feature off)
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"n\":");
                Str(sb, f.Name);
                sb.Append(",\"p\":").Append(((int)f.Priority).ToString(ci))
                  .Append(",\"avg\":").Append(f.AverageMs.ToString("0.00", ci))
                  .Append(",\"p95\":").Append(f.P95Ms.ToString("0.00", ci))
                  .Append(",\"cap\":").Append(f.CapMs.ToString("0.##", ci))
                  .Append(",\"clip\":").Append(f.ClippedFrames.ToString(ci)).Append('}');
            }
            sb.Append("],\"peers\":[");
            first = true;
            if (s.Host != null)
            {
                SessionHost host = s.Host;
                IReadOnlyList<PlayerInfo> players = host.GetPlayers();
                PeerScratch.Clear();
                host.GetAdmittedPeers(PeerScratch);
                foreach (PeerId peer in PeerScratch)
                {
                    IPeerOutput output = host.GetPeerOutput(peer);
                    if (output == null) continue;
                    ConnectionStats stats = output.Stats;
                    if (!first) sb.Append(',');
                    first = false;
                    Peer(sb, NameOf(players, peer), stats.InBytesPerSecond, stats.OutBytesPerSecond, stats.PingMs, output.QueuedBytes, Mod.Sync?.DeferredCount(peer) ?? -1);
                }
            }
            else
            {
                SessionNetStats n = s.Client.GetNetStats();
                Peer(sb, s.Client.Session?.HostName ?? "host", n.InBytesPerSecond, n.OutBytesPerSecond, n.PingMs, n.QueuedBytes, -1);
            }
            sb.Append("],\"queue\":").Append((Mod.Sync?.PendingBatches ?? 0).ToString(ci)).Append('}');
            return _json = sb.ToString();
        }

        private static string NameOf(IReadOnlyList<PlayerInfo> players, PeerId peer)
        {
            for (int i = 0; i < players.Count; i++)
            {
                if (players[i].Id == peer) return players[i].Name ?? peer.ToString();
            }
            return peer.ToString();
        }

        private static void Peer(StringBuilder sb, string name, float inBytes, float outBytes, int ping, long queued, int deferred)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            sb.Append("{\"name\":");
            Str(sb, name);
            sb.Append(",\"in\":").Append((Sane(inBytes) / 1024f).ToString("0.0", ci))
              .Append(",\"out\":").Append((Sane(outBytes) / 1024f).ToString("0.0", ci))
              .Append(",\"ping\":").Append(ping.ToString(ci))
              .Append(",\"queued\":").Append((queued / 1024).ToString(ci))
              .Append(",\"deferred\":").Append(deferred.ToString(ci)).Append('}');
        }

        private static float Sane(float v) => float.IsNaN(v) || float.IsInfinity(v) || v < 0 ? 0f : v;

        private static void Str(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    default:
                        if (c < ' ' || c == (char)0x2028 || c == (char)0x2029) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
