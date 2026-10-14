using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SkylineTogether.Core.Modding
{
    /// <summary>Where a prefab the host uses comes from (base game prefabs have no source record).</summary>
    public enum AssetSourceKind : byte
    {
        Unknown = 0,
        /// <summary>A Paradox Mods package: an asset pack, or the assets shipped with a code mod.</summary>
        Package = 1,
        /// <summary>An asset of the host's own (not published, no Paradox Mods id).</summary>
        LocalAsset = 2,
        /// <summary>Created in code at runtime by a mod (no asset behind it).</summary>
        RuntimePrefab = 3,
    }

    /// <summary>
    /// The origin of a prefab (or of a group of prefabs) for missing-asset reports (docs/design/MOD-COMPATIBILITY.md §7):
    /// the package's display name and Paradox Mods id, or the mod that creates it at runtime. Never a local path.
    /// </summary>
    public sealed class AssetSource : IEquatable<AssetSource>
    {
        /// <summary>UTF-8 bytes of <see cref="Name"/> at most on the wire.</summary>
        public const int MaxNameBytes = 256;

        /// <summary>UTF-8 bytes of <see cref="PlatformId"/> at most on the wire.</summary>
        public const int MaxPlatformIdBytes = 64;

        public AssetSourceKind Kind;
        /// <summary>The package's display name, or the creating mod's name (may be empty when unknown).</summary>
        public string Name;
        /// <summary>The Paradox Mods id (may be empty).</summary>
        public string PlatformId;

        public bool Equals(AssetSource other)
        {
            return other != null && Kind == other.Kind && string.Equals(Name ?? "", other.Name ?? "", StringComparison.Ordinal)
                   && string.Equals(PlatformId ?? "", other.PlatformId ?? "", StringComparison.Ordinal);
        }

        public override bool Equals(object obj) => Equals(obj as AssetSource);

        public override int GetHashCode() => ((int)Kind * 397) ^ (Name ?? "").GetHashCode() ^ (PlatformId ?? "").GetHashCode();

        /// <summary>A short readable origin, e.g. <c>the asset pack "China Pack" (Paradox Mods 100454)</c>.</summary>
        public string Describe()
        {
            string id = string.IsNullOrEmpty(PlatformId) ? "" : " (Paradox Mods " + PlatformId + ")";
            string name = string.IsNullOrEmpty(Name) ? "" : "\"" + Name + "\"";
            switch (Kind)
            {
                case AssetSourceKind.Package:
                    return name.Length > 0 ? "the package " + name + id : "a Paradox Mods package" + id;
                case AssetSourceKind.LocalAsset:
                    return "a local asset of the host" + (name.Length > 0 ? " (" + name + ")" : "");
                case AssetSourceKind.RuntimePrefab:
                    return name.Length > 0 ? "the mod " + name + id + " (created at runtime)" : "a mod of the host (created at runtime)";
                default:
                    return "an asset or mod";
            }
        }

        /// <summary>What the player can do about a missing prefab of this source.</summary>
        public string Advice()
        {
            switch (Kind)
            {
                case AssetSourceKind.Package:
                    return string.IsNullOrEmpty(PlatformId)
                        ? "subscribe to the same asset packs as the host"
                        : "subscribe to Paradox Mods " + PlatformId + ", restart the game and join again";
                case AssetSourceKind.LocalAsset:
                    return "the host has to publish it or stop using it; it cannot be downloaded";
                case AssetSourceKind.RuntimePrefab:
                    return "enable " + (string.IsNullOrEmpty(Name) ? "the same mods" : Name) + " in the host's version and with the same configuration (the prefab's name must come out the same)";
                default:
                    return "subscribe to the same asset packs and mods as the host";
            }
        }

        public override string ToString() => Describe();
    }

    /// <summary>One origin of the prefabs a city uses, with how many prefabs and one example (the join snapshot's list).</summary>
    public sealed class AssetSourceUse
    {
        /// <summary>UTF-8 bytes of <see cref="Example"/> at most on the wire.</summary>
        public const int MaxExampleBytes = 256;

        public AssetSource Source;
        /// <summary>Distinct prefabs of this origin the city uses.</summary>
        public int Prefabs;
        /// <summary>The name of one of them.</summary>
        public string Example;

        public override string ToString() => (Source?.Describe() ?? "?") + ": " + Prefabs.ToString(CultureInfo.InvariantCulture) + " prefab(s), e.g. " + Example;
    }

    /// <summary>
    /// The texts and decisions around prefabs that do not resolve on a client (docs/design/MOD-COMPATIBILITY.md §7). Pure.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class MissingAssets
    {
        /// <summary>
        /// Splits a <c>PrefabID</c> text (<c>"Type:Name"</c>, or <c>"Type:Name (hash)"</c> when the prefab has an asset) into
        /// its parts; false for a text that is not in that form.
        /// </summary>
        public static bool TryParsePrefabId(string text, string name, out string type, out bool hasHash)
        {
            type = null;
            hasHash = false;
            if (string.IsNullOrEmpty(text)) return false;
            int colon = text.IndexOf(':');
            if (colon <= 0) return false;
            type = text.Substring(0, colon);
            string rest = text.Substring(colon + 1);
            if (name == null)
            {
                // without the name, a trailing " (...)" is taken for the hash
                hasHash = rest.EndsWith(")", StringComparison.Ordinal) && rest.LastIndexOf(" (", StringComparison.Ordinal) > 0;
                return true;
            }
            if (string.Equals(rest, name, StringComparison.Ordinal)) return true;
            hasHash = rest.StartsWith(name + " (", StringComparison.Ordinal) && rest.EndsWith(")", StringComparison.Ordinal);
            return hasHash;
        }

        /// <summary>One candidate of the name fallback: a local prefab with the host prefab's type and name.</summary>
        public struct Candidate
        {
            /// <summary>The local prefab has an asset (it came from a file or package, not from code).</summary>
            public bool HasAsset;
            /// <summary>The local prefab's id has a hash (its asset has a platform id or guid).</summary>
            public bool HasHash;
        }

        /// <summary>
        /// The name fallback of prefab binding: when the host's exact <c>PrefabID</c> does not resolve, a local prefab with
        /// the same type and name is the same prefab if it was created in code (a runtime prefab: the hash is then empty
        /// or only names a local file the creating mod reused) or the host's id carries no hash. Two assets of different
        /// packages that share a name never match. Returns the index of the one acceptable candidate, -1 for none or
        /// more than one (ambiguous: reported as missing rather than guessed).
        /// </summary>
        public static int ChooseNameFallback(bool hostHasHash, IReadOnlyList<Candidate> candidates)
        {
            if (candidates == null) return -1;
            int found = -1;
            for (int i = 0; i < candidates.Count; i++)
            {
                Candidate c = candidates[i];
                bool same = !hostHasHash || !c.HasAsset || !c.HasHash;
                if (!same) continue;
                if (found >= 0) return -1;
                found = i;
            }
            return found;
        }

        /// <summary>
        /// The one-line explanation of a missing prefab: what it is, where it comes from and what to do, e.g.
        /// <c>'Big Tree' (StaticObjectPrefab) from the package "Trees" (Paradox Mods 123): subscribe to Paradox Mods 123,
        /// restart the game and join again</c>.
        /// </summary>
        public static string Describe(string prefabId, string prefabName, AssetSource source)
        {
            string type = null;
            if (TryParsePrefabId(prefabId, prefabName, out string parsedType, out _)) type = parsedType;
            string name = !string.IsNullOrEmpty(prefabName) ? prefabName : prefabId ?? "?";
            string what = "'" + name + "'" + (type != null ? " (" + type + ")" : "");
            if (source == null || source.Kind == AssetSourceKind.Unknown) return what + ": an asset or mod you do not have; subscribe to the same asset packs and mods as the host";
            return what + " from " + source.Describe() + ": " + source.Advice();
        }

        /// <summary>
        /// The toast after a join (or the first missing record): the first sources by number of missing prefabs, each
        /// with its advice, and how many objects are left out.
        /// </summary>
        public static string Summary(IReadOnlyList<KeyValuePair<AssetSource, int>> missingBySource, int maxSources = 3)
        {
            if (missingBySource == null || missingBySource.Count == 0) return null;
            var sorted = new List<KeyValuePair<AssetSource, int>>(missingBySource);
            sorted.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.Key?.Describe(), b.Key?.Describe()));
            var sb = new StringBuilder("The host's city uses assets you do not have; objects using them are left out. ");
            int shown = 0;
            foreach (KeyValuePair<AssetSource, int> pair in sorted)
            {
                if (shown == maxSources) break;
                AssetSource s = pair.Key ?? new AssetSource();
                sb.Append(Capitalize(s.Describe())).Append(" (").Append(pair.Value.ToString(CultureInfo.InvariantCulture)).Append(pair.Value == 1 ? " prefab" : " prefabs")
                  .Append("): ").Append(s.Advice()).Append(". ");
                shown++;
            }
            if (sorted.Count > shown) sb.Append("And ").Append((sorted.Count - shown).ToString(CultureInfo.InvariantCulture)).Append(" more (see the diagnostics).");
            return sb.ToString().Trim();
        }

        private static string Capitalize(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }
}
