using System;
using System.Collections.Generic;
using SkylineTogether.Core.Modding;
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
            foreach (string name in new[] { "I18NEverywhere", "ExtendedTooltip", "AssetIconLibrary", "UnifiedIconLibrary", "ToggleableOverlays", "EmploymentTracker", "CimCensus", "FindIt", "Skyve Mod", "ExtraLib", "AchievementFixer" })
            {
                Assert.Equal(ModClass.LocalOnly, ModCompatibility.Classify(M(name)), name);
            }
            foreach (string id in new[] { "75426", "78188", "79634", "74417", "80931", "76662", "85825", "75804", "77240", "75724", "121256" })
            {
                Assert.Equal(ModClass.LocalOnly, ModCompatibility.Classify(M("renamed", id: id)), id);
            }
            // known local beats a peer's scan for a mod without a check against one version (I18N Everywhere patches
            // localization code, Find It creates prefabs)
            Assert.Equal(ModClass.LocalOnly, ModCompatibility.Classify(M("I18NEverywhere", registers: true)));
            Assert.Equal(ModClass.LocalOnly, ModCompatibility.Classify(M("FindIt", registers: true)));
        }

        // ------------------------------------------------------------------------------------------ checked versions

        private static ModFacts Scan(string assembly, params SystemFact[] systems)
        {
            var facts = new ModFacts { AssemblyName = assembly, PhasesKnown = true, PatchedMethods = new List<string>() };
            facts.Systems.AddRange(systems);
            return facts;
        }

        private static SystemFact Sys(string name, params string[] phases)
        {
            var s = new SystemFact { Name = name };
            s.Phases.AddRange(phases);
            return s;
        }

        // the scans of the checked versions (ExtraLib 1.4.11, Achievement Fixer 1.3.1, Cim Census 1.8, Cim Route
        // Highlighter 1.26) as their code and the host's analysis log show them
        private static ModFacts ExtraLib()
        {
            ModFacts f = Scan("ExtraLib", Sys("ExtraLib.Systems.MainSystem", "LateUpdate"), Sys("ExtraLib.Systems.UI.AssetMultiCategory", "UIUpdate"),
                Sys("ExtraLib.Systems.UI.ExtraPanels.ExtraPanelsUISystem", "UIUpdate"));
            f.ReferencesHarmony = true;
            f.PatchedMethods.Add("Game.UI.InGame.ToolbarUISystem.SelectAssetMenu");
            f.PatchedMethods.Add("Game.UI.InGame.ToolbarUISystem.SelectAssetCategory");
            f.PrefabTypes.Add("ExtraLib.Prefabs.UIAssetParentCategoryPrefab");
            f.PrefabTypes.Add("ExtraLib.Prefabs.UIAssetChildCategoryPrefab");
            f.RuntimePrefabCreators.Add("ExtraLib.Systems.MainSystem.CreateDummyPrefab");
            f.RuntimePrefabCreators.Add("ExtraLib.Helpers.PrefabsHelper.GetOrCreateUIAssetChildCategoryPrefab");
            f.RuntimePrefabCreators.Add("ExtraLib.Helpers.PrefabsHelper.LoadPrefabInDirectory");
            f.Components.Add(new ComponentFact { Name = "ExtraLib.Prefabs.UIAssetChildCategoryData" });
            return f;
        }

        private static ModFacts EmploymentTracker()
        {
            ModFacts f = Scan("EmploymentTracker", Sys("EmploymentTracker.HighlightEmployeesSystem", "MainLoop"), Sys("EmploymentTracker.HighlightRoutesSystem", "Rendering"),
                Sys("EmploymentTracker.SimpleOverlayRendererSystem", "Rendering"));
            f.Systems[0].WritesEntities = true;
            f.Components.Add(new ComponentFact { Name = "EmploymentTracker.DeleteTimer", Serializable = true });
            return f;
        }

        [Test]
        public void TheCheckedVersionsOfClientLocalModsMatchTheirChecks()
        {
            foreach (ModFacts f in new[]
            {
                ExtraLib(), EmploymentTracker(),
                Scan("AchievementFixer", Sys("AchievementFixer.AchievementFixerSystem", "MainLoop")),
                Scan("CimCensus", Sys("CimCensus.StatisticsCalculationSystem", "GameSimulation")),
            })
            {
                Assert.True(ModClassifier.Classify(f).WorldAffecting, f.AssemblyName + ": the analysis alone would block a difference");
                List<string> beyond = ModCompatibility.BeyondVerifiedLocal(f);
                Assert.NotNull(beyond, f.AssemblyName);
                Assert.Equal(0, beyond.Count, f.AssemblyName + ": " + string.Join(", ", beyond));
                Assert.True(ModCompatibility.HasVerifiedProfile(f.AssemblyName, null), f.AssemblyName);
            }
            // a runtime-prefab entry covers the methods of its type and of the types nested in it, nothing else
            ModFacts nested = ExtraLib();
            nested.RuntimePrefabCreators.Add("ExtraLib.Helpers.PrefabsHelper+<>c.<Load>b__0");
            nested.RuntimePrefabCreators.Add("ExtraLib.Systems.MainSystem..ctor");
            Assert.Equal(0, ModCompatibility.BeyondVerifiedLocal(nested).Count);
            nested.RuntimePrefabCreators.Add("ExtraLib.Helpers.PrefabsHelperExtra.Create");
            Assert.SequenceEqual(new[] { "runtime prefabs in ExtraLib.Helpers.PrefabsHelperExtra.Create" }, ModCompatibility.BeyondVerifiedLocal(nested));
            // mods that were not checked against one version have no profile
            Assert.Null(ModCompatibility.BeyondVerifiedLocal(Scan("ToggleableOverlays", Sys("ToggleableOverlays.OverlaySystem", "MainLoop"))));
            Assert.Null(ModCompatibility.BeyondVerifiedLocal(null));
            Assert.True(ModCompatibility.HasVerifiedProfile("renamed", "85825"));
            Assert.False(ModCompatibility.HasVerifiedProfile("I18NEverywhere", "75426"));
        }

        [Test]
        public void ANewerCopyThatDoesMoreIsNotTheCheckedVersion()
        {
            ModFacts saved = Scan("CimCensus", Sys("CimCensus.StatisticsCalculationSystem", "GameSimulation"));
            saved.Components.Add(new ComponentFact { Name = "CimCensus.History", Serializable = true });
            Assert.SequenceEqual(new[] { "saved component CimCensus.History" }, ModCompatibility.BeyondVerifiedLocal(saved));

            ModFacts phase = Scan("AchievementFixer", Sys("AchievementFixer.AchievementFixerSystem", "MainLoop", "ModificationEnd"));
            Assert.SequenceEqual(new[] { "system AchievementFixer.AchievementFixerSystem in ModificationEnd" }, ModCompatibility.BeyondVerifiedLocal(phase));

            ModFacts tool = EmploymentTracker();
            tool.Systems.Add(new SystemFact { Name = "EmploymentTracker.RouteTool", IsTool = true });
            tool.Systems.Add(new SystemFact { Name = "EmploymentTracker.SavedSystem", SavesState = true });
            Assert.SequenceEqual(new[] { "tool EmploymentTracker.RouteTool", "saved system EmploymentTracker.SavedSystem" }, ModCompatibility.BeyondVerifiedLocal(tool));

            ModFacts patch = ExtraLib();
            patch.PatchedMethods.Add("Game.Simulation.SimulationSystem.OnUpdate");
            patch.PrefabTypes.Add("ExtraLib.Prefabs.NewPlaceablePrefab");
            Assert.SequenceEqual(new[] { "prefab type ExtraLib.Prefabs.NewPlaceablePrefab", "patch Game.Simulation.SimulationSystem.OnUpdate" }, ModCompatibility.BeyondVerifiedLocal(patch));

            ModFacts writingUi = ExtraLib();
            writingUi.Systems[1].WritesEntities = true;
            Assert.SequenceEqual(new[] { "system ExtraLib.Systems.UI.AssetMultiCategory in UIUpdate" }, ModCompatibility.BeyondVerifiedLocal(writingUi));

            // what cannot be checked is not the checked version (fail closed)
            ModFacts unknownPhases = Scan("CimCensus", Sys("CimCensus.StatisticsCalculationSystem", "GameSimulation"));
            unknownPhases.PhasesKnown = false;
            Assert.SequenceEqual(new[] { "system CimCensus.StatisticsCalculationSystem in an unknown phase" }, ModCompatibility.BeyondVerifiedLocal(unknownPhases));
            ModFacts unreadable = ExtraLib();
            unreadable.PatchedMethods = null;
            Assert.SequenceEqual(new[] { "unreadable Harmony patches" }, ModCompatibility.BeyondVerifiedLocal(unreadable));
            Assert.SequenceEqual(new[] { "a failed scan" }, ModCompatibility.BeyondVerifiedLocal(new ModFacts { AssemblyName = "ExtraLib", ScanFailed = true }));
        }

        [Test]
        public void ACheckedModIsLocalOnlyWhileEveryCopyIsTheCheckedVersion()
        {
            // the peers' copies match the checked version: local, whichever side has it and in whatever version
            Assert.Equal(ModClass.LocalOnly, ModCompatibility.Classify(M("CimCensus", "1.0", localHint: true)));
            var host = new List<ModEntry> { M("ExtraLib", "1.0", localHint: true), M("AchievementFixer", "1.3.1", localHint: true), M("CimCensus", "1.0", localHint: true), M("EmploymentTracker", "1.0", localHint: true) };
            Assert.False(ModCompatibility.Compare(host, new List<ModEntry>()).Blocks, "only the host has them");
            Assert.False(ModCompatibility.Compare(new List<ModEntry>(), host).Blocks, "only the joiner has them");
            Assert.Equal(4, ModCompatibility.Compare(host, new List<ModEntry>()).IgnoredLocal.Count);
            // a copy with more than the checked version: its analysis decides, on either side
            Assert.Equal(ModClass.WorldAffecting, ModCompatibility.Classify(M("CimCensus", "2.0", registers: true)));
            Assert.Equal(ModClass.WorldAffecting, ModCompatibility.Classify(M("renamed", "2.0", id: "121256", registers: true)));
            ModComparison c = ModCompatibility.Compare(new List<ModEntry> { M("ExtraLib", "1.0", localHint: true) }, new List<ModEntry> { M("ExtraLib", "2.0", registers: true) });
            Assert.True(c.Blocks);
            Assert.Equal(1, c.Version.Count);
            Assert.True(ModCompatibility.Compare(new List<ModEntry> { M("EmploymentTracker", "2.0", registers: true) }, new List<ModEntry>()).Blocks);
            Assert.False(ModCompatibility.IsLocalPair(M("ExtraLib", registers: true), M("ExtraLib", localHint: true)));
        }

        [Test]
        public void WorldItemsFollowTheScopeDecision()
        {
            ModFacts ui = Scan("Panel", Sys("Panel.PanelSystem", "UIUpdate"), Sys("Panel.Overlay", "Rendering"));
            ui.PatchedMethods.Add("Game.UI.InGame.InfoPanelUISystem.OnUpdate");
            ui.Components.Add(new ComponentFact { Name = "Panel.Hover" });
            ui.Components.Add(new ComponentFact { Name = "Panel.Cache", Storage = ComponentStorage.Shared, Serializable = true });
            Assert.Equal(0, ModClassifier.WorldItems(ui).Count);
            Assert.False(ModClassifier.Classify(ui).WorldAffecting);
            ModFacts edited = Scan("Panel", Sys("Panel.PanelSystem", "UIUpdate"));
            edited.Systems[0].WritesEntities = true;
            Assert.SequenceEqual(new[] { "system Panel.PanelSystem in UIUpdate" }, ModClassifier.WorldItems(edited));
            Assert.True(ModClassifier.Classify(edited).WorldAffecting);
            ModFacts tag = Scan("Tags");
            tag.Components.Add(new ComponentFact { Name = "Tags.Marked", Storage = ComponentStorage.Tag, EmptySerializable = true });
            Assert.SequenceEqual(new[] { "saved component Tags.Marked" }, ModClassifier.WorldItems(tag));
            Assert.True(ModClassifier.Classify(tag).WorldAffecting);
            Assert.Equal(0, ModClassifier.WorldItems(Scan("Editor", Sys("Editor.Brush", "EditorSimulation"))).Count);
            Assert.SequenceEqual(new[] { "a failed scan" }, ModClassifier.WorldItems(null));
        }

        [Test]
        public void TheUsersWorldModsAreNeverRelaxed()
        {
            foreach (string name in new[] { "Traffic", "MoveIt", "Anarchy", "BetterBulldozer", "FiveTwentyNineTiles", "HistoricalStart", "Water_Features", "ZoningToolkit", "LineTool", "PlopTheGrowables", "Tree_Controller", "Placemaker" })
            {
                Assert.Equal(ModClass.WorldAffecting, ModCompatibility.Classify(M(name, localHint: true)), name);
            }
            Assert.Equal(ModClass.WorldAffecting, ModCompatibility.Classify(M("x", id: "80095", localHint: true)));
            Assert.Equal(ModClass.WorldAffecting, ModCompatibility.Classify(M("x", id: "163638", localHint: true)), "Placemaker by id");
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
        public void TheStructuredRefusalLeavesClientLocalModsOut()
        {
            var host = new List<ModEntry> { M("Traffic", "2.0", "80095"), M("I18NEverywhere", "1.0", "75426"), M("Anarchy", "1.5") };
            var client = new List<ModEntry> { M("ExtendedTooltip", "3"), M("Anarchy", "1.6"), M("CimCensus") };
            MismatchReport report = MismatchReport.CompareMods(host, client);
            Assert.NotNull(report);
            Assert.Equal(1, report.Missing.Count);
            Assert.Equal("Traffic", report.Missing[0].Name);
            Assert.Equal(0, report.Extra.Count);
            Assert.Equal(1, report.Versions.Count);
            Assert.Equal("Anarchy", report.Versions[0].Name);
            // only client-local differences: no report at all, the join goes ahead
            Assert.Null(MismatchReport.CompareMods(new List<ModEntry> { M("CimCensus", "1") }, new List<ModEntry> { M("FindIt"), M("CimCensus", "2") }));
            // the encoded report survives the round trip without the local mods
            MismatchReport decoded = MismatchReport.Decode(MismatchReport.Encode(report));
            Assert.Equal(1, decoded.Missing.Count);
            Assert.DoesNotContain("I18NEverywhere", report.Describe());
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
