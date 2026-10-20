using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Contracts
{
    /// <summary>
    /// The CONTRIBUTING rule for game internals, checked on the game layer's sources (they cannot be compiled here, but
    /// they can be read): members and types are looked up by name only in <c>Diagnostics/Probes.cs</c> (which reports
    /// each one and switches its feature off when it is missing) and in <c>FamilyClassifier.FindType</c> (which reports a
    /// missing type), plus the files that reflect over other mods' or Steamworks' assemblies. A new
    /// <c>GetField("...")</c> anywhere else fails here: route it through a probe.
    /// </summary>
    public sealed class GameLayerReflectionRuleTests
    {
        private static readonly Regex Lookup = new Regex(@"\.(GetField|GetProperty|GetMethod|GetNestedType|GetEvent)\s*\(|\bAssembly\s*\.\s*GetType\s*\(|\bType\s*\.\s*GetType\s*\(",
                                                          RegexOptions.CultureInvariant);

        // file (below src/SkylineTogether) -> why it may look members up by name
        private static readonly Dictionary<string, string> Allowed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Diagnostics/Probes.cs"] = "the probes themselves",
            ["Sync/FamilyClassifier.cs"] = "FindType, which reports every missing name",
            ["Compat/HarmonyPatches.cs"] = "Harmony's own API, found in whichever mod ships it",
            ["Compat/ModAdapter.cs"] = "types of other mods, per adapter (the adapter check reports missing ones)",
            ["Compat/ModCompat.cs"] = "other mods' settings and the adapters' watched types (reported by the adapter check)",
            ["Compat/ModIntegration.cs"] = "the integration API other mods declare",
            ["Compat/ModSettingsSync.cs"] = "other mods' settings properties",
            ["Net/SteamLanes.cs"] = "a Steamworks.NET internal, probed by P-STEAMLANES",
            ["Session/JoinAssetTransfer.cs"] = "asset type names received from the host, validated against an allow list",
        };

        [Test]
        public void GameMembersAreLookedUpOnlyThroughProbes()
        {
            if (TestPaths.RepoRoot == null) Assert.Skip("the repository is not available");
            string root = Path.Combine(TestPaths.RepoRoot, "src", "SkylineTogether");
            var problems = new List<string>();
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.StartsWith("bin/", StringComparison.Ordinal) || rel.StartsWith("obj/", StringComparison.Ordinal)) continue;
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].TrimStart();
                    if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("*", StringComparison.Ordinal)) continue;
                    if (!Lookup.IsMatch(lines[i])) continue;
                    if (Allowed.ContainsKey(rel))
                    {
                        used.Add(rel);
                        continue;
                    }
                    problems.Add(rel + ":" + (i + 1) + ": " + line);
                }
            }
            foreach (string rel in Allowed.Keys)
            {
                if (!used.Contains(rel)) problems.Add(rel + " is allowed but looks nothing up any more: remove it from the list");
            }
            if (problems.Count > 0)
            {
                Assert.Fail("members or types looked up by name outside Diagnostics/Probes.cs (add a probe and read Probes.X; see CONTRIBUTING.md):\n" + string.Join("\n", problems));
            }
        }
    }
}
