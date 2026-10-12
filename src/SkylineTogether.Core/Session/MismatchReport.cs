using System;
using System.Collections.Generic;
using System.Text;

namespace SkylineTogether.Core.Session
{
    /// <summary>One mod whose version differs between the host and the joiner.</summary>
    public sealed class ModVersionDifference
    {
        public string Name;
        public string HostVersion;
        public string ClientVersion;
        public string PlatformId;
    }

    /// <summary>
    /// The structured reason for a compatibility refusal, carried in <see cref="Protocol.RefuseMessage.MismatchReport"/>
    /// so the joiner's UI can show lists (mods to enable, disable or update) instead of one truncated sentence. The
    /// encoding is UTF-8 text, one tab-separated record per line; unknown records are skipped when read, so the format
    /// can grow without breaking older readers.
    /// </summary>
    public sealed class MismatchReport
    {
        /// <summary>Upper bound of records read (defends the reader against a hostile report).</summary>
        public const int MaxRecords = 4096;

        /// <summary>"mods", "game", "build" or "protocol".</summary>
        public string Kind = "mods";
        public string HostGameVersion;
        public string ClientGameVersion;
        public string HostProtocol;
        public string ClientProtocol;
        /// <summary>Mods the host runs and the joiner lacks (the joiner enables or subscribes to them).</summary>
        public readonly List<ModEntry> Missing = new List<ModEntry>();
        /// <summary>Mods the joiner runs and the host does not (the joiner disables them).</summary>
        public readonly List<ModEntry> Extra = new List<ModEntry>();
        /// <summary>Mods both run in different versions (the joiner updates them, or the host does).</summary>
        public readonly List<ModVersionDifference> Versions = new List<ModVersionDifference>();

        public bool HasModDifferences => Missing.Count > 0 || Extra.Count > 0 || Versions.Count > 0;

        /// <summary>
        /// Compares two mod lists by Paradox Mods id (or name) and version (or DLL hash); null when nothing blocks.
        /// Mods that only affect their own player (<see cref="ModCompatibility"/>: UI, tooltips, icons, overlays,
        /// statistics panels) are left out whether one side lacks them or the versions differ.
        /// </summary>
        public static MismatchReport CompareMods(IReadOnlyList<ModEntry> host, IReadOnlyList<ModEntry> client)
        {
            var hostByKey = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (ModEntry m in host) hostByKey[Key(m)] = m;
            var clientByKey = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (ModEntry m in client) clientByKey[Key(m)] = m;
            var report = new MismatchReport { Kind = "mods" };
            foreach (KeyValuePair<string, ModEntry> h in hostByKey)
            {
                clientByKey.TryGetValue(h.Key, out ModEntry c);
                if (ModCompatibility.IsLocalPair(h.Value, c)) continue; // mod compatibility: client-local mods never block
                if (c == null)
                {
                    report.Missing.Add(h.Value);
                }
                else if (!string.Equals(h.Value.Version ?? "", c.Version ?? "", StringComparison.Ordinal)
                         || (!string.IsNullOrEmpty(h.Value.DllHash) && !string.IsNullOrEmpty(c.DllHash)
                             && !string.Equals(h.Value.DllHash, c.DllHash, StringComparison.OrdinalIgnoreCase)))
                {
                    report.Versions.Add(new ModVersionDifference { Name = h.Value.Name, HostVersion = h.Value.Version, ClientVersion = c.Version, PlatformId = h.Value.PlatformId });
                }
            }
            foreach (KeyValuePair<string, ModEntry> c in clientByKey)
            {
                if (!hostByKey.ContainsKey(c.Key) && !ModCompatibility.IsLocalPair(null, c.Value)) report.Extra.Add(c.Value);
            }
            if (!report.HasModDifferences) return null;
            report.Missing.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.ToString(), b.ToString()));
            report.Extra.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.ToString(), b.ToString()));
            report.Versions.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name ?? "", b.Name ?? ""));
            return report;
        }

        /// <summary>The readable one-line summary (English) the refusal's detail text carries.</summary>
        public string Describe()
        {
            var missing = new List<string>();
            foreach (ModEntry m in Missing) missing.Add(m.ToString());
            var extra = new List<string>();
            foreach (ModEntry m in Extra) extra.Add(m.ToString());
            var version = new List<string>();
            foreach (ModVersionDifference v in Versions) version.Add((v.Name ?? "?") + " (host " + (v.HostVersion ?? "?") + ", you " + (v.ClientVersion ?? "?") + ")");
            var sb = new StringBuilder();
            if (missing.Count > 0) sb.Append("Missing (enable these): ").Append(string.Join(", ", missing)).Append(". ");
            if (extra.Count > 0) sb.Append("Not on host (disable these): ").Append(string.Join(", ", extra)).Append(". ");
            if (version.Count > 0) sb.Append("Different version: ").Append(string.Join(", ", version)).Append('.');
            return sb.ToString().Trim();
        }

        public static byte[] Encode(MismatchReport r)
        {
            if (r == null) return null;
            var sb = new StringBuilder(256);
            Line(sb, "kind", r.Kind);
            if (r.HostGameVersion != null || r.ClientGameVersion != null) Line(sb, "game", r.HostGameVersion, r.ClientGameVersion);
            if (r.HostProtocol != null || r.ClientProtocol != null) Line(sb, "protocol", r.HostProtocol, r.ClientProtocol);
            foreach (ModEntry m in r.Missing) Line(sb, "missing", m.Name, m.Version, m.PlatformId);
            foreach (ModEntry m in r.Extra) Line(sb, "extra", m.Name, m.Version, m.PlatformId);
            foreach (ModVersionDifference v in r.Versions) Line(sb, "version", v.Name, v.HostVersion, v.ClientVersion, v.PlatformId);
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        /// <summary>Reads <see cref="Encode"/>; null for an empty or unreadable report (never throws).</summary>
        public static MismatchReport Decode(byte[] data)
        {
            if (data == null || data.Length == 0) return null;
            string text;
            try
            {
                text = Encoding.UTF8.GetString(data);
            }
            catch (Exception)
            {
                return null;
            }
            var r = new MismatchReport();
            int records = 0;
            foreach (string line in text.Split('\n'))
            {
                if (line.Length == 0) continue;
                if (++records > MaxRecords) break;
                string[] f = line.Split('\t');
                switch (f[0])
                {
                    case "kind": r.Kind = Field(f, 1) ?? "mods"; break;
                    case "game": r.HostGameVersion = Field(f, 1); r.ClientGameVersion = Field(f, 2); break;
                    case "protocol": r.HostProtocol = Field(f, 1); r.ClientProtocol = Field(f, 2); break;
                    case "missing": r.Missing.Add(new ModEntry { Name = Field(f, 1), Version = Field(f, 2), PlatformId = Field(f, 3) }); break;
                    case "extra": r.Extra.Add(new ModEntry { Name = Field(f, 1), Version = Field(f, 2), PlatformId = Field(f, 3) }); break;
                    case "version": r.Versions.Add(new ModVersionDifference { Name = Field(f, 1), HostVersion = Field(f, 2), ClientVersion = Field(f, 3), PlatformId = Field(f, 4) }); break;
                }
            }
            return r;
        }

        private static string Key(ModEntry m) => !string.IsNullOrEmpty(m.PlatformId) ? "id:" + m.PlatformId : "name:" + (m.Name ?? "");

        private static string Field(string[] f, int i)
        {
            if (i >= f.Length) return null;
            string s = Protocol.ProtocolText.Sanitize(f[i]);
            return s.Length > 256 ? s.Substring(0, 256) : s;
        }

        private static void Line(StringBuilder sb, string kind, params string[] fields)
        {
            sb.Append(kind);
            foreach (string f in fields)
            {
                sb.Append('\t');
                foreach (char c in f ?? "") sb.Append(c == '\t' || c == '\n' || c == '\r' ? ' ' : c);
            }
            sb.Append('\n');
        }
    }
}
