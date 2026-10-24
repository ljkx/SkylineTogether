using System;
using System.Collections.Generic;
using System.Text;

namespace SkylineTogether.Core.Session
{
    /// <summary>
    /// The result of comparing the host's and a joiner's code mods (<see cref="ModCompatibility.Compare"/>): what blocks
    /// the join, and which differences were ignored because the mod only affects its own player (UI, tooltips, icons,
    /// overlays, statistics panels).
    /// </summary>
    public sealed class ModComparison
    {
        /// <summary>World-affecting mods the host has and the joiner lacks ("enable these").</summary>
        public readonly List<string> Missing = new List<string>();
        /// <summary>World-affecting mods the joiner has and the host lacks ("disable these").</summary>
        public readonly List<string> Extra = new List<string>();
        /// <summary>World-affecting mods both have in different versions.</summary>
        public readonly List<string> Version = new List<string>();
        /// <summary>Client-local mods present on one side only or in different versions (informational).</summary>
        public readonly List<string> IgnoredLocal = new List<string>();

        /// <summary>True when the join must be refused.</summary>
        public bool Blocks => Missing.Count > 0 || Extra.Count > 0 || Version.Count > 0;

        /// <summary>The readable refusal text (null when nothing blocks).</summary>
        public string RefusalText()
        {
            if (!Blocks) return null;
            var sb = new StringBuilder();
            if (Missing.Count > 0) sb.Append("Missing (enable these): ").Append(string.Join(", ", Missing)).Append(". ");
            if (Extra.Count > 0) sb.Append("Not on host (disable these): ").Append(string.Join(", ", Extra)).Append(". ");
            if (Version.Count > 0) sb.Append("Different version: ").Append(string.Join(", ", Version)).Append('.');
            return sb.ToString().Trim();
        }
    }

    /// <summary>
    /// Which code mods must match between host and joiners (docs/design/MOD-COMPATIBILITY.md). A mod that changes the
    /// shared world (components, systems, prefabs, tools, settings that the simulation reads) must be on every peer in
    /// the same version; a mod that only presents information to its own player may differ freely. The classification
    /// is a built-in list of known mods, then the hint each peer computes for its own mods
    /// (<see cref="ModEntry.ClientLocalHint"/>: the mod registers no serializable component, saved system, prefab or tool
    /// and patches nothing), and otherwise <see cref="ModClass.WorldAffecting"/> (fail closed). A client-local entry that
    /// was checked against one version holds only while each peer's copy matches it (<see cref="BeyondVerifiedLocal"/>).
    /// Pure and thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class ModCompatibility
    {
        // assembly names (case-insensitive) and Paradox Mods ids of mods that only affect their own player
        private static readonly HashSet<string> LocalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "I18NEverywhere", "ExtendedTooltip", "AssetIconLibrary", "UnifiedIconLibrary", "ToggleableOverlays",
            "EmploymentTracker", "CimCensus", "Skyve Mod", "Skyve.Mod.CS2", "Skyve", "FindIt",
            "ExtraLib", "AchievementFixer",
        };

        private static readonly HashSet<string> LocalIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "75426", "78188", "79634", "74417", "80931", "76662", "85825", "75804", "77240",
            "75724", "121256",
        };

        // known world-affecting mods: never relaxed, whatever a peer's own scan says
        private static readonly HashSet<string> WorldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Traffic", "MoveIt", "Anarchy", "BetterBulldozer", "FiveTwentyNineTiles", "HistoricalStart", "Water_Features",
            "ZoningToolkit", "LineTool", "PlopTheGrowables", "Tree_Controller",
            "Placemaker",
        };

        private static readonly HashSet<string> WorldIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "80095", "74324", "74604", "75250", "74328", "74535", "75613", "75750", "75816", "75826", "75993",
            "163638",
        };

        /// <summary>True for a mod the built-in list knows as client-local.</summary>
        public static bool IsKnownClientLocal(string assemblyName, string platformId)
        {
            return (!string.IsNullOrEmpty(assemblyName) && LocalNames.Contains(assemblyName))
                   || (!string.IsNullOrEmpty(platformId) && LocalIds.Contains(platformId));
        }

        /// <summary>True for a mod the built-in list knows as world-affecting.</summary>
        public static bool IsKnownWorldAffecting(string assemblyName, string platformId)
        {
            return (!string.IsNullOrEmpty(assemblyName) && WorldNames.Contains(assemblyName))
                   || (!string.IsNullOrEmpty(platformId) && WorldIds.Contains(platformId));
        }

        // client-local mods whose classification was checked by hand against one version (MOD-COMPATIBILITY.md §2): the
        // parts of its scan that look world-affecting (Modding.ModClassifier.WorldItems) and were found to change nothing
        // shared. A copy with any other such part is not that version any more and is classified by its scan.
        private static readonly Dictionary<string, string[]> Verified = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // ExtraLib 1.4.11: MainSystem is disabled from its creation and never updates; the prefabs are toolbar menus
            // and categories created in the main menu, which no object of a city references
            ["ExtraLib"] = new[]
            {
                "system ExtraLib.Systems.MainSystem in LateUpdate",
                "prefab type ExtraLib.Prefabs.UIAssetChildCategoryPrefab", "prefab type ExtraLib.Prefabs.UIAssetParentCategoryPrefab",
                "runtime prefabs in ExtraLib.Systems.MainSystem", "runtime prefabs in ExtraLib.Helpers.PrefabsHelper",
            },
            // Achievement Fixer 1.3.1: switches the player's own achievements flag on after a load; writes no entity
            ["AchievementFixer"] = new[] { "system AchievementFixer.AchievementFixerSystem in MainLoop" },
            // Cim Census 1.8: counts citizens, workers and vehicles for its panel (read-only jobs)
            ["CimCensus"] = new[] { "system CimCensus.StatisticsCalculationSystem in GameSimulation" },
            // Cim Route Highlighter 1.26: highlights the selected building's employees (Highlighted, BatchesUpdated);
            // DeleteTimer is declared but never put on an entity
            ["EmploymentTracker"] = new[] { "saved component EmploymentTracker.DeleteTimer", "system EmploymentTracker.HighlightEmployeesSystem in MainLoop" },
        };

        private static readonly Dictionary<string, string> VerifiedIds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["75724"] = "ExtraLib", ["121256"] = "AchievementFixer", ["85825"] = "CimCensus", ["76662"] = "EmploymentTracker",
        };

        /// <summary>
        /// True for a client-local mod of the built-in list whose classification is tied to the version checked by hand:
        /// its entries say whether the sending peer's copy is still that version (<see cref="ModEntry.RegistersTypesOrSystemsOrPrefabs"/>
        /// set only when <see cref="BeyondVerifiedLocal"/> found more).
        /// </summary>
        public static bool HasVerifiedProfile(string assemblyName, string platformId)
        {
            return (!string.IsNullOrEmpty(assemblyName) && Verified.ContainsKey(assemblyName))
                   || (!string.IsNullOrEmpty(platformId) && VerifiedIds.ContainsKey(platformId));
        }

        /// <summary>The parts of the checked version's scan that were found to change nothing shared (empty when unchecked).</summary>
        public static IReadOnlyList<string> VerifiedLocalItems(string assemblyName)
        {
            return !string.IsNullOrEmpty(assemblyName) && Verified.TryGetValue(assemblyName, out string[] items) ? items : Array.Empty<string>();
        }

        /// <summary>
        /// The world-affecting parts of a scan (<see cref="Modding.ModClassifier.WorldItems"/>) that the hand check of the
        /// mod's built-in client-local classification did not cover; empty when the copy matches the checked version, null
        /// when the mod has no such check. A runtime-prefab entry covers every method of its type and the types nested in it.
        /// </summary>
        public static List<string> BeyondVerifiedLocal(Modding.ModFacts facts)
        {
            if (facts?.AssemblyName == null || !Verified.TryGetValue(facts.AssemblyName, out string[] verified)) return null;
            var beyond = new List<string>();
            foreach (string item in Modding.ModClassifier.WorldItems(facts))
            {
                if (!Covers(verified, item)) beyond.Add(item);
            }
            return beyond;
        }

        private static bool Covers(string[] verified, string item)
        {
            foreach (string v in verified)
            {
                if (string.Equals(v, item, StringComparison.Ordinal)) return true;
                if (v.StartsWith("runtime prefabs in ", StringComparison.Ordinal) && item.Length > v.Length
                    && item.StartsWith(v, StringComparison.Ordinal) && (item[v.Length] == '.' || item[v.Length] == '+')) return true;
            }
            return false;
        }

        /// <summary>The class of one mod entry: built-in list first, then the peer's hint, else world-affecting.</summary>
        public static ModClass Classify(ModEntry m)
        {
            if (m == null) return ModClass.WorldAffecting;
            if (IsKnownWorldAffecting(m.Name, m.PlatformId)) return ModClass.WorldAffecting;
            // a checked client-local mod whose peer found more in its copy than in the checked version: the scan decides
            if (IsKnownClientLocal(m.Name, m.PlatformId) && HasVerifiedProfile(m.Name, m.PlatformId) && m.RegistersTypesOrSystemsOrPrefabs) return ModClass.WorldAffecting;
            if (IsKnownClientLocal(m.Name, m.PlatformId)) return ModClass.LocalOnly;
            if (m.RegistersTypesOrSystemsOrPrefabs) return ModClass.WorldAffecting;
            return m.ClientLocalHint || m.IsUiModule ? ModClass.LocalOnly : ModClass.WorldAffecting;
        }

        /// <summary>
        /// Compares the host's and a joiner's mods. A mod is local when either side classifies its entry as local and
        /// neither side flags it as world-affecting (a peer's scan of its own copy is the best evidence available).
        /// </summary>
        public static ModComparison Compare(IReadOnlyList<ModEntry> host, IReadOnlyList<ModEntry> client)
        {
            var result = new ModComparison();
            Dictionary<string, ModEntry> hostByKey = Index(host);
            Dictionary<string, ModEntry> clientByKey = Index(client);
            foreach (KeyValuePair<string, ModEntry> h in hostByKey)
            {
                clientByKey.TryGetValue(h.Key, out ModEntry c);
                bool local = IsLocalPair(h.Value, c);
                if (c == null)
                {
                    if (local) result.IgnoredLocal.Add(h.Value + " (host only)");
                    else result.Missing.Add(h.Value.ToString());
                    continue;
                }
                bool differs = !string.Equals(h.Value.Version ?? "", c.Version ?? "", StringComparison.Ordinal)
                               || (!string.IsNullOrEmpty(h.Value.DllHash) && !string.IsNullOrEmpty(c.DllHash)
                                   && !string.Equals(h.Value.DllHash, c.DllHash, StringComparison.OrdinalIgnoreCase));
                if (!differs) continue;
                string text = (h.Value.Name ?? "?") + " (host " + (h.Value.Version ?? "?") + ", you " + (c.Version ?? "?") + ")";
                if (local) result.IgnoredLocal.Add(text);
                else result.Version.Add(text);
            }
            foreach (KeyValuePair<string, ModEntry> c in clientByKey)
            {
                if (hostByKey.ContainsKey(c.Key)) continue;
                if (IsLocalPair(null, c.Value)) result.IgnoredLocal.Add(c.Value + " (yours only)");
                else result.Extra.Add(c.Value.ToString());
            }
            result.Missing.Sort(StringComparer.OrdinalIgnoreCase);
            result.Extra.Sort(StringComparer.OrdinalIgnoreCase);
            result.Version.Sort(StringComparer.OrdinalIgnoreCase);
            result.IgnoredLocal.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        /// <summary>True when a mod (host entry, joiner entry; either may be null) never blocks a join.</summary>
        public static bool IsLocalPair(ModEntry a, ModEntry b)
        {
            if (DefinitelyWorld(a) || DefinitelyWorld(b)) return false;
            return (a != null && Classify(a) == ModClass.LocalOnly) || (b != null && Classify(b) == ModClass.LocalOnly);
        }

        /// <summary>Known as world-affecting, or flagged by its peer's scan (unless the built-in list knows it as local).</summary>
        private static bool DefinitelyWorld(ModEntry m)
        {
            if (m == null) return false;
            if (IsKnownWorldAffecting(m.Name, m.PlatformId)) return true;
            return m.RegistersTypesOrSystemsOrPrefabs && (!IsKnownClientLocal(m.Name, m.PlatformId) || HasVerifiedProfile(m.Name, m.PlatformId));
        }

        private static Dictionary<string, ModEntry> Index(IReadOnlyList<ModEntry> mods)
        {
            var map = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
            if (mods == null) return map;
            foreach (ModEntry m in mods)
            {
                if (m != null) map[Key(m)] = m;
            }
            return map;
        }

        /// <summary>Identity of a mod entry across peers: its Paradox Mods id when known, else its name.</summary>
        public static string Key(ModEntry m) => !string.IsNullOrEmpty(m.PlatformId) ? "id:" + m.PlatformId : "name:" + (m.Name ?? "");
    }
}
