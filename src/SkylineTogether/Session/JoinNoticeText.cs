using System.Collections.Generic;
using System.Globalization;
using SkylineTogether.Core;
using SkylineTogether.Core.Session;
using SkylineTogether.Settings;

namespace SkylineTogether.Session
{
    /// <summary>
    /// The player-facing texts of a join notice (ARCHITECTURE.md#handshake-and-admission, docs/guide/troubleshooting.md):
    /// what differs between the host and an admitted joiner without blocking the join. One line per category, from the
    /// locale (<c>st.notice.*</c>); lists show the first few names (asset packs with their Paradox Mods id).
    /// </summary>
    internal static class JoinNoticeText
    {
        private const int ShownPerList = 5;

        /// <summary>The joiner's lines (toasts and system chat lines), in order of importance.</summary>
        public static List<string> ForJoiner(MismatchReport r)
        {
            var lines = new List<string>();
            if (r == null) return lines;
            if (r.AssetsMissing.Count > 0) lines.Add(LocaleSource.Resolve("st.notice.assetsMissing", "LIST", Packs(r.AssetsMissing)));
            if (r.AssetVersions.Count > 0)
            {
                var names = new List<string>();
                foreach (ModVersionDifference v in r.AssetVersions) names.Add(MismatchReport.Named(v.Name, v.PlatformId));
                lines.Add(LocaleSource.Resolve("st.notice.assetVersions", "LIST", Join(names)));
            }
            if (r.AssetsUnknown) lines.Add(LocaleSource.Resolve("st.notice.assetsUnknown"));
            if (r.AssetsExtra.Count > 0) lines.Add(LocaleSource.Resolve("st.notice.assetsExtra", "LIST", Packs(r.AssetsExtra)));
            if (r.ContentExtra.Count > 0) lines.Add(LocaleSource.Resolve("st.notice.contentExtra", "LIST", Packs(r.ContentExtra)));
            if (r.HasModDifferences)
            {
                var names = new List<string>();
                foreach (ModEntry m in r.Missing) names.Add(m.Name ?? "?");
                foreach (ModEntry m in r.Extra) names.Add(m.Name ?? "?");
                foreach (ModVersionDifference v in r.Versions) names.Add(v.Name ?? "?");
                lines.Add(LocaleSource.Resolve("st.notice.mods", "LIST", Join(names)));
            }
            if (r.Settings.Count > 0)
            {
                var items = new List<string>();
                foreach (SettingDifference d in r.Settings)
                {
                    items.Add(LocaleSource.Resolve("st.notice.settingItem", "MOD", d.Mod ?? "?", "KEY", d.Key ?? "?", "HOST", d.HostValue ?? "", "YOU", d.ClientValue ?? ""));
                }
                lines.Add(LocaleSource.Resolve("st.notice.settings", "LIST", Join(items)));
            }
            return lines;
        }

        /// <summary>The host's line about a joiner, or null when there is nothing the host needs to know.</summary>
        public static string ForHost(string name, MismatchReport r)
        {
            if (r == null || !r.HasDifferences) return null;
            if (r.AssetsMissing.Count > 0)
            {
                return LocaleSource.Resolve("st.notice.hostAssets", "NAME", name ?? "?", "N", r.AssetsMissing.Count.ToString(CultureInfo.InvariantCulture));
            }
            return LocaleSource.Resolve("st.notice.hostDiffers", "NAME", name ?? "?");
        }

        private static string Packs(List<ModEntry> entries)
        {
            var names = new List<string>(entries.Count);
            foreach (ModEntry e in entries) names.Add(MismatchReport.Named(e.Name, e.PlatformId));
            return Join(names);
        }

        private static string Join(List<string> names)
        {
            if (names.Count <= ShownPerList) return string.Join(", ", names);
            return string.Join(", ", names.GetRange(0, ShownPerList)) + " (+" + (names.Count - ShownPerList).ToString(CultureInfo.InvariantCulture) + ")";
        }
    }
}
