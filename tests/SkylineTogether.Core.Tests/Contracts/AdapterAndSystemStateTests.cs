using System.Collections.Generic;
using System.Text;
using SkylineTogether.Core.Hashing;
using SkylineTogether.Core.Json;
using SkylineTogether.Core.Tests.Framework;
using static SkylineTogether.Core.Tests.Contracts.DataFileSamples;

namespace SkylineTogether.Core.Tests.Contracts
{
    /// <summary>adapters.json (ARCH §10.3) and sysstate.json (ARCH §4.10.1).</summary>
    public sealed class AdapterAndSystemStateTests
    {
        private static ConfigValidationException InvalidAdapters(string singleQuotedAdapters)
        {
            return Assert.Throws<ConfigValidationException>(() => AdapterModel.ParseList(J("[ " + singleQuotedAdapters + " ]")),
                                                            "expected adapters.json to be rejected: " + singleQuotedAdapters);
        }

        private static ConfigValidationException InvalidEntries(string singleQuotedEntries)
        {
            return Assert.Throws<ConfigValidationException>(() => SystemStateEntryModel.ParseList(J("[ " + singleQuotedEntries + " ]")),
                                                            "expected sysstate.json to be rejected: " + singleQuotedEntries);
        }

        [Test]
        public void ParsesTheAdapterSample()
        {
            IReadOnlyList<AdapterModel> adapters = AdapterModel.ParseList(J(Adapters));
            Assert.Equal(3, adapters.Count);

            AdapterModel anarchy = adapters[0];
            Assert.Equal("Anarchy", anarchy.Mod);
            Assert.Equal("Anarchy", anarchy.Assembly);
            Assert.Equal("*", anarchy.Versions);
            Assert.Equal(ModClass.WorldAffecting, anarchy.Class);
            Assert.SequenceEqual(new[] { "Anarchy.Systems.OverridePrevention.PreventOverrideSystem", "Anarchy.Systems.ResetTransformSystem" }, anarchy.ClientOff);
            Assert.SequenceEqual(new[] { "Anarchy.Systems.OverridePrevention.AnarchyPlopSystem" }, anarchy.ClientKeep);
            Assert.SequenceEqual(new[] { "Anarchy.Components.PreventOverride", "Anarchy.Components.TransformRecord" }, anarchy.PlayerMutableTypes);
            Assert.SequenceEqual(new[] { "Anarchy.Tools.AnarchyComponentsToolSystem" }, anarchy.DirectWriteTools);
            Assert.SequenceEqual(new[] { "AllowPlacingMultipleUniqueBuildings", "MinimumClearanceBelowElevatedNetworks" }, anarchy.SimSettings);
            Assert.True(anarchy.PushSettings);
            Assert.Equal(0, anarchy.Cosmetic.Count + anarchy.JoinDisable.Count + anarchy.LocalTypes.Count + anarchy.HostOnlyActions.Count
                            + anarchy.ResendAfterJoinTypes.Count + anarchy.SystemStateSync.Count + anarchy.ToolScoped.Count + anarchy.LocalIconPrefabs.Count);
            Assert.Contains("TransformRecord", anarchy.Notes);

            AdapterModel water = adapters[1];
            Assert.Equal("Water Features", water.Mod);
            Assert.Equal("1.*", water.Versions);
            Assert.SequenceEqual(new[] { "Cleanup cycle" }, water.HostOnlyActions);
            Assert.Equal(1, water.SystemStateSync.Count);
            Assert.Equal("WaterFeatures.Parameters", water.SystemStateSync[0].Id);
            Assert.Equal(SystemStateCodecKind.Custom, water.SystemStateSync[0].Codec);
            Assert.Equal(0.5, water.SystemStateSync[0].MaxHz);
            Assert.Equal(1, water.ToolScoped.Count);
            Assert.Equal(1.0, water.ToolScoped[0].LingerSeconds);
            Assert.False(water.PushSettings);
            Assert.Null(water.Notes);

            AdapterModel findIt = adapters[2];
            Assert.Equal(ModClass.LocalOnly, findIt.Class);
            Assert.SequenceEqual(new[] { "FindIt Marker" }, findIt.LocalIconPrefabs);
            Assert.Equal(0, findIt.ClientOff.Count, "absent lists are empty, never null");
            Assert.NotNull(findIt.SystemStateSync);
            Assert.NotNull(findIt.ToolScoped);
        }

        [Test]
        public void AdapterListMayBeABareArrayOrEmpty()
        {
            Assert.Equal(1, AdapterModel.ParseList(J("[ { 'mod': 'X', 'assembly': 'X' } ]")).Count);
            Assert.Equal(0, AdapterModel.ParseList(J("{ 'adapters': [] }")).Count);
            Assert.Contains("required member \"adapters\"", Assert.Throws<ConfigValidationException>(() => AdapterModel.ParseList(J("{ 'version': 1 }"))).Message);
        }

        [Test]
        public void AdaptersAreValidated()
        {
            Assert.Contains("required member \"mod\"", InvalidAdapters("{ 'assembly': 'X' }").Message);
            Assert.Contains("required member \"assembly\"", InvalidAdapters("{ 'mod': 'X' }").Message);
            Assert.Contains("unknown ModClass", InvalidAdapters("{ 'mod': 'X', 'assembly': 'X', 'class': 'Local' }").Message);
            Assert.Contains("unknown member \"clientoff\"", InvalidAdapters("{ 'mod': 'X', 'assembly': 'X', 'clientoff': [] }").Message);
            Assert.Contains("duplicate entry", InvalidAdapters("{ 'mod': 'X', 'assembly': 'X', 'localTypes': [ 'A.B', 'A.B' ] }").Message);
            Assert.Contains("in both \"clientOff\" and \"clientKeep\"",
                            InvalidAdapters("{ 'mod': 'X', 'assembly': 'X', 'clientOff': [ 'A.B' ], 'clientKeep': [ 'A.B' ] }").Message);
            Assert.Contains("in both \"clientOff\" and \"cosmetic\"",
                            InvalidAdapters("{ 'mod': 'X', 'assembly': 'X', 'clientOff': [ 'A.B' ], 'cosmetic': [ 'A.B' ] }").Message);
            Assert.Contains("never be cosmetic", InvalidAdapters("{ 'mod': 'X', 'assembly': 'X', 'cosmetic': [ 'Game.Simulation.StreetLightSystem' ] }").Message);
            Assert.Contains("listed twice", InvalidAdapters("{ 'mod': 'X', 'assembly': 'X' }, { 'mod': 'X', 'assembly': 'X2' }").Message);
            Assert.Contains("listed twice",
                            InvalidAdapters("{ 'mod': 'X', 'assembly': 'X', 'toolScoped': [ { 'tool': 'T.T', 'systems': [ 'A.B' ] }, { 'tool': 'T.T', 'systems': [ 'C.D' ] } ] }").Message);
            Assert.Contains("leading or trailing whitespace", InvalidAdapters("{ 'mod': ' X', 'assembly': 'X' }").Message);
            Assert.Contains("true or false", InvalidAdapters("{ 'mod': 'X', 'assembly': 'X', 'pushSettings': 'yes' }").Message);
            IReadOnlyList<AdapterModel> versions = AdapterModel.ParseList(J("[ { 'mod': 'X', 'assembly': 'X', 'versions': '1.*' }, { 'mod': 'X', 'assembly': 'X', 'versions': '2.*' } ]"));
            Assert.Equal(2, versions.Count);
        }

        [Test]
        public void SystemStateStableIdsAreUniqueAcrossAdapters()
        {
            ConfigValidationException e = InvalidAdapters(
                "{ 'mod': 'A', 'assembly': 'A', 'systemStateSync': [ { 'id': 'Shared', 'system': 'A.S' } ] }, " +
                "{ 'mod': 'B', 'assembly': 'B', 'systemStateSync': [ { 'id': 'Shared', 'system': 'B.S' } ] }");
            Assert.Contains("\"Shared\" is listed twice", e.Message);
            Assert.Equal("$[1].systemStateSync[0].id", e.Path);
        }

        [Test]
        public void ParsesTheSystemStateSample()
        {
            IReadOnlyList<SystemStateEntryModel> entries = SystemStateEntryModel.ParseList(J(SystemState));
            Assert.Equal(4, entries.Count);
            SystemStateEntryModel tax = entries[0];
            Assert.Equal("TaxSystem", tax.Id);
            Assert.Equal("Game.Simulation.TaxSystem", tax.SystemTypeName);
            Assert.Equal(SystemStateCodecKind.GameSerializable, tax.Codec);
            Assert.Equal(1.0, tax.MaxHz);
            Assert.True(tax.RequiresRoundTrip);
            Assert.Equal("Readers", tax.ReaderHandleMember);
            Assert.Equal(1.0, entries[1].MaxHz, "default rate");
            Assert.Null(entries[1].ReaderHandleMember);
            Assert.Equal(SystemStateCodecKind.DeserializeOnly, entries[2].Codec);
            Assert.Equal(0.25, entries[2].MaxHz);
            Assert.False(entries[3].RequiresRoundTrip);
            Assert.Equal(SystemStateCodecKind.Custom, entries[3].Codec);
        }

        [Test]
        public void StableIdIsTheFoldedXxHash64OfTheUtf8Id()
        {
            byte[] utf8 = Encoding.UTF8.GetBytes("TaxSystem");
            uint expected = HashMix.Fold32(XxHash64.Hash(utf8, 0, utf8.Length));
            var entry = new SystemStateEntryModel { Id = "TaxSystem" };
            Assert.Equal(expected, entry.StableId);
            // pinned: the wire identity of an entry must never change between builds
            Assert.Equal(0xBF9B198Au, entry.StableId);
            Assert.Equal(0x84271865u, SystemStateEntryModel.ComputeStableId("ResidentialDemand"));
            Assert.Equal(0x46AE653Au, SystemStateEntryModel.ComputeStableId("CityStatistics"));
            Assert.Equal(0u, new SystemStateEntryModel().StableId);
            Assert.NotEqual(SystemStateEntryModel.ComputeStableId("TaxSystem"), SystemStateEntryModel.ComputeStableId("taxsystem"));
        }

        [Test]
        public void SystemStateRejectsDuplicateIdsAndCollisions()
        {
            Assert.Contains("\"TaxSystem\" is listed twice",
                            InvalidEntries("{ 'id': 'TaxSystem', 'system': 'A.B' }, { 'id': 'TaxSystem', 'system': 'C.D' }").Message);

            // a 32-bit collision between two different ids is reported with both names
            var r = new ConfigReader(SystemStateEntryModel.FileName);
            var known = new Dictionary<uint, string> { { SystemStateEntryModel.ComputeStableId("TaxSystem"), "OtherEntry" } };
            JsonValue array = J("[ { 'id': 'TaxSystem', 'system': 'A.B' } ]");
            ConfigValidationException collision = Assert.Throws<ConfigValidationException>(() => SystemStateEntryModel.ParseEntries(r, array, "$", known));
            Assert.Contains("\"OtherEntry\" and \"TaxSystem\" have the same stable id", collision.Message);
        }

        [Test]
        public void SystemStateEntriesAreValidated()
        {
            Assert.Contains("required member \"id\"", InvalidEntries("{ 'system': 'A.B' }").Message);
            Assert.Contains("required member \"system\"", InvalidEntries("{ 'id': 'X' }").Message);
            Assert.Contains("identifier", InvalidEntries("{ 'id': 'Tax System', 'system': 'A.B' }").Message);
            Assert.Contains("outside", InvalidEntries("{ 'id': 'X', 'system': 'A.B', 'maxHz': 0 }").Message);
            Assert.Contains("outside", InvalidEntries("{ 'id': 'X', 'system': 'A.B', 'maxHz': 61 }").Message);
            Assert.Contains("unknown SystemStateCodecKind", InvalidEntries("{ 'id': 'X', 'system': 'A.B', 'codec': 'Json' }").Message);
            Assert.Contains("identifier", InvalidEntries("{ 'id': 'X', 'system': 'A.B', 'readerHandleMember': 'm Readers' }").Message);
            Assert.Contains("unknown member", InvalidEntries("{ 'id': 'X', 'system': 'A.B', 'rate': 1 }").Message);
            Assert.Contains("expected a string", InvalidEntries("{ 'id': 5, 'system': 'A.B' }").Message);
            Assert.Contains("required member \"entries\"",
                            Assert.Throws<ConfigValidationException>(() => SystemStateEntryModel.ParseList(J("{ 'version': 1 }"))).Message);
            Assert.Equal(1, SystemStateEntryModel.ParseList(J("[ { 'id': 'Demand:Residential', 'system': 'A.B' } ]")).Count);
        }
    }
}
