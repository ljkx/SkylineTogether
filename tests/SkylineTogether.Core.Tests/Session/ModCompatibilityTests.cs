using System;
using System.Collections.Generic;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>
    /// Mod compatibility (docs/design/MOD-COMPATIBILITY.md): which mods must match between host and joiner, and the
    /// readable refusal text.
    /// </summary>
    public sealed class ModCompatibilityTests
    {
        private static ModEntry M(string name, string version = "1.0", string id = null, bool registers = false, bool localHint = false)
        {
            return new ModEntry { Name = name, Version = version, PlatformId = id, RegistersTypesOrSystemsOrPrefabs = registers, ClientLocalHint = localHint };
        }

        [Test]
        public void TheUsersClientLocalModsAreKnownByNameAndId()
        {
            foreach (string name in new[] { "I18NEverywhere", "ExtendedTooltip", "AssetIconLibrary", "UnifiedIconLibrary", "ToggleableOverlays", "EmploymentTracker", "CimCensus", "FindIt", "Skyve Mod" })
            {
                Assert.Equal(ModClass.LocalOnly, ModCompatibility.Classify(M(name)), name);
            }
            foreach (string id in new[] { "75426", "78188", "79634", "74417", "80931", "76662", "85825", "75804", "77240" })
            {
                Assert.Equal(ModClass.LocalOnly, ModCompatibility.Classify(M("renamed", id: id)), id);
            }
            // known local beats a peer's scan (Employment Tracker declares an unused serializable component)
            Assert.Equal(ModClass.LocalOnly, ModCompatibility.Classify(M("EmploymentTracker", registers: true)));
        }

        [Test]
        public void TheUsersWorldModsAreNeverRelaxed()
        {
            foreach (string name in new[] { "Traffic", "MoveIt", "Anarchy", "BetterBulldozer", "FiveTwentyNineTiles", "HistoricalStart", "Water_Features", "ZoningToolkit", "LineTool", "PlopTheGrowables", "Tree_Controller" })
            {
                Assert.Equal(ModClass.WorldAffecting, ModCompatibility.Classify(M(name, localHint: true)), name);
            }
            Assert.Equal(ModClass.WorldAffecting, ModCompatibility.Classify(M("x", id: "80095", localHint: true)));
        }

        [Test]
        public void UnknownModsFollowTheScanHintAndFailClosed()
        {
            Assert.Equal(ModClass.WorldAffecting, ModCompatibility.Classify(M("Unknown")));
            Assert.Equal(ModClass.LocalOnly, ModCompatibility.Classify(M("Unknown", localHint: true)));
            Assert.Equal(ModClass.WorldAffecting, ModCompatibility.Classify(M("Unknown", registers: true, localHint: true)));
            Assert.Equal(ModClass.LocalOnly, ModCompatibility.Classify(new ModEntry { Name = "ui", IsUiModule = true }));
            Assert.Equal(ModClass.WorldAffecting, ModCompatibility.Classify(null));
        }

        [Test]
        public void ClientLocalModsOnOneSideDoNotBlock()
        {
            var host = new List<ModEntry> { M("Traffic", "2.0", "80095"), M("I18NEverywhere", "1.0", "75426"), M("CimCensus") };
            var client = new List<ModEntry> { M("Traffic", "2.0", "80095"), M("ExtendedTooltip", "3"), M("CimCensus", "9.9") };
            ModComparison c = ModCompatibility.Compare(host, client);
            Assert.False(c.Blocks);
            Assert.Null(c.RefusalText());
            Assert.Null(SessionPayloads.CompareMods(host, client));
            Assert.Equal(3, c.IgnoredLocal.Count);
        }

        [Test]
        public void WorldModDifferencesBlockWithAReadableList()
        {
            var host = new List<ModEntry> { M("Traffic", "2.0"), M("Anarchy", "1.5"), M("FindIt") };
            var client = new List<ModEntry> { M("Anarchy", "1.6"), M("MoveIt", "3.0"), M("ToggleableOverlays") };
            string text = SessionPayloads.CompareMods(host, client);
            Assert.NotNull(text);
            Assert.Contains("Missing (enable these): Traffic 2.0", text);
            Assert.Contains("Not on host (disable these): MoveIt 3.0", text);
            Assert.Contains("Different version: Anarchy (host 1.5, you 1.6)", text);
            Assert.DoesNotContain("FindIt", text);
            Assert.DoesNotContain("ToggleableOverlays", text);
        }

        [Test]
        public void EitherSidesScanCanFlagAModAsWorldAffecting()
        {
            // the client's copy says local, the host's copy registers components: blocks on a version difference
            var host = new List<ModEntry> { M("Newer", "2", registers: true) };
            var client = new List<ModEntry> { M("Newer", "1", localHint: true) };
            Assert.True(ModCompatibility.Compare(host, client).Blocks);
            // a mod only the client has, and its own scan says local: fine
            Assert.False(ModCompatibility.Compare(new List<ModEntry>(), new List<ModEntry> { M("Panel", localHint: true) }).Blocks);
            // unknown without a hint: blocks (fail closed)
            Assert.True(ModCompatibility.Compare(new List<ModEntry>(), new List<ModEntry> { M("Panel") }).Blocks);
        }

        [Test]
        public void EntriesAreMatchedByPlatformIdBeforeName()
        {
            var host = new List<ModEntry> { M("Traffic", "2.0", "80095") };
            var client = new List<ModEntry> { M("Traffic (local build)", "2.0", "80095") };
            Assert.False(ModCompatibility.Compare(host, client).Blocks);
            Assert.True(ModCompatibility.Compare(null, new List<ModEntry> { M("Traffic") }).Blocks);
        }

        [Test]
        public void SettingValuesRoundTripInvariantText()
        {
            Assert.Equal("true", SettingValueText.Format(true));
            Assert.Equal("0.1", SettingValueText.Format(0.1f));
            Assert.Equal("-3", SettingValueText.Format(-3));
            Assert.Equal("Friday", SettingValueText.Format(DayOfWeek.Friday));
            Assert.Equal("", SettingValueText.Format(null));
            foreach (float f in new[] { 0f, 0.1f, 1.0000001f, -12345.678f, float.Epsilon })
            {
                Assert.True(SettingValueText.TryParse(SettingValueText.Format(f), typeof(float), out object v));
                Assert.Equal(f, (float)v);
            }
            Assert.True(SettingValueText.TryParse("FALSE", typeof(bool), out object b));
            Assert.Equal(false, (bool)b);
            Assert.True(SettingValueText.TryParse("Monday", typeof(DayOfWeek), out object e));
            Assert.Equal(DayOfWeek.Monday, (DayOfWeek)e);
            Assert.True(SettingValueText.TryParse("12", typeof(long), out object l));
            Assert.Equal(12L, (long)l);
        }

        [Test]
        public void MalformedSettingValuesAreRefused()
        {
            Assert.False(SettingValueText.TryParse("yes", typeof(bool), out _));
            Assert.False(SettingValueText.TryParse("1,5", typeof(int), out _));
            Assert.False(SettingValueText.TryParse("NaN", typeof(float), out _));
            Assert.False(SettingValueText.TryParse("3", typeof(DayOfWeek), out _), "enums by name only");
            Assert.False(SettingValueText.TryParse("Someday", typeof(DayOfWeek), out _));
            Assert.False(SettingValueText.TryParse("1", typeof(Guid), out _), "unsupported type");
            Assert.False(SettingValueText.TryParse(null, typeof(string), out _));
            Assert.False(SettingValueText.IsSupported(typeof(object)));
        }

        [Test]
        public void TheHostSendsOnlyChangedSettings()
        {
            var t = new HostSettingsTracker();
            var current = new List<SettingEntry>
            {
                new SettingEntry { Mod = "Anarchy", Key = "MinimumClearanceBelowElevatedNetworks", Value = "7.5" },
                new SettingEntry { Mod = "Tree_Controller", Key = "DisableTreeGrowth", Value = "false" },
            };
            Assert.Equal(2, t.Diff(current, false).Count);
            Assert.Equal(0, t.Diff(current, false).Count);
            current[1].Value = "true";
            List<SettingEntry> changed = t.Diff(current, false);
            Assert.Equal(1, changed.Count);
            Assert.Equal("DisableTreeGrowth", changed[0].Key);
            Assert.Equal(2, t.Diff(current, true).Count, "a joiner gets everything");
            t.Reset();
            Assert.Equal(2, t.Diff(current, false).Count);
        }

        [Test]
        public void TheClientRemembersItsOwnValuesOnce()
        {
            var t = new ClientSettingsTracker();
            Assert.True(t.RememberOriginal("PlopTheGrowables", "LockPloppedBuildings", "false"));
            Assert.False(t.RememberOriginal("PlopTheGrowables", "LockPloppedBuildings", "true"), "the second host value must not replace the player's own");
            Assert.True(t.IsOverridden("PlopTheGrowables", "LockPloppedBuildings"));
            Assert.False(t.IsOverridden("PlopTheGrowables", "SpawnedZoneDespawn"));
            List<SettingEntry> originals = t.Originals();
            Assert.Equal(1, originals.Count);
            Assert.Equal("false", originals[0].Value);
            t.Clear();
            Assert.Equal(0, t.Count);
        }
    }
}
