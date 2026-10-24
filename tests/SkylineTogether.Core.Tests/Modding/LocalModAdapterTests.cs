using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Modding
{
    /// <summary>
    /// The adapters of client-local mods (<c>src/SkylineTogether/Compat/LocalModAdapters.cs</c>, docs/design/MOD-COMPATIBILITY.md
    /// §2), checked on the game layer's sources (they cannot be compiled here): each is registered in <c>ModCompat</c>,
    /// belongs to a mod the built-in list knows as client-local with a check against one version, and every system it
    /// gates or keeps and every type it excludes is one of the parts of that check (a system named in the check, a saved
    /// type the mod never writes), so the adapter and the classification describe the same version.
    /// </summary>
    public sealed class LocalModAdapterTests
    {
        private const string Adapters = "src/SkylineTogether/Compat/LocalModAdapters.cs";
        private const string Registry = "src/SkylineTogether/Compat/ModCompat.cs";

        private static readonly Regex Class = new Regex(@"public sealed class (\w+Adapter) : ModAdapter\s*\{(.*?)\n    \}", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        private static readonly Regex Assembly = new Regex("AssemblyName => \"([^\"]+)\"", RegexOptions.CultureInvariant);
        private static readonly Regex Names = new Regex(@"(JoinOffSystems|ClientOffSystems|ClientSimulationSystems|NeverReplicatedTypes|LocalTypes) => new\[\] \{([^}]*)\}", RegexOptions.CultureInvariant);

        private static string Read(string relative)
        {
            if (!TestPaths.TryGetRepoFile(relative, out string path)) Assert.Fail(relative + " is missing");
            return File.ReadAllText(path);
        }

        [Test]
        public void EveryLocalModAdapterIsRegisteredAndMatchesTheCheckedVersion()
        {
            string adapters = Read(Adapters);
            string registry = Read(Registry);
            var seen = new List<string>();
            foreach (Match m in Class.Matches(adapters))
            {
                string name = m.Groups[1].Value;
                string body = m.Groups[2].Value;
                seen.Add(name);
                Assert.Contains("new " + name + "()", registry, name + " is not registered in ModCompat");
                Match a = Assembly.Match(body);
                Assert.True(a.Success, name + " names no assembly");
                string assembly = a.Groups[1].Value;
                Assert.True(ModCompatibility.IsKnownClientLocal(assembly, null), assembly + " is not on the built-in client-local list");
                Assert.True(ModCompatibility.HasVerifiedProfile(assembly, null), assembly + " has no check against one version");
                IReadOnlyList<string> verified = ModCompatibility.VerifiedLocalItems(assembly);
                foreach (Match list in Names.Matches(body))
                {
                    foreach (Match q in Regex.Matches(list.Groups[2].Value, "\"([^\"]+)\""))
                    {
                        string target = q.Groups[1].Value;
                        Assert.True(target.StartsWith(assembly + ".", StringComparison.Ordinal), name + ": " + target + " is not a type of " + assembly);
                        bool covered = false;
                        if (list.Groups[1].Value.EndsWith("Systems", StringComparison.Ordinal))
                        {
                            foreach (string v in verified) covered |= v.StartsWith("system " + target + " in ", StringComparison.Ordinal);
                        }
                        else
                        {
                            foreach (string t in CityDataRule.DormantSavedTypes(assembly)) covered |= t == target;
                            foreach (string v in verified) covered |= v == "saved component " + target;
                        }
                        Assert.True(covered, name + ": " + target + " is not part of the check of " + assembly);
                    }
                }
            }
            Assert.SequenceEqual(new[] { "AchievementFixerAdapter", "CimCensusAdapter", "EmploymentTrackerAdapter" }, seen);
        }

        [Test]
        public void TheUnusedSavedTypeIsNeverSentAndCountsOnlyWhereEntitiesHaveIt()
        {
            // Cim Route Highlighter: the same type is excluded by the adapter and dormant for the city-data rule
            string adapters = Read(Adapters);
            Assert.Contains("NeverReplicatedTypes => new[] { \"EmploymentTracker.DeleteTimer\" }", adapters);
            Assert.SequenceEqual(new[] { "EmploymentTracker.DeleteTimer" }, CityDataRule.DormantSavedTypes("EmploymentTracker"));
            Assert.Contains("saved component EmploymentTracker.DeleteTimer", ModCompatibility.VerifiedLocalItems("EmploymentTracker"));
            Assert.Equal(0, ModCompatibility.VerifiedLocalItems("Traffic").Count);
        }
    }
}
