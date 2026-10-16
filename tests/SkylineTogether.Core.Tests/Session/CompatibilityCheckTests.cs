using System.Collections.Generic;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// The host's per-category compatibility decision (ARCHITECTURE.md#handshake-and-admission): DLC the joiner lacks
    /// refuses, asset packs are told (or refused with "require the same assets"), mod settings are told, and categories
    /// with equal digests are not compared item by item.
    /// </summary>
    public sealed class CompatibilityCheckTests
    {
        private static ContentEntry Dlc(string id, string name) => new ContentEntry { Id = id, Name = name };

        private static ContentEntry Pack(string id, string name, string version = "1.0", ulong digest = 1, int prefabs = 10) =>
            new ContentEntry { Id = id, Name = name, Version = version, Digest = digest, Prefabs = prefabs };

        private static HandshakeManifest Manifest(List<ContentEntry> content = null, List<ContentEntry> packs = null, List<SettingEntry> settings = null, List<ModEntry> mods = null)
        {
            var m = new HandshakeManifest
            {
                Mods = mods ?? new List<ModEntry> { new ModEntry { Name = "Traffic", Version = "1.0", PlatformId = "80095", RegistersTypesOrSystemsOrPrefabs = true } },
                Content = content ?? new List<ContentEntry> { Dlc("2427731", "San Francisco Set"), Dlc("2427740", "Beach Properties") },
                AssetPacks = packs ?? new List<ContentEntry> { Pack("75426", "Bus Stops"), Pack("80010", "Trees", "2.1", 7, 40) },
                Settings = settings ?? new List<SettingEntry> { new SettingEntry { Mod = "Traffic", Key = "AllowUTurns", Value = "True" } },
            };
            CompatibilityDigests.Fill(m);
            return m;
        }

        [Test]
        public void TheSameManifestJoinsWithoutANotice()
        {
            CompatibilityResult r = CompatibilityCheck.Compare(Manifest(), Manifest(), new CompatibilityOptions { RequireSameAssets = true });
            Assert.Null(r.Refusal);
            Assert.Null(r.Notice);
        }

        [Test]
        public void MissingDlcIsRefusedWithItsName()
        {
            HandshakeManifest client = Manifest(content: new List<ContentEntry> { Dlc("2427731", "San Francisco Set") });
            CompatibilityResult r = CompatibilityCheck.Compare(Manifest(), client, null);
            Assert.NotNull(r.Refusal);
            Assert.Equal("content", r.Refusal.Kind);
            Assert.Equal(1, r.Refusal.ContentMissing.Count);
            Assert.Equal("Beach Properties", r.Refusal.ContentMissing[0].Name);
            Assert.Null(r.Refusal.ContentMissing[0].PlatformId, "a DLC id is no Paradox Mods id");
            Assert.Contains("Beach Properties", r.RefusalDetail);
            Assert.DoesNotContain("Paradox Mods", r.RefusalDetail);
        }

        [Test]
        public void DlcOnlyTheJoinerOwnsIsToldNotRefused()
        {
            var content = new List<ContentEntry> { Dlc("2427731", "San Francisco Set"), Dlc("2427740", "Beach Properties"), Dlc("2427750", "Modern Architecture") };
            CompatibilityResult r = CompatibilityCheck.Compare(Manifest(), Manifest(content: content), null);
            Assert.Null(r.Refusal);
            Assert.NotNull(r.Notice);
            Assert.Equal("notice", r.Notice.Kind);
            Assert.Equal("Modern Architecture", r.Notice.ContentExtra[0].Name);
        }

        [Test]
        public void AssetPackDifferencesAreToldWithNamesAndIds()
        {
            var packs = new List<ContentEntry> { Pack("80010", "Trees", "2.2", 8, 41), Pack("90001", "Fences") };
            CompatibilityResult r = CompatibilityCheck.Compare(Manifest(), Manifest(packs: packs), new CompatibilityOptions());
            Assert.Null(r.Refusal, "asset packs only warn by default");
            MismatchReport n = r.Notice;
            Assert.NotNull(n);
            Assert.Equal(1, n.AssetsMissing.Count);
            Assert.Equal("Bus Stops", n.AssetsMissing[0].Name);
            Assert.Equal("75426", n.AssetsMissing[0].PlatformId);
            Assert.Equal("Fences", n.AssetsExtra[0].Name);
            Assert.Equal(1, n.AssetVersions.Count);
            Assert.Equal("Trees", n.AssetVersions[0].Name);
            Assert.Equal("2.1", n.AssetVersions[0].HostVersion);
            Assert.Equal("2.2", n.AssetVersions[0].ClientVersion);
            Assert.Contains("Bus Stops (Paradox Mods 75426)", n.Describe());
        }

        [Test]
        public void TheSameVersionWithOtherContentShowsThePrefabCounts()
        {
            var packs = new List<ContentEntry> { Pack("75426", "Bus Stops"), Pack("80010", "Trees", "2.1", 9, 38) };
            MismatchReport n = CompatibilityCheck.Compare(Manifest(), Manifest(packs: packs), null).Notice;
            Assert.Equal("2.1 (40)", n.AssetVersions[0].HostVersion);
            Assert.Equal("2.1 (38)", n.AssetVersions[0].ClientVersion);
        }

        [Test]
        public void RequireSameAssetsRefusesAnyAssetDifference()
        {
            var options = new CompatibilityOptions { RequireSameAssets = true };
            HandshakeManifest extraOnly = Manifest(packs: new List<ContentEntry> { Pack("75426", "Bus Stops"), Pack("80010", "Trees", "2.1", 7, 40), Pack("90001", "Fences") });
            CompatibilityResult r = CompatibilityCheck.Compare(Manifest(), extraOnly, options);
            Assert.NotNull(r.Refusal);
            Assert.Equal("assets", r.Refusal.Kind);
            Assert.Equal("Fences", r.Refusal.AssetsExtra[0].Name);
            Assert.Contains("requires the same asset packs", r.RefusalDetail);
        }

        [Test]
        public void AnAssetListThatWasNotSentIsReportedAsUnknown()
        {
            HandshakeManifest client = Manifest(packs: new List<ContentEntry> { Pack("1", "Other") });
            client.AssetPacks = null; // too long to send; the digest still says "different"
            CompatibilityResult told = CompatibilityCheck.Compare(Manifest(), client, null);
            Assert.Null(told.Refusal);
            Assert.True(told.Notice.AssetsUnknown);
            CompatibilityResult refused = CompatibilityCheck.Compare(Manifest(), client, new CompatibilityOptions { RequireSameAssets = true });
            Assert.Equal("assets", refused.Refusal.Kind);
            Assert.True(refused.Refusal.AssetsUnknown);

            // equal digests: nothing to report even without the list
            HandshakeManifest same = Manifest();
            same.AssetPacks = null;
            Assert.Null(CompatibilityCheck.Compare(Manifest(), same, new CompatibilityOptions { RequireSameAssets = true }).Notice);
        }

        [Test]
        public void ModSettingsAreToldWithBothValuesAndNeverRefuse()
        {
            var settings = new List<SettingEntry>
            {
                new SettingEntry { Mod = "Traffic", Key = "AllowUTurns", Value = "False" },
                new SettingEntry { Mod = "Other", Key = "OnlyHere", Value = "1" },
            };
            CompatibilityResult r = CompatibilityCheck.Compare(Manifest(), Manifest(settings: settings), new CompatibilityOptions { RequireSameAssets = true });
            Assert.Null(r.Refusal);
            Assert.Equal(1, r.Notice.Settings.Count, "a setting only one side lists belongs to a mod difference");
            SettingDifference d = r.Notice.Settings[0];
            Assert.Equal("Traffic", d.Mod);
            Assert.Equal("AllowUTurns", d.Key);
            Assert.Equal("True", d.HostValue);
            Assert.Equal("False", d.ClientValue);
        }

        [Test]
        public void ModDifferencesRefuseOnlyWhenRequired()
        {
            HandshakeManifest client = Manifest(mods: new List<ModEntry>());
            CompatibilityResult required = CompatibilityCheck.Compare(Manifest(), client, new CompatibilityOptions { RequireSameMods = true });
            Assert.Equal("mods", required.Refusal.Kind);
            Assert.Contains("Missing (enable these): Traffic 1.0", required.RefusalDetail);
            CompatibilityResult allowed = CompatibilityCheck.Compare(Manifest(), client, new CompatibilityOptions { RequireSameMods = false });
            Assert.Null(allowed.Refusal);
            Assert.Equal("Traffic", allowed.Notice.Missing[0].Name);
        }

        [Test]
        public void AModListThatWasNotSentStillRefusesByDigest()
        {
            HandshakeManifest client = Manifest(mods: new List<ModEntry>());
            client.Mods = null; // too many mods to send: digests only
            CompatibilityResult r = CompatibilityCheck.Compare(Manifest(), client, new CompatibilityOptions { RequireSameMods = true });
            Assert.Equal("mods", r.Refusal?.Kind);
            Assert.Equal(0, r.Refusal.Missing.Count, "no names it could not compare");
            Assert.Equal("Your mods differ from the host's.", r.RefusalDetail);
            HandshakeManifest same = Manifest();
            same.Mods = null;
            Assert.Null(CompatibilityCheck.Compare(Manifest(), same, new CompatibilityOptions { RequireSameMods = true }).Refusal);
        }

        [Test]
        public void EqualDigestsAreNotComparedItemByItem()
        {
            HandshakeManifest client = Manifest();
            client.AssetPacks = new List<ContentEntry> { Pack("1", "Something else") }; // digest left as for the original list
            client.Content = new List<ContentEntry>();
            Assert.Null(CompatibilityCheck.Compare(Manifest(), client, new CompatibilityOptions { RequireSameAssets = true }).Notice);
            Assert.True(CompatibilityCheck.Differs(0, 0), "an unfilled digest is compared");
            Assert.True(CompatibilityCheck.Differs(1, 2));
            Assert.False(CompatibilityCheck.Differs(3, 3));
        }

        [Test]
        public void ListsAreCapped()
        {
            var packs = new List<ContentEntry>();
            for (int i = 0; i < 300; i++) packs.Add(Pack("p" + i, "Pack " + i));
            MismatchReport n = CompatibilityCheck.Compare(Manifest(packs: packs), Manifest(packs: new List<ContentEntry>()), null).Notice;
            Assert.Equal(CompatibilityCheck.MaxListed, n.AssetsMissing.Count);
        }

        [Test]
        public void DigestsIgnoreOrderAndSeeEveryField()
        {
            var a = new List<ContentEntry> { Pack("1", "A"), Pack("2", "B") };
            var b = new List<ContentEntry> { Pack("2", "B"), Pack("1", "A") };
            Assert.Equal(CompatibilityDigests.OfContent(a), CompatibilityDigests.OfContent(b));
            Assert.NotEqual(CompatibilityDigests.OfContent(a), CompatibilityDigests.OfContent(new List<ContentEntry> { Pack("1", "A"), Pack("2", "B", digest: 2) }));
            Assert.NotEqual(CompatibilityDigests.OfContent(a), CompatibilityDigests.OfContent(new List<ContentEntry> { Pack("1", "A"), Pack("2", "B", "1.1") }));
            Assert.Equal(0UL, CompatibilityDigests.OfContent(new List<ContentEntry>()));
            Assert.Equal(0UL, CompatibilityDigests.OfSettings(null));
            Assert.NotEqual(0UL, CompatibilityDigests.OfMods(new List<ModEntry> { new ModEntry { Name = "x" } }));
            Assert.Equal(CompatibilityDigests.OfPrefabIds(new[] { "A", "B" }), CompatibilityDigests.OfPrefabIds(new[] { "B", "A" }));
            var settings = new List<SettingEntry> { new SettingEntry { Mod = "m", Key = "k", Value = "1" } };
            Assert.NotEqual(CompatibilityDigests.OfSettings(settings), CompatibilityDigests.OfSettings(new List<SettingEntry> { new SettingEntry { Mod = "m", Key = "k", Value = "2" } }));
        }

        [Test]
        public void TheNewReportRecordsRoundTrip()
        {
            var r = new MismatchReport { Kind = "notice", AssetsUnknown = true };
            r.ContentMissing.Add(new ModEntry { Name = "Beach Properties" });
            r.ContentExtra.Add(new ModEntry { Name = "Modern Architecture" });
            r.AssetsMissing.Add(new ModEntry { Name = "Bus Stops", Version = "1.0", PlatformId = "75426" });
            r.AssetsExtra.Add(new ModEntry { Name = "Fences", PlatformId = "90001" });
            r.AssetVersions.Add(new ModVersionDifference { Name = "Trees", HostVersion = "2.1", ClientVersion = "2.2", PlatformId = "80010" });
            r.Settings.Add(new SettingDifference { Mod = "Traffic", Key = "AllowUTurns", HostValue = "True", ClientValue = "False" });
            MismatchReport back = MismatchReport.Decode(MismatchReport.Encode(r));
            Assert.Equal("notice", back.Kind);
            Assert.True(back.AssetsUnknown);
            Assert.Equal("Beach Properties", back.ContentMissing[0].Name);
            Assert.Equal("Modern Architecture", back.ContentExtra[0].Name);
            Assert.Equal("75426", back.AssetsMissing[0].PlatformId);
            Assert.Equal("1.0", back.AssetsMissing[0].Version);
            Assert.Equal("90001", back.AssetsExtra[0].PlatformId);
            Assert.Equal("2.2", back.AssetVersions[0].ClientVersion);
            Assert.Equal("80010", back.AssetVersions[0].PlatformId);
            Assert.Equal("False", back.Settings[0].ClientValue);
            Assert.True(back.HasDifferences);
            string text = back.Describe();
            Assert.Contains("DLC or content packs you lack: Beach Properties", text);
            Assert.Contains("Asset packs you lack: Bus Stops (Paradox Mods 75426)", text);
            Assert.Contains("Asset packs in another version: Trees (Paradox Mods 80010) (host 2.1, you 2.2)", text);
            Assert.Contains("Traffic AllowUTurns (host True, you False)", text);
            // an older reader skips what it does not know; a newer record never breaks the mod lists
            Assert.False(MismatchReport.Decode(System.Text.Encoding.UTF8.GetBytes("kind\tmods\nfuture\tx\n")).HasDifferences);
        }

        // ------------------------------------------------------------------------------------------- join anyway

        private static CompatibilityOptions Options(bool allow, bool joinAnyway, bool sameAssets = false) =>
            new CompatibilityOptions { RequireSameMods = true, RequireSameAssets = sameAssets, AllowOverride = allow, JoinAnyway = joinAnyway };

        [Test]
        public void AModRefusalIsOverridableOnlyWhenTheHostAllowsIt()
        {
            HandshakeManifest client = Manifest(mods: new List<ModEntry>());
            CompatibilityResult allowed = CompatibilityCheck.Compare(Manifest(), client, Options(allow: true, joinAnyway: false));
            Assert.Equal("mods", allowed.Refusal?.Kind);
            Assert.True(allowed.Refusal.Overridable);
            Assert.Contains("The host lets you join anyway.", allowed.RefusalDetail);
            Assert.False(allowed.JoinAnywayIgnored);

            CompatibilityResult required = CompatibilityCheck.Compare(Manifest(), client, Options(allow: false, joinAnyway: false));
            Assert.False(required.Refusal.Overridable);
            Assert.DoesNotContain("join anyway", required.RefusalDetail);
            Assert.False(required.JoinAnywayIgnored, "nothing was asked");
        }

        [Test]
        public void JoinAnywayTurnsTheRefusalIntoANotice()
        {
            HandshakeManifest client = Manifest(mods: new List<ModEntry> { new ModEntry { Name = "Anarchy", Version = "1.0", PlatformId = "74604" } });
            CompatibilityResult r = CompatibilityCheck.Compare(Manifest(), client, Options(allow: true, joinAnyway: true));
            Assert.Null(r.Refusal);
            Assert.True(r.Notice.JoinedAnyway);
            Assert.False(r.Notice.Overridable);
            Assert.Equal("Traffic", r.Notice.Missing[0].Name);
            Assert.Equal("Anarchy", r.Notice.Extra[0].Name);

            MismatchReport back = MismatchReport.Decode(MismatchReport.Encode(r.Notice));
            Assert.True(back.JoinedAnyway);
            Assert.False(back.Overridable);
            Assert.Equal("Anarchy", back.Extra[0].Name);
        }

        [Test]
        public void JoinAnywayIsIgnoredWhenTheHostDoesNotAllowIt()
        {
            HandshakeManifest client = Manifest(mods: new List<ModEntry>());
            CompatibilityResult r = CompatibilityCheck.Compare(Manifest(), client, Options(allow: false, joinAnyway: true));
            Assert.Equal("mods", r.Refusal?.Kind);
            Assert.False(r.Refusal.Overridable);
            Assert.True(r.JoinAnywayIgnored);
            Assert.Null(r.Notice);
        }

        [Test]
        public void JoinAnywayNeverPassesMissingDlc()
        {
            HandshakeManifest client = Manifest(content: new List<ContentEntry>(), mods: new List<ModEntry>());
            foreach (bool joinAnyway in new[] { false, true })
            {
                CompatibilityResult r = CompatibilityCheck.Compare(Manifest(), client, Options(allow: true, joinAnyway: joinAnyway));
                Assert.Equal("content", r.Refusal?.Kind, "DLC first, whether or not the joiner chose to join anyway");
                Assert.False(r.Refusal.Overridable);
                Assert.DoesNotContain("join anyway", r.RefusalDetail);
            }
        }

        [Test]
        public void OneJoinAnywayPassesModsAndAssetPacks()
        {
            HandshakeManifest client = Manifest(mods: new List<ModEntry>(), packs: new List<ContentEntry> { Pack("75426", "Bus Stops") });
            CompatibilityResult refused = CompatibilityCheck.Compare(Manifest(), client, Options(allow: true, joinAnyway: false, sameAssets: true));
            Assert.Equal("mods", refused.Refusal?.Kind, "code mods are reported first");
            Assert.True(refused.Refusal.Overridable);

            HandshakeManifest assetsOnly = Manifest(packs: new List<ContentEntry> { Pack("75426", "Bus Stops") });
            CompatibilityResult assets = CompatibilityCheck.Compare(Manifest(), assetsOnly, Options(allow: true, joinAnyway: false, sameAssets: true));
            Assert.Equal("assets", assets.Refusal?.Kind);
            Assert.True(assets.Refusal.Overridable);
            Assert.Contains("The host lets you join anyway.", assets.RefusalDetail);

            CompatibilityResult joined = CompatibilityCheck.Compare(Manifest(), client, Options(allow: true, joinAnyway: true, sameAssets: true));
            Assert.Null(joined.Refusal);
            Assert.True(joined.Notice.JoinedAnyway);
            Assert.Equal("Traffic", joined.Notice.Missing[0].Name);
            Assert.Equal("Trees", joined.Notice.AssetsMissing[0].Name);
        }

        [Test]
        public void JoinAnywayMarksTheNoticeOnlyWhenItPassedARefusal()
        {
            // nothing differs: no notice
            Assert.Null(CompatibilityCheck.Compare(Manifest(), Manifest(), Options(allow: true, joinAnyway: true)).Notice);
            // asset packs differ, but the host does not require the same: told as before
            HandshakeManifest packs = Manifest(packs: new List<ContentEntry> { Pack("75426", "Bus Stops") });
            CompatibilityResult told = CompatibilityCheck.Compare(Manifest(), packs, Options(allow: true, joinAnyway: true));
            Assert.Null(told.Refusal);
            Assert.False(told.Notice.JoinedAnyway);
            // mods differ, not required: told as before
            var relaxed = new CompatibilityOptions { RequireSameMods = false, AllowOverride = true, JoinAnyway = true };
            CompatibilityResult mods = CompatibilityCheck.Compare(Manifest(), Manifest(mods: new List<ModEntry>()), relaxed);
            Assert.False(mods.Notice.JoinedAnyway);
            Assert.Equal("Traffic", mods.Notice.Missing[0].Name);
        }

        [Test]
        public void TheOverridableMarkTravelsInTheReportAndOlderReadersKeepTheLists()
        {
            var r = new MismatchReport { Kind = "mods", Overridable = true };
            r.Missing.Add(new ModEntry { Name = "Traffic", Version = "1.0", PlatformId = "80095" });
            byte[] encoded = MismatchReport.Encode(r);
            MismatchReport back = MismatchReport.Decode(encoded);
            Assert.True(back.Overridable);
            Assert.False(back.JoinedAnyway);
            Assert.Equal("Traffic", back.Missing[0].Name);
            // the mark is a record of its own: without it (what a reader that does not know it sees) the lists are the same
            string text = System.Text.Encoding.UTF8.GetString(encoded);
            Assert.Contains("\noverridable\n", text);
            MismatchReport old = MismatchReport.Decode(System.Text.Encoding.UTF8.GetBytes(text.Replace("overridable\n", "")));
            Assert.False(old.Overridable);
            Assert.Equal("Traffic", old.Missing[0].Name);
            // the refusal around it keeps its layout: the report is the opaque byte field of RefuseMessage
            var w = new SkylineTogether.Core.Buffers.ByteWriter();
            SkylineTogether.Core.Protocol.RefuseMessage.Write(w, new SkylineTogether.Core.Protocol.RefuseMessage { Reason = SkylineTogether.Core.Protocol.RefuseReason.Mismatch, Detail = "x", MismatchReport = encoded });
            byte[] wire = w.ToArray();
            SkylineTogether.Core.Protocol.RefuseMessage read = SkylineTogether.Core.Protocol.RefuseMessage.Decode(wire, 0, wire.Length);
            Assert.True(MismatchReport.Decode(read.MismatchReport).Overridable);
        }
    }
}
