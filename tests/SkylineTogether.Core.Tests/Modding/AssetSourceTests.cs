using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Json;
using SkylineTogether.Core.Modding;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Modding
{
    /// <summary>
    /// Missing assets and runtime prefabs (docs/design/MOD-COMPATIBILITY.md §7): the source record, the snapshot's source
    /// list, the actionable texts, and the name fallback that binds deterministic runtime prefabs across peers.
    /// </summary>
    public sealed class AssetSourceTests
    {
        private static AssetSource Pack(string name = "China Pack", string id = "100454") => new AssetSource { Kind = AssetSourceKind.Package, Name = name, PlatformId = id };

        [Test]
        public void PrefabSourceRecordsRoundTrip()
        {
            var w = new SyncBatch.Writer();
            w.PrefabSource(42, Pack());
            w.PrefabSource(43, new AssetSource { Kind = AssetSourceKind.RuntimePrefab, Name = "Road Builder", PlatformId = null });
            w.PrefabSource(44, new AssetSource { Kind = AssetSourceKind.LocalAsset, Name = new string('n', 500), PlatformId = "\u0001x" });
            ArraySegment<byte> s = w.Finish();
            List<SyncRecord> records = SyncBatch.Read(s.Array, s.Offset, s.Count);
            Assert.Equal(3, records.Count);
            Assert.Equal(SyncRecordKind.PrefabSource, records[0].Kind);
            Assert.Equal(42u, records[0].NetId);
            Assert.Equal(Pack(), records[0].Source);
            Assert.Equal(AssetSourceKind.RuntimePrefab, records[1].Source.Kind);
            Assert.Equal("", records[1].Source.PlatformId);
            Assert.True(records[2].Source.Name.Length <= AssetSource.MaxNameBytes, "long names are clipped");
            Assert.Equal("x", records[2].Source.PlatformId, "control characters are stripped");
            Assert.False(SyncBatch.AcceptedFromClient(SyncRecordKind.PrefabSource));
            Assert.Throws<ArgumentNullException>(() => new SyncBatch.Writer().PrefabSource(1, null));
        }

        [Test]
        public void TheSnapshotManifestCarriesSourcesOnlyWhenThereAreAny()
        {
            var m = new SnapshotManifest { SnapshotId = 7, ChunkSize = 1024, GameModeName = "NormalMode" };
            byte[] without = Write(m);
            m.AssetSources = new List<AssetSourceUse>();
            Assert.SequenceEqual(without, Write(m), "an empty list keeps the old bytes");
            m.FamilyCounts = null;
            byte[] withoutCounts = Write(m);
            Assert.Null(SnapshotManifestCodec.Decode(withoutCounts, 0, withoutCounts.Length).AssetSources);

            m.FamilyCounts = new Dictionary<Family, int> { { Family.Object, 3 } };
            m.AssetSources = new List<AssetSourceUse>
            {
                new AssetSourceUse { Source = Pack(), Prefabs = 12, Example = "Pagoda" },
                new AssetSourceUse { Source = new AssetSource { Kind = AssetSourceKind.RuntimePrefab, Name = "Find It" }, Prefabs = 2, Example = "Tree Variant 3" },
            };
            byte[] bytes = Write(m);
            SnapshotManifest back = SnapshotManifestCodec.Decode(bytes, 0, bytes.Length);
            Assert.Equal(3, back.FamilyCounts[Family.Object]);
            Assert.Equal(2, back.AssetSources.Count);
            Assert.Equal(Pack(), back.AssetSources[0].Source);
            Assert.Equal(12, back.AssetSources[0].Prefabs);
            Assert.Equal("Tree Variant 3", back.AssetSources[1].Example);

            m.FamilyCounts = null;
            bytes = Write(m);
            back = SnapshotManifestCodec.Decode(bytes, 0, bytes.Length);
            Assert.Null(back.FamilyCounts);
            Assert.Equal(2, back.AssetSources.Count);

            // an unknown source kind is refused
            bytes[bytes.Length - (1 + 1 + 7 + 1 + 1 + 1 + 14)] = 9; // kind, "Find It", "", 2, "Tree Variant 3"
            Assert.Throws<DecodeException>(() => SnapshotManifestCodec.Decode(bytes, 0, bytes.Length));
        }

        private static byte[] Write(SnapshotManifest m)
        {
            var w = new ByteWriter();
            SnapshotManifestCodec.Write(w, m);
            return w.ToArray();
        }

        [Test]
        public void TextsNameTheSourceAndWhatToDo()
        {
            Assert.Equal("'Pagoda' (StaticObjectPrefab) from the package \"China Pack\" (Paradox Mods 100454): subscribe to Paradox Mods 100454, restart the game and join again",
                         MissingAssets.Describe("StaticObjectPrefab:Pagoda (0123abcd)", "Pagoda", Pack()));
            string runtime = MissingAssets.Describe("NetPrefab:Custom Road 4", "Custom Road 4", new AssetSource { Kind = AssetSourceKind.RuntimePrefab, Name = "Road Builder" });
            Assert.Contains("from the mod \"Road Builder\" (created at runtime)", runtime);
            Assert.Contains("same configuration", runtime);
            string local = MissingAssets.Describe("BuildingPrefab:My House", "My House", new AssetSource { Kind = AssetSourceKind.LocalAsset, Name = "My House" });
            Assert.Contains("a local asset of the host", local);
            Assert.Contains("cannot be downloaded", local);
            Assert.Contains("subscribe to the same asset packs and mods", MissingAssets.Describe("X:Y", "Y", null));
            Assert.Contains("'Y' (X)", MissingAssets.Describe("X:Y", "Y", null));
            Assert.Contains("'#5'", MissingAssets.Describe("#5", null, null));
        }

        [Test]
        public void TheSummaryListsTheBiggestSourcesFirst()
        {
            var missing = new List<KeyValuePair<AssetSource, int>>
            {
                new KeyValuePair<AssetSource, int>(new AssetSource { Kind = AssetSourceKind.RuntimePrefab, Name = "Find It" }, 1),
                new KeyValuePair<AssetSource, int>(Pack(), 9),
                new KeyValuePair<AssetSource, int>(Pack("Trees", "1"), 2),
                new KeyValuePair<AssetSource, int>(null, 1),
            };
            string text = MissingAssets.Summary(missing, null, 2);
            Assert.True(text.IndexOf("China Pack", StringComparison.Ordinal) < text.IndexOf("Trees", StringComparison.Ordinal));
            Assert.Contains("China Pack (Paradox Mods 100454): subscribe to it, restart the game and join again", text);
            Assert.DoesNotContain("Find It", text);
            Assert.Contains("; and 2 more. The diagnostics file lists them all.", text);
            Assert.DoesNotContain("prefab", text, "no jargon for players");
            Assert.Null(MissingAssets.Summary(new List<KeyValuePair<AssetSource, int>>()));
            Assert.Null(MissingAssets.Summary(null));
        }

        [Test]
        public void TheSummaryIsInThePlayersLanguage()
        {
            var missing = new List<KeyValuePair<AssetSource, int>>
            {
                new KeyValuePair<AssetSource, int>(new AssetSource { Kind = AssetSourceKind.RuntimePrefab, Name = "Find It" }, 3),
                new KeyValuePair<AssetSource, int>(new AssetSource { Kind = AssetSourceKind.LocalAsset, Name = "My House" }, 2),
                new KeyValuePair<AssetSource, int>(new AssetSource { Kind = AssetSourceKind.Package, Name = "Trees" }, 1),
            };
            var german = new Dictionary<string, string>
            {
                [MissingAssets.ToastKey] = "Weggelassen: {LIST}.",
                [MissingAssets.ModKey] = "Mod {NAME}",
                [MissingAssets.LocalKey] = "lokal {NAME}",
            };
            string text = MissingAssets.Summary(missing, k => german.TryGetValue(k, out string v) ? v : null);
            Assert.Equal("Weggelassen: Mod Find It; lokal My House; Trees: subscribe to it, restart the game and join again.", text, "a key the table lacks falls back to English");
            // sources without a name are not listed; with none listable the toast says so without a list
            var unnamed = new List<KeyValuePair<AssetSource, int>> { new KeyValuePair<AssetSource, int>(new AssetSource(), 4) };
            Assert.Equal(MissingAssets.English[MissingAssets.UnknownKey], MissingAssets.Summary(unnamed));
            Assert.Equal("Weggelassen: Mod Find It; and 1 more.", MissingAssets.Summary(new List<KeyValuePair<AssetSource, int>>
            {
                new KeyValuePair<AssetSource, int>(new AssetSource { Kind = AssetSourceKind.RuntimePrefab, Name = "Find It" }, 3),
                new KeyValuePair<AssetSource, int>(null, 1),
            }, k => german.TryGetValue(k, out string v) ? v : null));
        }

        [Test]
        public void TheLocaleTablesTranslateTheSummary()
        {
            if (!TestPaths.TryGetRepoFile("src/SkylineTogether/Data/locale.en-US.json", out string en) || !TestPaths.TryGetRepoFile("src/SkylineTogether/Data/locale.de-DE.json", out string de))
            {
                Assert.Skip("the repository's data files are not reachable");
                return;
            }
            JsonValue english = MiniJson.Parse(File.ReadAllText(en, Encoding.UTF8), "en");
            JsonValue german = MiniJson.Parse(File.ReadAllText(de, Encoding.UTF8), "de");
            foreach (KeyValuePair<string, string> e in MissingAssets.English)
            {
                Assert.True(new List<string>(english.Keys).Contains(e.Key), "en lacks " + e.Key);
                Assert.True(new List<string>(german.Keys).Contains(e.Key), "de lacks " + e.Key);
                Assert.Equal(e.Value, english[e.Key].AsString(), "the English table matches the fallback: " + e.Key);
            }
        }

        [Test]
        public void PrefabIdsParse()
        {
            Assert.True(MissingAssets.TryParsePrefabId("NetPrefab:Road 2", "Road 2", out string type, out bool hash));
            Assert.Equal("NetPrefab", type);
            Assert.False(hash);
            Assert.True(MissingAssets.TryParsePrefabId("NetPrefab:Road (2) (9f00)", "Road (2)", out _, out hash));
            Assert.True(hash);
            Assert.True(MissingAssets.TryParsePrefabId("NetPrefab:Road (2)", "Road (2)", out _, out hash));
            Assert.False(hash, "a name that ends in brackets is not a hash");
            Assert.True(MissingAssets.TryParsePrefabId("NetPrefab:Road (9f00)", null, out _, out hash));
            Assert.True(hash);
            Assert.False(MissingAssets.TryParsePrefabId("NetPrefab:Other", "Road", out _, out _));
            Assert.False(MissingAssets.TryParsePrefabId(":Road", "Road", out _, out _));
            Assert.False(MissingAssets.TryParsePrefabId(null, "Road", out _, out _));
        }

        [Test]
        public void TheNameFallbackBindsRuntimePrefabsButNeverGuesses()
        {
            var runtime = new MissingAssets.Candidate { HasAsset = false, HasHash = false };
            var otherPack = new MissingAssets.Candidate { HasAsset = true, HasHash = true };
            var hashless = new MissingAssets.Candidate { HasAsset = true, HasHash = false };
            // the host's prefab has no hash: any single same-named prefab is it
            Assert.Equal(0, MissingAssets.ChooseNameFallback(false, new[] { otherPack }));
            // the host's prefab has a hash (it reused a file); a local runtime prefab of the same name is the same prefab
            Assert.Equal(0, MissingAssets.ChooseNameFallback(true, new[] { runtime }));
            Assert.Equal(1, MissingAssets.ChooseNameFallback(true, new[] { otherPack, hashless }));
            // two assets of different packages never match
            Assert.Equal(-1, MissingAssets.ChooseNameFallback(true, new[] { otherPack }));
            // ambiguous: reported missing rather than guessed
            Assert.Equal(-1, MissingAssets.ChooseNameFallback(false, new[] { runtime, otherPack }));
            Assert.Equal(-1, MissingAssets.ChooseNameFallback(true, new MissingAssets.Candidate[0]));
            Assert.Equal(-1, MissingAssets.ChooseNameFallback(true, null));
        }
    }
}
