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

    /// <summary>One mod setting whose value differs between the host and the joiner (the host's value applies).</summary>
    public sealed class SettingDifference
    {
        public string Mod;
        public string Key;
        public string HostValue;
        public string ClientValue;

        public override string ToString() => (Mod ?? "?") + " " + (Key ?? "?") + " (host " + (HostValue ?? "?") + ", you " + (ClientValue ?? "?") + ")";
    }

    /// <summary>
    /// The structured reason for a compatibility refusal, carried in <see cref="Protocol.RefuseMessage.MismatchReport"/>
    /// so the joiner's UI can show lists (mods to enable, disable or update) instead of one truncated sentence, and the
    /// differences an admitted joiner is told about (<see cref="Protocol.ControlKind.JoinNotice"/>, kind "notice"). The
    /// encoding is UTF-8 text, one tab-separated record per line; unknown records are skipped when read, so the format
    /// can grow without breaking older readers. The "overridable" and "joined-anyway" records were added that way: a
    /// refusal the joiner may pass with "join anyway" carries the first, the notice of a joiner that did the second; the
    /// <see cref="Protocol.RefuseMessage"/> around the report keeps its layout (docs/dev/releasing.md). The city-data
    /// records ("city-data", "host-city-data", "city-data-checked", "rest-overridable") and the old multiplayer mod's
    /// ("conflict-host", "conflict-you", kind "conflict") were added the same way (<see cref="CityDataRule"/>).
    /// </summary>
    public sealed class MismatchReport
    {
        /// <summary>Upper bound of records read (defends the reader against a hostile report).</summary>
        public const int MaxRecords = 4096;

        /// <summary>
        /// "mods", "game", "build", "protocol", "content" (DLC the joiner lacks), "assets" (asset packs differ and the host
        /// requires the same), "conflict" (the old multiplayer mod is loaded on one side) or "notice" (an admitted joiner's
        /// differences).
        /// </summary>
        public string Kind = "mods";
        public string HostGameVersion;
        public string ClientGameVersion;
        public string HostProtocol;
        public string ClientProtocol;
        /// <summary>The build ids (<see cref="BuildId"/>, 12 hex digits) of a "build" mismatch.</summary>
        public string HostBuild;
        public string ClientBuild;
        /// <summary>Mods the host runs and the joiner lacks (the joiner enables or subscribes to them).</summary>
        public readonly List<ModEntry> Missing = new List<ModEntry>();
        /// <summary>Mods the joiner runs and the host does not (the joiner disables them).</summary>
        public readonly List<ModEntry> Extra = new List<ModEntry>();
        /// <summary>Mods both run in different versions (the joiner updates them, or the host does).</summary>
        public readonly List<ModVersionDifference> Versions = new List<ModVersionDifference>();

        /// <summary>DLC and content packs the host has and the joiner lacks (name, id in <see cref="ModEntry.PlatformId"/>).</summary>
        public readonly List<ModEntry> ContentMissing = new List<ModEntry>();
        /// <summary>DLC and content packs the joiner has and the host does not.</summary>
        public readonly List<ModEntry> ContentExtra = new List<ModEntry>();
        /// <summary>Asset packs the host has and the joiner lacks (name, version, Paradox Mods id).</summary>
        public readonly List<ModEntry> AssetsMissing = new List<ModEntry>();
        /// <summary>Asset packs the joiner has and the host does not.</summary>
        public readonly List<ModEntry> AssetsExtra = new List<ModEntry>();
        /// <summary>Asset packs both have with different contents (versions, or prefab counts when the versions read the same).</summary>
        public readonly List<ModVersionDifference> AssetVersions = new List<ModVersionDifference>();
        /// <summary>The asset packs differ, but one side's list was too long to send, so they could not be named.</summary>
        public bool AssetsUnknown;
        /// <summary>Mod settings whose values differ; the host's values apply for the session.</summary>
        public readonly List<SettingDifference> Settings = new List<SettingDifference>();

        /// <summary>
        /// A refusal (kind "mods" or "assets") the joiner may pass: the host lets players join with different mods, and a
        /// hello with <see cref="Protocol.HelloJoinFlags.AcceptModMismatch"/> is admitted despite these differences.
        /// </summary>
        public bool Overridable;

        /// <summary>
        /// A notice (kind "notice") of a joiner the host admitted only because it chose "join anyway": the mod or asset pack
        /// differences in it would have refused the join otherwise.
        /// </summary>
        public bool JoinedAnyway;

        /// <summary>
        /// A refusal (kind "mods"): the host's mods that store data in the city and the joiner lacks; never overridable
        /// (<see cref="CityDataRule"/>). Each entry is also in <see cref="Missing"/>; <see cref="ModEntry.CityDataInCity"/>
        /// says whether the host's current city holds its data.
        /// </summary>
        public readonly List<ModEntry> CityData = new List<ModEntry>();

        /// <summary>
        /// A notice (kind "notice"): every mod of the host's that stores city data, for the joiner's check before it loads
        /// the city (<see cref="CityDataRule.CheckBeforeLoad"/>). Not a difference.
        /// </summary>
        public readonly List<ModEntry> HostCityData = new List<ModEntry>();

        /// <summary>A notice from a host that checked city data (<see cref="HostCityData"/> is complete, possibly empty).</summary>
        public bool CityDataChecked;

        /// <summary>
        /// A refusal with <see cref="CityData"/>: the host lets players join with different mods, so the other differences
        /// in it can be passed with "join anyway" once the city-data mods are enabled.
        /// </summary>
        public bool RestOverridable;

        /// <summary>Kind "conflict": the host runs the old multiplayer mod (this entry), or null.</summary>
        public ModEntry HostConflict;

        /// <summary>Kind "conflict": the joiner runs the old multiplayer mod (this entry), or null.</summary>
        public ModEntry ClientConflict;

        /// <summary>
        /// The locale key of the refusal's detail text (<c>NetText</c>; null: none) and its parameters (name, value, ...),
        /// so the joiner reads the detail in its own language ("text" record). Carried in every refusal of protocol 6 and
        /// later (kind "refusal" when nothing else is reported).
        /// </summary>
        public string TextKey;
        public string[] TextArgs = Array.Empty<string>();

        /// <summary>
        /// True for a report that carries only a refusal's text and retry time (kind "refusal"): nothing about versions,
        /// builds, mods, DLC, asset packs or settings (what a joiner learns before the password).
        /// </summary>
        public bool CarriesNoDetails => Kind == "refusal" && HostGameVersion == null && ClientGameVersion == null && HostProtocol == null && ClientProtocol == null
                                        && HostBuild == null && ClientBuild == null && Missing.Count == 0 && Extra.Count == 0 && Versions.Count == 0
                                        && ContentMissing.Count == 0 && ContentExtra.Count == 0 && AssetsMissing.Count == 0 && AssetsExtra.Count == 0
                                        && AssetVersions.Count == 0 && Settings.Count == 0 && CityData.Count == 0 && HostCityData.Count == 0
                                        && HostConflict == null && ClientConflict == null;

        /// <summary>Seconds after which a new attempt may succeed (a password lockout, a join cooldown; "retry-after" record); 0: none.</summary>
        public int RetryAfterSeconds;

        /// <summary>The entries of <see cref="Missing"/> that are not in <see cref="CityData"/> (the overridable ones).</summary>
        public List<ModEntry> OtherMissing()
        {
            var list = new List<ModEntry>();
            foreach (ModEntry m in Missing)
            {
                if (!CityDataRule.Has(CityData, m)) list.Add(m);
            }
            return list;
        }

        public bool HasModDifferences => Missing.Count > 0 || Extra.Count > 0 || Versions.Count > 0;

        public bool HasContentDifferences => ContentMissing.Count > 0 || ContentExtra.Count > 0;

        public bool HasAssetDifferences => AssetsMissing.Count > 0 || AssetsExtra.Count > 0 || AssetVersions.Count > 0 || AssetsUnknown;

        /// <summary>True when the report lists anything at all.</summary>
        public bool HasDifferences => HasModDifferences || HasContentDifferences || HasAssetDifferences || Settings.Count > 0;

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

        /// <summary>The readable one-line summary (English) the refusal's detail text and the log carry.</summary>
        public string Describe()
        {
            var missing = new List<string>();
            foreach (ModEntry m in OtherMissing()) missing.Add(m.ToString());
            var extra = new List<string>();
            foreach (ModEntry m in Extra) extra.Add(m.ToString());
            var version = new List<string>();
            foreach (ModVersionDifference v in Versions) version.Add((v.Name ?? "?") + " (host " + (v.HostVersion ?? "?") + ", you " + (v.ClientVersion ?? "?") + ")");
            var sb = new StringBuilder();
            if (ClientConflict != null) sb.Append(ConflictText(false)).Append(' ');
            if (HostConflict != null) sb.Append(ConflictText(true)).Append(' ');
            if (CityData.Count > 0)
            {
                var required = new List<string>();
                foreach (ModEntry m in CityData) required.Add(CityDataText(m));
                sb.Append("Enable these mods to join: ").Append(string.Join(", ", required)).Append(". ");
                if (missing.Count > 0 || extra.Count > 0 || version.Count > 0) sb.Append("Other differences: ");
            }
            if (missing.Count > 0) sb.Append("Missing (enable these): ").Append(string.Join(", ", missing)).Append(". ");
            if (extra.Count > 0) sb.Append("Not on host (disable these): ").Append(string.Join(", ", extra)).Append(". ");
            if (version.Count > 0) sb.Append("Different version: ").Append(string.Join(", ", version)).Append(". ");
            if (ContentMissing.Count > 0) sb.Append("DLC or content packs you lack: ").Append(Join(ContentMissing)).Append(". ");
            if (ContentExtra.Count > 0) sb.Append("DLC or content packs the host lacks: ").Append(Join(ContentExtra)).Append(". ");
            if (AssetsMissing.Count > 0) sb.Append("Asset packs you lack: ").Append(Join(AssetsMissing)).Append(". ");
            if (AssetsExtra.Count > 0) sb.Append("Asset packs the host lacks: ").Append(Join(AssetsExtra)).Append(". ");
            if (AssetVersions.Count > 0)
            {
                var packs = new List<string>();
                foreach (ModVersionDifference v in AssetVersions) packs.Add(Named(v.Name, v.PlatformId) + " (host " + (v.HostVersion ?? "?") + ", you " + (v.ClientVersion ?? "?") + ")");
                sb.Append("Asset packs in another version: ").Append(string.Join(", ", packs)).Append(". ");
            }
            if (AssetsUnknown) sb.Append("The asset packs differ (too many to list). ");
            if (Settings.Count > 0)
            {
                var settings = new List<string>();
                foreach (SettingDifference d in Settings) settings.Add(d.ToString());
                sb.Append("Mod settings set to the host's values: ").Append(string.Join(", ", settings)).Append('.');
            }
            return sb.ToString().Trim();
        }

        /// <summary>
        /// One required mod with the reason: <c>ZoningToolkit (this city contains its data – loading it without the mod
        /// would crash the game)</c>, or "it can store data in a city" when the host's city holds none of it yet.
        /// </summary>
        public static string CityDataText(ModEntry m)
        {
            string name = m?.Name ?? "?";
            return m != null && m.CityDataInCity
                ? name + " (this city contains its data – loading it without the mod would crash the game)"
                : name + " (it can store data in a city – loading the city without the mod can crash the game)";
        }

        /// <summary>The old multiplayer mod's message, for the side that has it (<paramref name="host"/>: the host has it).</summary>
        public static string ConflictText(bool host)
        {
            return host
                ? "The host has the '" + CityDataRule.OldMultiplayerModTitle + "' enabled – two multiplayer mods at once conflict. The host has to disable it and restart the game."
                : "Disable the '" + CityDataRule.OldMultiplayerModTitle + "' – two multiplayer mods at once conflict. Disable it, restart the game and join again.";
        }

        /// <summary>A pack's name with its id: <c>Name (Paradox Mods 123)</c>, or the id alone.</summary>
        public static string Named(string name, string id)
        {
            string n = string.IsNullOrEmpty(name) ? null : name;
            if (string.IsNullOrEmpty(id)) return n ?? "?";
            return n != null ? n + " (Paradox Mods " + id + ")" : "Paradox Mods " + id;
        }

        private static string Join(List<ModEntry> entries)
        {
            var names = new List<string>(entries.Count);
            foreach (ModEntry e in entries) names.Add(Named(e.Name, e.PlatformId));
            return string.Join(", ", names);
        }

        public static byte[] Encode(MismatchReport r)
        {
            if (r == null) return null;
            var sb = new StringBuilder(256);
            Line(sb, "kind", r.Kind);
            if (r.HostGameVersion != null || r.ClientGameVersion != null) Line(sb, "game", r.HostGameVersion, r.ClientGameVersion);
            if (r.HostProtocol != null || r.ClientProtocol != null) Line(sb, "protocol", r.HostProtocol, r.ClientProtocol);
            if (r.HostBuild != null || r.ClientBuild != null) Line(sb, "build", r.HostBuild, r.ClientBuild);
            foreach (ModEntry m in r.Missing) Line(sb, "missing", m.Name, m.Version, m.PlatformId);
            foreach (ModEntry m in r.Extra) Line(sb, "extra", m.Name, m.Version, m.PlatformId);
            foreach (ModVersionDifference v in r.Versions) Line(sb, "version", v.Name, v.HostVersion, v.ClientVersion, v.PlatformId);
            foreach (ModEntry m in r.ContentMissing) Line(sb, "content-missing", m.Name, m.Version, m.PlatformId);
            foreach (ModEntry m in r.ContentExtra) Line(sb, "content-extra", m.Name, m.Version, m.PlatformId);
            foreach (ModEntry m in r.AssetsMissing) Line(sb, "asset-missing", m.Name, m.Version, m.PlatformId);
            foreach (ModEntry m in r.AssetsExtra) Line(sb, "asset-extra", m.Name, m.Version, m.PlatformId);
            foreach (ModVersionDifference v in r.AssetVersions) Line(sb, "asset-version", v.Name, v.HostVersion, v.ClientVersion, v.PlatformId);
            if (r.AssetsUnknown) Line(sb, "assets-unknown");
            foreach (SettingDifference d in r.Settings) Line(sb, "setting", d.Mod, d.Key, d.HostValue, d.ClientValue);
            if (r.Overridable) Line(sb, "overridable");
            if (r.JoinedAnyway) Line(sb, "joined-anyway");
            foreach (ModEntry m in r.CityData) Line(sb, "city-data", m.Name, m.Version, m.PlatformId, m.CityDataInCity ? "1" : "0");
            foreach (ModEntry m in r.HostCityData) Line(sb, "host-city-data", m.Name, m.Version, m.PlatformId, m.CityDataInCity ? "1" : "0");
            if (r.CityDataChecked) Line(sb, "city-data-checked");
            if (r.RestOverridable) Line(sb, "rest-overridable");
            if (r.HostConflict != null) Line(sb, "conflict-host", r.HostConflict.Name, r.HostConflict.Version, r.HostConflict.PlatformId);
            if (r.ClientConflict != null) Line(sb, "conflict-you", r.ClientConflict.Name, r.ClientConflict.Version, r.ClientConflict.PlatformId);
            if (!string.IsNullOrEmpty(r.TextKey))
            {
                var fields = new List<string> { r.TextKey };
                string[] args = r.TextArgs ?? Array.Empty<string>();
                for (int i = 0; i + 1 < args.Length && i < 16; i += 2)
                {
                    fields.Add(args[i] ?? "");
                    fields.Add(args[i + 1] ?? "");
                }
                Line(sb, "text", fields.ToArray());
            }
            if (r.RetryAfterSeconds > 0) Line(sb, "retry-after", r.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
                    case "build": r.HostBuild = Field(f, 1); r.ClientBuild = Field(f, 2); break;
                    case "missing": r.Missing.Add(new ModEntry { Name = Field(f, 1), Version = Field(f, 2), PlatformId = Field(f, 3) }); break;
                    case "extra": r.Extra.Add(new ModEntry { Name = Field(f, 1), Version = Field(f, 2), PlatformId = Field(f, 3) }); break;
                    case "version": r.Versions.Add(new ModVersionDifference { Name = Field(f, 1), HostVersion = Field(f, 2), ClientVersion = Field(f, 3), PlatformId = Field(f, 4) }); break;
                    case "content-missing": r.ContentMissing.Add(new ModEntry { Name = Field(f, 1), Version = Field(f, 2), PlatformId = Field(f, 3) }); break;
                    case "content-extra": r.ContentExtra.Add(new ModEntry { Name = Field(f, 1), Version = Field(f, 2), PlatformId = Field(f, 3) }); break;
                    case "asset-missing": r.AssetsMissing.Add(new ModEntry { Name = Field(f, 1), Version = Field(f, 2), PlatformId = Field(f, 3) }); break;
                    case "asset-extra": r.AssetsExtra.Add(new ModEntry { Name = Field(f, 1), Version = Field(f, 2), PlatformId = Field(f, 3) }); break;
                    case "asset-version": r.AssetVersions.Add(new ModVersionDifference { Name = Field(f, 1), HostVersion = Field(f, 2), ClientVersion = Field(f, 3), PlatformId = Field(f, 4) }); break;
                    case "assets-unknown": r.AssetsUnknown = true; break;
                    case "setting": r.Settings.Add(new SettingDifference { Mod = Field(f, 1), Key = Field(f, 2), HostValue = Field(f, 3), ClientValue = Field(f, 4) }); break;
                    case "overridable": r.Overridable = true; break;
                    case "joined-anyway": r.JoinedAnyway = true; break;
                    case "city-data": r.CityData.Add(CityDataEntry(f)); break;
                    case "host-city-data": r.HostCityData.Add(CityDataEntry(f)); break;
                    case "city-data-checked": r.CityDataChecked = true; break;
                    case "rest-overridable": r.RestOverridable = true; break;
                    case "conflict-host": r.HostConflict = new ModEntry { Name = Field(f, 1), Version = Field(f, 2), PlatformId = Field(f, 3) }; break;
                    case "conflict-you": r.ClientConflict = new ModEntry { Name = Field(f, 1), Version = Field(f, 2), PlatformId = Field(f, 3) }; break;
                    case "text":
                    {
                        r.TextKey = Field(f, 1);
                        int pairs = Math.Min(8, Math.Max(0, (f.Length - 2) / 2));
                        r.TextArgs = new string[pairs * 2];
                        for (int i = 0; i < pairs * 2; i++) r.TextArgs[i] = Field(f, 2 + i) ?? "";
                        break;
                    }
                    case "retry-after":
                        int.TryParse(Field(f, 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int seconds);
                        r.RetryAfterSeconds = Math.Max(0, Math.Min(seconds, 86400));
                        break;
                }
            }
            return r;
        }

        private static ModEntry CityDataEntry(string[] f) => new ModEntry
        {
            Name = Field(f, 1),
            Version = Field(f, 2),
            PlatformId = Field(f, 3),
            StoresCityData = true,
            CityDataInCity = Field(f, 4) == "1",
        };

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
