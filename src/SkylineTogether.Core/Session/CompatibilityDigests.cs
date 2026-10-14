using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SkylineTogether.Core.Hashing;

namespace SkylineTogether.Core.Session
{
    /// <summary>
    /// The handshake digests of the compared categories (ARCHITECTURE.md#handshake-and-admission): XXH64 of a canonical
    /// text of each list, sorted by key with ordinal comparison, so the same set gives the same digest on every machine
    /// whatever order the game enumerated it in. An empty or null list has digest 0. Pure; thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class CompatibilityDigests
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

        /// <summary>The digest of the code mods: name, version, Paradox Mods id and DLL hash of each.</summary>
        public static ulong OfMods(IReadOnlyList<ModEntry> mods)
        {
            if (mods == null || mods.Count == 0) return 0;
            var lines = new List<string>(mods.Count);
            foreach (ModEntry m in mods)
            {
                if (m != null) lines.Add((m.Name ?? "") + "\u0001" + (m.Version ?? "") + "\u0001" + (m.PlatformId ?? "") + "\u0001" + (m.DllHash ?? ""));
            }
            return OfLines(lines);
        }

        /// <summary>The digest of DLC/content or asset pack entries: key, version, prefab digest and count of each.</summary>
        public static ulong OfContent(IReadOnlyList<ContentEntry> entries)
        {
            if (entries == null || entries.Count == 0) return 0;
            var lines = new List<string>(entries.Count);
            foreach (ContentEntry e in entries)
            {
                if (e == null) continue;
                lines.Add(e.Key + "\u0001" + (e.Version ?? "") + "\u0001" + e.Digest.ToString("x16", CultureInfo.InvariantCulture) + "\u0001"
                          + e.Prefabs.ToString(CultureInfo.InvariantCulture));
            }
            return OfLines(lines);
        }

        /// <summary>The digest of mod settings: mod, key and value of each.</summary>
        public static ulong OfSettings(IReadOnlyList<SettingEntry> settings)
        {
            if (settings == null || settings.Count == 0) return 0;
            var lines = new List<string>(settings.Count);
            foreach (SettingEntry s in settings)
            {
                if (s != null) lines.Add((s.Mod ?? "") + "\u0001" + (s.Key ?? "") + "\u0001" + (s.Value ?? ""));
            }
            return OfLines(lines);
        }

        /// <summary>The digest of an asset pack's prefab ids (the <see cref="ContentEntry.Digest"/> of a pack).</summary>
        public static ulong OfPrefabIds(IEnumerable<string> prefabIds)
        {
            if (prefabIds == null) return 0;
            var lines = new List<string>();
            foreach (string id in prefabIds) lines.Add(id ?? "");
            return OfLines(lines);
        }

        /// <summary>Fills the four category digests of <paramref name="manifest"/> from its lists.</summary>
        public static void Fill(HandshakeManifest manifest)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            manifest.Digests.Mods = OfMods(manifest.Mods);
            manifest.Digests.Content = OfContent(manifest.Content);
            manifest.Digests.AssetPacks = OfContent(manifest.AssetPacks);
            manifest.Digests.ModSettings = OfSettings(manifest.Settings);
        }

        private static ulong OfLines(List<string> lines)
        {
            if (lines.Count == 0) return 0;
            lines.Sort(StringComparer.Ordinal);
            var sb = new StringBuilder(lines.Count * 32);
            foreach (string line in lines) sb.Append(line).Append('\n');
            byte[] bytes = Utf8.GetBytes(sb.ToString());
            ulong h = XxHash64.Hash(bytes, 0, bytes.Length);
            return h == 0 ? 1 : h; // 0 means "empty"
        }
    }
}
