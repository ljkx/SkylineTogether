using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SkylineTogether.Core.Diagnostics;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Diagnostics
{
    /// <summary>
    /// <see cref="ModFeatureMap"/> (P-REFS attribution, ARCHITECTURE.md#probes-and-self-test): every type of the game layer
    /// maps to a feature or deliberately to none, read from <c>src/SkylineTogether/Data/mod-types.generated.txt</c> (the
    /// type list of the built <c>SkylineTogether.dll</c>, rewritten by <c>tools/build.sh</c> after every build with the
    /// game, so this runs in CI without the game); every feature id it names is one the game layer registers; no entry is
    /// stale; the types and methods the round-4 review named map to their own, non-critical features; and a type the map
    /// does not know gets no feature (a warning), never the critical city sync.
    /// </summary>
    public sealed class ModFeatureMapTests
    {
        private const string TypeList = "src/SkylineTogether/Data/mod-types.generated.txt";
        private const string FeaturesSource = "src/SkylineTogether/Diagnostics/Features.cs";

        private static List<string> ModTypes()
        {
            if (!TestPaths.TryGetRepoFile(TypeList, out string path)) Assert.Fail(TypeList + " is missing (tools/build.sh writes it; commit it)");
            var types = new List<string>();
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                string t = line.Trim();
                if (t.Length > 0 && !t.StartsWith("#", StringComparison.Ordinal)) types.Add(t);
            }
            Assert.True(types.Count > 100, "the type list has " + types.Count + " types");
            return types;
        }

        // feature id -> critical, from the game layer's Registry.Add("id", "title", critical) calls
        private static Dictionary<string, bool> RegisteredFeatures()
        {
            if (!TestPaths.TryGetRepoFile(FeaturesSource, out string path)) Assert.Fail(FeaturesSource + " is missing");
            var features = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(File.ReadAllText(path), "Registry\\.Add\\(\"([a-z]+)\",\\s*\"[^\"]*\",\\s*(true|false)\\)"))
            {
                features[m.Groups[1].Value] = m.Groups[2].Value == "true";
            }
            Assert.True(features.Count >= 20, "registered features found: " + features.Count);
            return features;
        }

        [Test]
        public void EveryTypeOfTheGameLayerMapsToAFeatureOrDeliberatelyToNone()
        {
            var unmapped = ModTypes().Where(t => ModFeatureMap.Of(t) == null).ToList();
            Assert.Equal(0, unmapped.Count, "types of SkylineTogether.dll that ModFeatureMap does not place (add an entry for each): " + string.Join(", ", unmapped));
        }

        [Test]
        public void EveryFeatureTheMapNamesIsRegistered()
        {
            Dictionary<string, bool> registered = RegisteredFeatures();
            foreach (string id in ModFeatureMap.FeatureIds())
            {
                Assert.True(registered.ContainsKey(id), "ModFeatureMap names the feature '" + id + "', which Features.cs does not register");
            }
        }

        [Test]
        public void NoEntryIsStale()
        {
            var types = new HashSet<string>(ModTypes(), StringComparer.Ordinal);
            string sources = TestPaths.RepoRoot == null ? null : Path.Combine(TestPaths.RepoRoot, "src", "SkylineTogether");
            Assert.True(sources != null && Directory.Exists(sources), "the game layer's sources");
            string allSource = string.Join("\n", Directory.GetFiles(sources, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
            foreach ((string key, string[] _) in ModFeatureMap.Entries)
            {
                if (key.EndsWith(".", StringComparison.Ordinal)) continue; // namespace entries (compiler attributes, Core)
                int sep = key.IndexOf("::", StringComparison.Ordinal);
                string type = sep >= 0 ? key.Substring(0, sep) : key;
                Assert.True(types.Contains(type), "the entry " + key + " names a type SkylineTogether.dll does not have");
                if (sep >= 0)
                {
                    string method = key.Substring(sep + 2);
                    Assert.True(Regex.IsMatch(allSource, "\\b" + Regex.Escape(method) + "\\s*(<[^>]*>)?\\("), "the entry " + key + " names a method the game layer's sources do not declare");
                }
            }
        }

        [Test]
        public void TheReviewedTypesMapToTheirOwnFeatures()
        {
            Dictionary<string, bool> critical = RegisteredFeatures();
            void Expect(string type, string method, params string[] features)
            {
                string[] got = ModFeatureMap.Of(type, method);
                Assert.NotNull(got, type + "::" + method + " is not mapped");
                Assert.SequenceEqual(features, got, type + "::" + method);
                foreach (string f in got) Assert.False(critical[f], type + " maps to the critical feature " + f);
            }
            const string sync = "SkylineTogether.Sync.";
            Expect(sync + "AttentionValues", null, "simvals");
            Expect(sync + "AttentionValues+HostPeer", null, "simvals");
            Expect(sync + "SignalFollower", "Update()", "simvals");
            Expect(sync + "LineCheck", null, "integrity");
            Expect(sync + "GpuReadback`1", "Start(Texture, Int32)", "terrain", "water");
            Expect(sync + "UploadTextures", null, "terrain");
            Expect("SkylineTogether.Diagnostics.InstallInfo", "CheckAtStartup(ILog)");
            Expect("SkylineTogether.Diagnostics.DiagnosticsDump", null);
            Expect("SkylineTogether.Replay.ReplayController", null);
            Expect("SkylineTogether.Replay.ReplayController+ReplayWorld", null);
            // the codec's simulation-value partial, per method, with its lambdas, local functions and state machines
            Expect(sync + "EntityCodec", "ApplySimValues(Byte[], Int32, Int32, List`1, ApplyResult)", "simvals");
            Expect(sync + "EntityCodec", "<ApplySimValues>b__12_0(Entity)", "simvals");
            Expect(sync + "EntityCodec+<>c__DisplayClass40_0", "<HashSimValues>b__0()", "simvals", "agents");
            Expect(sync + "EntityCodec+<>c", "<ApplyAgentValues>g__Local|7_0()", "agents");
            Expect(sync + "SyncController+<CaptureModChanges>d__3", "MoveNext()", "modcompat");
            Expect(sync + "SyncController", "ReleaseHolds()", "modcompat");
        }

        [Test]
        public void TheCitySyncKeepsItsOwnCode()
        {
            const string sync = "SkylineTogether.Sync.";
            Assert.SequenceEqual(new[] { "replication" }, ModFeatureMap.Of(sync + "EntityCodec", "Capture(Entity)"));
            Assert.SequenceEqual(new[] { "replication" }, ModFeatureMap.Of(sync + "EntityCodec", "TrySerializeSim(TypeHandler, Entity, Int32&)"), "shared with the structure write");
            Assert.SequenceEqual(new[] { "replication" }, ModFeatureMap.Of(sync + "EntityCodec+<>c__DisplayClass9_0", "<WriteRecord>b__0()"));
            Assert.SequenceEqual(new[] { "replication" }, ModFeatureMap.Of(sync + "SyncController", "WriteDependencies(HashSet`1, List`1)"));
            Assert.SequenceEqual(new[] { "replication" }, ModFeatureMap.Of(sync + "NetWriter"));
            Assert.SequenceEqual(new[] { "replication" }, ModFeatureMap.Of(sync + "NetWriterState+KeyBuilder"));
            Assert.SequenceEqual(new[] { "session" }, ModFeatureMap.Of("SkylineTogether.Core.Session.SessionHost"));
        }

        [Test]
        public void AnUnknownTypeGetsNoFeatureNeverTheCriticalFallback()
        {
            Assert.Null(ModFeatureMap.Of("Game.Prefabs.PrefabBase"), "a game type (Mono's loader names the missing base type)");
            Assert.Null(ModFeatureMap.Of("SkylineTogether.Sync.SomethingNew"));
            Assert.Null(ModFeatureMap.Of("SkylineTogether.Sync.EventSyncHelper"), "entries match whole type names, not prefixes");
            Assert.Null(ModFeatureMap.Of(null));
            Assert.Null(ModFeatureMap.Of(""));
            Assert.SequenceEqual(new[] { "events" }, ModFeatureMap.Of("SkylineTogether.Sync.EventSync+Watch"), "a nested type is its outer type's");
            Assert.Equal(0, ModFeatureMap.Of("<PrivateImplementationDetails>+__StaticArrayInitTypeSize=16").Length);
        }

        [Test]
        public void NamesAreNormalisedToTheMethodTheyBelongTo()
        {
            Assert.Equal("A.B", ModFeatureMap.Normalize("A.B+<Run>d__3", "MoveNext()", out string name));
            Assert.Equal("Run", name);
            Assert.Equal("A.B", ModFeatureMap.Normalize("A.B+<>c__DisplayClass5_0", "<Run>b__0(Int32)", out name));
            Assert.Equal("Run", name);
            Assert.Equal("A.B", ModFeatureMap.Normalize("A.B", "<Run>g__Local|2_0()", out name));
            Assert.Equal("Run", name);
            Assert.Equal("A.B+Inner", ModFeatureMap.Normalize("A.B+Inner", "Go(String)", out name));
            Assert.Equal("Go", name);
            Assert.Equal("A.B", ModFeatureMap.Normalize("A.B", null, out name));
            Assert.Null(name);
        }
    }
}
