using System;
using System.Collections.Generic;
using System.Globalization;

namespace SkylineTogether.Core.Session
{
    /// <summary>What the host requires of a joiner (from <see cref="HostOptions"/>).</summary>
    public sealed class CompatibilityOptions
    {
        /// <summary>Refuse joiners whose code mods differ (otherwise they are only told).</summary>
        public bool RequireSameMods = true;

        /// <summary>Refuse joiners whose asset packs differ (otherwise they are only told, and objects of packs they lack are left out).</summary>
        public bool RequireSameAssets;

        /// <summary>
        /// The host lets players join with different mods: a refusal for code mods or asset packs is marked
        /// <see cref="MismatchReport.Overridable"/>, and <see cref="JoinAnyway"/> turns it into a notice.
        /// </summary>
        public bool AllowOverride;

        /// <summary>
        /// The joiner chose "join anyway" (<see cref="Protocol.HelloJoinFlags.AcceptModMismatch"/>). Counts only together with
        /// <see cref="AllowOverride"/>, and only for code mods and asset packs; DLC the joiner lacks, mods with city data it
        /// lacks and the old multiplayer mod still refuse.
        /// </summary>
        public bool JoinAnyway;
    }

    /// <summary>The outcome of <see cref="CompatibilityCheck.Compare"/>.</summary>
    public sealed class CompatibilityResult
    {
        /// <summary>The refusal's report (kind "mods", "content" or "assets"), null when the joiner may join.</summary>
        public MismatchReport Refusal;

        /// <summary>The refusal's readable detail text (English; the joiner's UI shows its own text by report kind).</summary>
        public string RefusalDetail;

        /// <summary>The differences that do not block the join (kind "notice"), null when there are none.</summary>
        public MismatchReport Notice;

        /// <summary>
        /// The joiner asked to join anyway, but the host does not allow it and its mods or asset packs differ (the refusal is
        /// not overridable; the host logs that the request was ignored).
        /// </summary>
        public bool JoinAnywayIgnored;

        /// <summary>
        /// The refusal's text for the joiner: the locale key of a short sentence by report kind (<see cref="RefusalKey"/>,
        /// shown in the joiner's language next to the report's lists), with <see cref="RefusalDetail"/> as the English text
        /// for the logs and for a receiver without the key. Null without a refusal.
        /// </summary>
        public LocalText RefusalText()
        {
            if (Refusal == null) return null;
            return new LocalText { Key = RefusalKey(Refusal), Text = RefusalDetail ?? "" };
        }

        /// <summary>The <see cref="NetText"/> key of a refusal's short sentence: by report kind, and whether it can list what differs.</summary>
        public static string RefusalKey(MismatchReport r)
        {
            if (r == null) return null;
            switch (r.Kind)
            {
                case "conflict":
                    return r.ClientConflict != null ? NetText.RefuseConflictYou : NetText.RefuseConflictHost;
                case "content":
                    return NetText.RefuseContent;
                case "assets":
                    return r.AssetsMissing.Count + r.AssetsExtra.Count + r.AssetVersions.Count == 0 ? NetText.RefuseAssetsUnlisted : NetText.RefuseAssets;
                default:
                    if (r.CityData.Count > 0) return NetText.RefuseCityData;
                    return r.Missing.Count + r.Extra.Count + r.Versions.Count == 0 ? NetText.RefuseModsUnlisted : NetText.RefuseMods;
            }
        }
    }

    /// <summary>
    /// The host's compatibility decision after the password (ARCHITECTURE.md#handshake-and-admission), per category of
    /// the handshake manifest. Two rules come before every option (<see cref="CityDataRule"/>): the old multiplayer mod on
    /// either side refuses (kind "conflict"), and a joiner that lacks one of the host's mods that store data in the city
    /// is refused with the list (<see cref="MismatchReport.CityData"/>), never overridable, whatever "require the same
    /// mods", "let players join with different mods" and "join anyway" say. A category is compared item by item only when
    /// its digests differ (or one is missing):
    /// <list type="bullet">
    /// <item>code mods: refused with "require the same mods", otherwise told (client-local mods never count,
    /// <see cref="MismatchReport.CompareMods"/>);</item>
    /// <item>DLC and content packs: refused when the joiner lacks one the host has, because the host's city may use its
    /// objects and the joiner's game cannot load them; content only the joiner has is told (the host refuses
    /// proposals of objects it lacks);</item>
    /// <item>asset packs (by Paradox Mods id, and each pack's prefab digest for other versions): refused with
    /// "require the same assets", otherwise told; objects of packs the joiner lacks are left out on its side;</item>
    /// <item>simulation-relevant mod settings: told only; the host's values apply for the session
    /// (<c>Compat.ModSettingsSync</c>).</item>
    /// </list>
    /// DLC the joiner lacks is checked first: it is a hard incompatibility, so a joiner is never offered "join anyway" for its
    /// mods only to be refused for its DLC afterwards. A refusal for code mods or asset packs is overridable when the host
    /// lets players join with different mods (<see cref="CompatibilityOptions.AllowOverride"/>); a joiner that chose
    /// "join anyway" (<see cref="CompatibilityOptions.JoinAnyway"/>) is then told those differences in the notice
    /// (<see cref="MismatchReport.JoinedAnyway"/>) instead of being refused. Nothing else depends on the joiner's choice.
    /// Lists in the reports are sorted and capped at <see cref="MaxListed"/> entries each. Pure; thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class CompatibilityCheck
    {
        /// <summary>Entries per list at most (the UI shows 200).</summary>
        public const int MaxListed = 200;

        public static CompatibilityResult Compare(HandshakeManifest host, HandshakeManifest client, CompatibilityOptions options)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            if (client == null) throw new ArgumentNullException(nameof(client));
            options = options ?? new CompatibilityOptions();
            var result = new CompatibilityResult();
            var notice = new MismatchReport { Kind = "notice" };
            // the joiner's choice counts only when the host allows it
            bool joinAnyway = options.AllowOverride && options.JoinAnyway;
            MismatchReport refusal = null;
            string refusalDetail = null;

            // the old multiplayer mod on either side: never overridable (two multiplayer mods at once conflict)
            MismatchReport conflict = CityDataRule.Conflict(CityDataRule.FindOldMultiplayerMod(host.Mods), CityDataRule.FindOldMultiplayerMod(client.Mods));
            if (conflict != null)
            {
                result.Refusal = conflict;
                result.RefusalDetail = conflict.Describe();
                return result;
            }

            // DLC and content packs first: lacking the host's is never overridable
            if (Differs(host.Digests.Content, client.Digests.Content) && host.Content != null && client.Content != null)
            {
                Diff(host.Content, client.Content, false, out List<ModEntry> missing, out List<ModEntry> extra, out _);
                if (missing.Count > 0)
                {
                    var content = new MismatchReport { Kind = "content" };
                    content.ContentMissing.AddRange(Cap(missing));
                    content.ContentExtra.AddRange(Cap(extra));
                    result.Refusal = content;
                    result.RefusalDetail = "The host has DLC or content packs you don't have: " + Names(missing)
                                           + ". The host's city may use them, and your game cannot load it the same way. Get them, or ask the host to host without them.";
                    return result;
                }
                notice.ContentExtra.AddRange(Cap(extra));
            }

            // code mods
            MismatchReport mods = null;
            if (host.Mods == null || client.Mods == null)
            {
                // a list too long to send: only the digests can say "different" (no names to show)
                if (host.Digests.Mods != client.Digests.Mods) mods = new MismatchReport { Kind = "mods" };
            }
            else if (Differs(host.Digests.Mods, client.Digests.Mods))
            {
                mods = MismatchReport.CompareMods(host.Mods, client.Mods);
            }
            // mods that store data in the city: a joiner without one of them cannot load the city (the game's loader
            // crashes), so it is refused whatever the options and the joiner's choice say
            bool modsDiffer = (host.Mods == null || client.Mods == null) ? host.Digests.Mods != client.Digests.Mods : Differs(host.Digests.Mods, client.Digests.Mods);
            List<ModEntry> hostCityData = CityDataRule.CityDataMods(host.Mods);
            if (modsDiffer && hostCityData.Count > 0)
            {
                // a joiner whose list was too long to send cannot show that it has them: all of them are required
                List<ModEntry> lacking = client.Mods == null ? hostCityData : CityDataRule.MissingFrom(hostCityData, client.Mods);
                if (lacking.Count > 0)
                {
                    MismatchReport required = CityDataRule.Refusal(new List<ModEntry>(Cap(lacking)), mods);
                    bool others = required.OtherMissing().Count > 0 || required.Extra.Count > 0 || required.Versions.Count > 0;
                    required.RestOverridable = others && options.AllowOverride && options.RequireSameMods;
                    result.Refusal = required;
                    result.RefusalDetail = (client.Mods == null ? "Your mod list was too long to compare. " : "")
                                           + required.Describe()
                                           + (required.RestOverridable ? " Once these are enabled, the host lets you join anyway with the other differences." : "");
                    return result;
                }
            }
            if (modsDiffer)
            {
                // the joiner checks these again before it loads the city (CityDataRule.CheckBeforeLoad)
                notice.CityDataChecked = true;
                notice.HostCityData.AddRange(Cap(hostCityData));
            }
            if (mods != null)
            {
                if (options.RequireSameMods && !joinAnyway)
                {
                    refusal = mods;
                    refusalDetail = ("Your mods differ from the host's. " + mods.Describe()).Trim();
                }
                else
                {
                    if (options.RequireSameMods) notice.JoinedAnyway = true;
                    notice.Missing.AddRange(Cap(mods.Missing));
                    notice.Extra.AddRange(Cap(mods.Extra));
                    notice.Versions.AddRange(Cap(mods.Versions));
                }
            }

            // asset packs
            if (Differs(host.Digests.AssetPacks, client.Digests.AssetPacks))
            {
                var assets = new MismatchReport { Kind = "assets" };
                if (host.AssetPacks == null || client.AssetPacks == null)
                {
                    // one side's list was too long to send: the digests say "different", the lists cannot say what
                    assets.AssetsUnknown = host.Digests.AssetPacks != client.Digests.AssetPacks;
                }
                else
                {
                    Diff(host.AssetPacks, client.AssetPacks, true, out List<ModEntry> missing, out List<ModEntry> extra, out List<ModVersionDifference> versions);
                    assets.AssetsMissing.AddRange(Cap(missing));
                    assets.AssetsExtra.AddRange(Cap(extra));
                    assets.AssetVersions.AddRange(Cap(versions));
                }
                if (assets.HasAssetDifferences)
                {
                    if (options.RequireSameAssets && !joinAnyway)
                    {
                        if (refusal == null)
                        {
                            refusal = assets;
                            refusalDetail = "The host requires the same asset packs. " + assets.Describe();
                        }
                    }
                    else
                    {
                        if (options.RequireSameAssets) notice.JoinedAnyway = true;
                        notice.AssetsMissing.AddRange(assets.AssetsMissing);
                        notice.AssetsExtra.AddRange(assets.AssetsExtra);
                        notice.AssetVersions.AddRange(assets.AssetVersions);
                        notice.AssetsUnknown = assets.AssetsUnknown;
                    }
                }
            }

            if (refusal != null)
            {
                // code mods before asset packs; one "join anyway" passes both
                refusal.Overridable = options.AllowOverride;
                result.Refusal = refusal;
                result.RefusalDetail = options.AllowOverride ? refusalDetail + " The host lets you join anyway." : refusalDetail;
                result.JoinAnywayIgnored = options.JoinAnyway && !options.AllowOverride;
                return result;
            }

            // simulation-relevant mod settings: the host's values win
            if (Differs(host.Digests.ModSettings, client.Digests.ModSettings) && host.Settings != null && client.Settings != null)
            {
                notice.Settings.AddRange(Cap(SettingDifferences(host.Settings, client.Settings)));
            }

            result.Notice = notice.HasDifferences ? notice : null;
            return result;
        }

        /// <summary>True when a category must be compared item by item: the digests differ, or one side did not fill its digest.</summary>
        public static bool Differs(ulong host, ulong client) => host == 0 || client == 0 || host != client;

        /// <summary>
        /// The settings both sides have with different values (by mod and key; values compared exactly). A setting only one
        /// side lists belongs to a mod only that side runs, which the mod comparison reports.
        /// </summary>
        public static List<SettingDifference> SettingDifferences(IReadOnlyList<SettingEntry> host, IReadOnlyList<SettingEntry> client)
        {
            var mine = new Dictionary<string, SettingEntry>(StringComparer.Ordinal);
            foreach (SettingEntry s in client)
            {
                if (s != null) mine[(s.Mod ?? "") + "\n" + (s.Key ?? "")] = s;
            }
            var list = new List<SettingDifference>();
            foreach (SettingEntry h in host)
            {
                if (h == null || !mine.TryGetValue((h.Mod ?? "") + "\n" + (h.Key ?? ""), out SettingEntry c)) continue;
                if (string.Equals(h.Value ?? "", c.Value ?? "", StringComparison.Ordinal)) continue;
                list.Add(new SettingDifference { Mod = h.Mod, Key = h.Key, HostValue = h.Value, ClientValue = c.Value });
            }
            list.Sort((a, b) =>
            {
                int byMod = string.CompareOrdinal(a.Mod ?? "", b.Mod ?? "");
                return byMod != 0 ? byMod : string.CompareOrdinal(a.Key ?? "", b.Key ?? "");
            });
            return list;
        }

        /// <summary>
        /// Entries only the host has (<paramref name="missing"/>), only the joiner has (<paramref name="extra"/>), and both
        /// have with different contents (<paramref name="versions"/>: version text or prefab digest), each sorted by name.
        /// Keys compare case-insensitively. <paramref name="paradoxIds"/>: the ids are Paradox Mods ids (asset packs) and go
        /// into <see cref="ModEntry.PlatformId"/>; DLC ids are only keys.
        /// </summary>
        public static void Diff(IReadOnlyList<ContentEntry> host, IReadOnlyList<ContentEntry> client, bool paradoxIds, out List<ModEntry> missing, out List<ModEntry> extra, out List<ModVersionDifference> versions)
        {
            var hostByKey = Index(host);
            var clientByKey = Index(client);
            missing = new List<ModEntry>();
            extra = new List<ModEntry>();
            versions = new List<ModVersionDifference>();
            foreach (KeyValuePair<string, ContentEntry> h in hostByKey)
            {
                if (!clientByKey.TryGetValue(h.Key, out ContentEntry c))
                {
                    missing.Add(AsEntry(h.Value, paradoxIds));
                    continue;
                }
                string hv = h.Value.Version ?? "", cv = c.Version ?? "";
                bool versionDiffers = hv.Length > 0 && cv.Length > 0 && !string.Equals(hv, cv, StringComparison.Ordinal);
                if (!versionDiffers && h.Value.Digest == c.Digest) continue;
                bool showCounts = !versionDiffers;
                versions.Add(new ModVersionDifference
                {
                    Name = h.Value.Name,
                    PlatformId = paradoxIds ? h.Value.Id : null,
                    HostVersion = VersionText(h.Value, showCounts),
                    ClientVersion = VersionText(c, showCounts),
                });
            }
            foreach (KeyValuePair<string, ContentEntry> c in clientByKey)
            {
                if (!hostByKey.ContainsKey(c.Key)) extra.Add(AsEntry(c.Value, paradoxIds));
            }
            missing.Sort(ByName);
            extra.Sort(ByName);
            versions.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name ?? "", b.Name ?? ""));
        }

        private static Dictionary<string, ContentEntry> Index(IReadOnlyList<ContentEntry> entries)
        {
            var map = new Dictionary<string, ContentEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (ContentEntry e in entries)
            {
                if (e != null) map[e.Key] = e;
            }
            return map;
        }

        private static ModEntry AsEntry(ContentEntry e, bool paradoxIds) => new ModEntry { Name = string.IsNullOrEmpty(e.Name) ? e.Id : e.Name, Version = e.Version, PlatformId = paradoxIds ? e.Id : null };

        // "1.2", or with the prefab count when the versions read the same: "1.2 (120)", "(120)"
        private static string VersionText(ContentEntry e, bool withCount)
        {
            string v = e.Version ?? "";
            if (!withCount) return v;
            string count = "(" + e.Prefabs.ToString(CultureInfo.InvariantCulture) + ")";
            return v.Length > 0 ? v + " " + count : count;
        }

        private static int ByName(ModEntry a, ModEntry b)
        {
            int byName = StringComparer.OrdinalIgnoreCase.Compare(a.Name ?? "", b.Name ?? "");
            return byName != 0 ? byName : string.CompareOrdinal(a.PlatformId ?? "", b.PlatformId ?? "");
        }

        private static string Names(List<ModEntry> entries)
        {
            var names = new List<string>();
            for (int i = 0; i < entries.Count && i < 10; i++) names.Add(MismatchReport.Named(entries[i].Name, entries[i].PlatformId));
            string text = string.Join(", ", names);
            return entries.Count > 10 ? text + " and " + (entries.Count - 10).ToString(CultureInfo.InvariantCulture) + " more" : text;
        }

        private static IEnumerable<T> Cap<T>(List<T> list) => list.Count <= MaxListed ? list : list.GetRange(0, MaxListed);
    }
}
