using System.Collections.Generic;
using System.Linq;
using SkylineTogether.Core.Modding;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Modding
{
    /// <summary>
    /// The automatic classification of unknown mods (docs/design/MOD-COMPATIBILITY.md §6): scope for the join check,
    /// component treatments, client gating of systems, tool capture, and that every decision is explained.
    /// </summary>
    public sealed class ModClassifierTests
    {
        private static ModFacts Facts(string name = "SomeMod") => new ModFacts { AssemblyName = name, PhasesKnown = true };

        private static SystemFact System(string name, params string[] phases)
        {
            var s = new SystemFact { Name = name };
            s.Phases.AddRange(phases);
            return s;
        }

        private static ComponentFact Component(string name, ComponentStorage storage = ComponentStorage.Data, bool serializable = true)
        {
            return new ComponentFact { Name = name, Storage = storage, Serializable = serializable };
        }

        private static ModDeclarations Declare(params string[] lines)
        {
            var d = new ModDeclarations();
            for (int i = 0; i < lines.Length; i++) d.AddLine(lines[i], "Declarations[" + i + "]");
            return d;
        }

        private static ComponentTreatment TreatmentOf(ModReport r, string component) => r.Components.Single(c => c.Name == component).Treatment;

        private static SystemTreatment TreatmentOfSystem(ModReport r, string system) => r.Systems.Single(s => s.Name == system).Treatment;

        [Test]
        public void AModWithOnlyUiSystemsIsClientLocal()
        {
            ModFacts f = Facts("TooltipMod");
            f.Systems.Add(System("TooltipMod.TooltipSystem", "UITooltip"));
            f.Systems.Add(System("TooltipMod.PanelSystem", "UIUpdate", "Rendering"));
            f.Components.Add(Component("TooltipMod.Cache", serializable: false));
            ModReport r = ModClassifier.Classify(f);
            Assert.False(r.WorldAffecting);
            Assert.Equal(SystemTreatment.Presentation, TreatmentOfSystem(r, "TooltipMod.TooltipSystem"));
            Assert.Equal(ComponentTreatment.NotReplicated, TreatmentOf(r, "TooltipMod.Cache"));
            Assert.Contains("UI, rendering or tooltips only", string.Join(" ", r.ScopeReasons));
        }

        [Test]
        public void SavedComponentsReplicateAndMakeTheModWorldAffecting()
        {
            ModFacts f = Facts();
            f.Components.Add(new ComponentFact { Name = "SomeMod.Data", Storage = ComponentStorage.Data, Serializable = true, HasEntityReferences = true, Enableable = true });
            f.Components.Add(new ComponentFact { Name = "SomeMod.Flag", Storage = ComponentStorage.Tag, EmptySerializable = true });
            f.Components.Add(new ComponentFact { Name = "SomeMod.List", Storage = ComponentStorage.Buffer, Serializable = true });
            f.Components.Add(new ComponentFact { Name = "SomeMod.Shared", Storage = ComponentStorage.Shared, Serializable = true });
            ModReport r = ModClassifier.Classify(f);
            Assert.True(r.WorldAffecting);
            Assert.Equal(ComponentTreatment.Replicated, TreatmentOf(r, "SomeMod.Data"));
            Assert.Equal(ComponentTreatment.Presence, TreatmentOf(r, "SomeMod.Flag"));
            Assert.Equal(ComponentTreatment.Replicated, TreatmentOf(r, "SomeMod.List"));
            Assert.Equal(ComponentTreatment.NotReplicated, TreatmentOf(r, "SomeMod.Shared"));
            string note = r.Components.Single(c => c.Name == "SomeMod.Data").Note;
            Assert.Contains("entity references", note);
            Assert.Contains("enabled bit", note);
            Assert.Contains("saves 3 component type(s)", string.Join(" ", r.ScopeReasons));
        }

        [Test]
        public void SimulationSystemsAreGatedLikeVanillaOnes()
        {
            ModFacts f = Facts();
            f.Systems.Add(System("SomeMod.Growth", "GameSimulation"));
            f.Systems.Add(System("SomeMod.AfterStep", "PostSimulation"));
            f.Systems.Add(System("SomeMod.BeforeStep", "PreSimulation"));
            f.Systems.Add(System("SomeMod.Lanes", "Modification4"));
            f.Systems.Add(System("SomeMod.OnDemand"));
            f.Systems.Add(System("SomeMod.EditorOnly", "EditorSimulation"));
            ModReport r = ModClassifier.Classify(f);
            Assert.Equal(SystemTreatment.OffBySimulation, TreatmentOfSystem(r, "SomeMod.Growth"));
            Assert.Equal(SystemTreatment.HostOnly, TreatmentOfSystem(r, "SomeMod.AfterStep"));
            Assert.Equal(SystemTreatment.HostOnly, TreatmentOfSystem(r, "SomeMod.BeforeStep"));
            Assert.Equal(SystemTreatment.Everywhere, TreatmentOfSystem(r, "SomeMod.Lanes"));
            Assert.Equal(SystemTreatment.NotScheduled, TreatmentOfSystem(r, "SomeMod.OnDemand"));
            Assert.Equal(SystemTreatment.Editor, TreatmentOfSystem(r, "SomeMod.EditorOnly"));
            Assert.True(r.WorldAffecting);
            Assert.SequenceEqual(new[] { "SomeMod.AfterStep", "SomeMod.BeforeStep" }, r.SystemsWith(SystemTreatment.HostOnly));
        }

        [Test]
        public void SystemsInWorldPhasesMakeAModWorldAffectingEvenWithoutSavedData()
        {
            ModFacts f = Facts();
            f.Systems.Add(System("SomeMod.Rewriter", "ModificationEnd"));
            ModReport r = ModClassifier.Classify(f);
            Assert.True(r.WorldAffecting);
            Assert.Contains("SomeMod.Rewriter in ModificationEnd", string.Join(" ", r.ScopeReasons));
        }

        [Test]
        public void UnknownPhasesFailClosed()
        {
            ModFacts f = Facts();
            f.PhasesKnown = false;
            f.Systems.Add(System("SomeMod.Anything"));
            ModReport r = ModClassifier.Classify(f);
            Assert.True(r.WorldAffecting);
            Assert.Equal(SystemTreatment.Everywhere, TreatmentOfSystem(r, "SomeMod.Anything"));
            Assert.Contains("update phase is unknown", string.Join(" ", r.ScopeReasons));
        }

        [Test]
        public void HarmonyPatchesCountByTheirTargets()
        {
            ModFacts ui = Facts();
            ui.ReferencesHarmony = true;
            ui.PatchedMethods = new List<string> { "Game.UI.InGame.SelectedInfoUISystem.OnUpdate", "Colossal.Localization.LocalizationManager.SetActiveLocale" };
            Assert.False(ModClassifier.Classify(ui).WorldAffecting, "UI patches only");

            ModFacts sim = Facts();
            sim.ReferencesHarmony = true;
            sim.PatchedMethods = new List<string> { "Game.UI.Foo.Bar", "Game.Simulation.TaxSystem.OnUpdate" };
            ModReport r = ModClassifier.Classify(sim);
            Assert.True(r.WorldAffecting);
            Assert.Contains("Game.Simulation.TaxSystem.OnUpdate", string.Join(" ", r.ScopeReasons));

            ModFacts unreadable = Facts();
            unreadable.ReferencesHarmony = true;
            unreadable.PatchedMethods = null;
            Assert.True(ModClassifier.Classify(unreadable).WorldAffecting, "Harmony without a readable patch list fails closed");

            ModFacts unused = Facts();
            unused.ReferencesHarmony = true;
            unused.PatchedMethods = new List<string>();
            Assert.False(ModClassifier.Classify(unused).WorldAffecting, "Harmony referenced but nothing patched");
        }

        [Test]
        public void PrefabsToolsAndAFailedScanAreWorldAffecting()
        {
            ModFacts prefabs = Facts();
            prefabs.PrefabTypes.Add("SomeMod.RoadPrefab");
            Assert.True(ModClassifier.Classify(prefabs).WorldAffecting);

            ModFacts runtime = Facts();
            runtime.RuntimePrefabCreators.Add("SomeMod.Generator.Create");
            ModReport r = ModClassifier.Classify(runtime);
            Assert.True(r.WorldAffecting);
            Assert.Contains("creates prefabs at runtime", string.Join(" ", r.ScopeReasons));

            ModFacts tool = Facts();
            tool.Systems.Add(new SystemFact { Name = "SomeMod.Brush", IsTool = true });
            Assert.True(ModClassifier.Classify(tool).WorldAffecting);

            ModFacts failed = Facts();
            failed.ScanFailed = true;
            failed.ScanError = "type load";
            r = ModClassifier.Classify(failed);
            Assert.True(r.WorldAffecting);
            Assert.Contains("scan failed", r.ScopeReasons[0]);
            Assert.True(ModClassifier.Classify(null).WorldAffecting);
        }

        [Test]
        public void ToolsAreDirectWriteUnlessDeclaredPipeline()
        {
            ModFacts f = Facts();
            f.Systems.Add(new SystemFact { Name = "SomeMod.Brush", IsTool = true });
            f.Systems.Add(new SystemFact { Name = "SomeMod.Placer", IsTool = true, UsesCreationDefinitions = true });
            f.Systems.Add(new SystemFact { Name = "SomeMod.Liner", IsTool = true, UsesCreationDefinitions = true });
            ModReport r = ModClassifier.Classify(f, Declare("pipeline: SomeMod.Liner"));
            Assert.SequenceEqual(new[] { "SomeMod.Brush", "SomeMod.Placer" }, r.ToolsWith(ToolTreatment.DirectWrite));
            Assert.SequenceEqual(new[] { "SomeMod.Liner" }, r.ToolsWith(ToolTreatment.Pipeline));
            // the creation-definition heuristic only explains, it never changes the (fail-safe) default
            Assert.Contains("probably the Temp pipeline", r.Tools.Single(t => t.Name == "SomeMod.Placer").Note);
            Assert.Equal(0, r.Systems.Count, "tools are not gated as systems");
        }

        [Test]
        public void ComponentDeclarationsApplyAndInvalidOnesAreReported()
        {
            ModFacts f = Facts();
            f.Components.Add(Component("SomeMod.Marker", ComponentStorage.Tag, serializable: false));
            f.Components.Add(Component("SomeMod.Preview"));
            f.Components.Add(Component("SomeMod.Cache", serializable: false));
            f.Components.Add(Component("SomeMod.Data"));
            f.Systems.Add(System("SomeMod.Growth", "GameSimulation"));
            ModReport r = ModClassifier.Classify(f, Declare(
                "tool-local: SomeMod.Marker",
                "never-replicate: SomeMod.Preview",
                "replicate: SomeMod.Cache",
                "replicate: SomeMod.Data",
                "host-only: SomeMod.Data",
                "replicate: SomeMod.Missing"));
            Assert.Equal(ComponentTreatment.ToolLocal, TreatmentOf(r, "SomeMod.Marker"));
            Assert.Equal(ComponentTreatment.Excluded, TreatmentOf(r, "SomeMod.Preview"));
            Assert.Equal(ComponentTreatment.NotReplicated, TreatmentOf(r, "SomeMod.Cache"), "an unsaved type is never forced onto the wire");
            Assert.Equal(ComponentTreatment.Replicated, TreatmentOf(r, "SomeMod.Data"));
            Assert.Equal(3, r.Problems.Count, string.Join(" | ", r.Problems));
            Assert.Contains("SomeMod.Cache cannot replicate", r.Problems[0]);
            Assert.Contains("(it is a component)", r.Problems[1]);
            Assert.Contains("no such type", r.Problems[2]);
            Assert.Contains("Declarations[2]", r.Problems[0]);
        }

        [Test]
        public void SystemDeclarationsOverrideThePhaseRuleAndConflictsCancel()
        {
            ModFacts f = Facts();
            f.Systems.Add(System("SomeMod.Water", "GameSimulation"));
            f.Systems.Add(System("SomeMod.Zones", "ModificationEnd"));
            f.Systems.Add(System("SomeMod.Unlocks", "Deserialize"));
            f.Systems.Add(System("SomeMod.Both", "Modification3"));
            f.Systems.Add(System("SomeMod.LoadFix", "MainLoop"));
            f.Systems.Add(new SystemFact { Name = "SomeMod.Tool", IsTool = true });
            ModReport r = ModClassifier.Classify(f, Declare(
                "everywhere: SomeMod.Water",
                "host-only: SomeMod.Zones",
                "join-off: SomeMod.Unlocks",
                "host-only: SomeMod.Both",
                "everywhere: SomeMod.Both",
                "join-off: SomeMod.LoadFix",
                "host-only: SomeMod.LoadFix",
                "host-only: SomeMod.Tool"));
            Assert.Equal(SystemTreatment.KeptOnClients, TreatmentOfSystem(r, "SomeMod.Water"));
            Assert.Equal(SystemTreatment.HostOnly, TreatmentOfSystem(r, "SomeMod.Zones"));
            Assert.Equal(SystemTreatment.JoinOff, TreatmentOfSystem(r, "SomeMod.Unlocks"));
            Assert.Equal(SystemTreatment.Everywhere, TreatmentOfSystem(r, "SomeMod.Both"), "contradicting declarations fall back to the default");
            Assert.Equal(SystemTreatment.JoinOff, TreatmentOfSystem(r, "SomeMod.LoadFix"), "join-off and host-only combine");
            Assert.Equal(2, r.Problems.Count, string.Join(" | ", r.Problems));
            Assert.Contains("is a tool", r.Problems[0]);
            Assert.Contains("SomeMod.Both is declared host-only and everywhere", r.Problems[1]);
        }

        [Test]
        public void TheModsOwnScopeDeclarationWins()
        {
            ModFacts f = Facts();
            f.Components.Add(Component("SomeMod.UnusedTimer"));
            ModReport r = ModClassifier.Classify(f, Declare("client-local"));
            Assert.False(r.WorldAffecting);
            Assert.True(r.ScopeDeclared);
            Assert.Contains("declared client-local although it saves 1 component type(s)", string.Join(" ", r.Hints));

            ModFacts ui = Facts();
            ui.Systems.Add(System("SomeMod.Panel", "UIUpdate"));
            Assert.True(ModClassifier.Classify(ui, Declare("world")).WorldAffecting);

            r = ModClassifier.Classify(ui, Declare("client-local", "world"));
            Assert.False(r.WorldAffecting, "both cancel; the scan decides");
            Assert.False(r.ScopeDeclared);
            Assert.Contains("both", r.Problems[0]);
        }

        [Test]
        public void HostSettingsAreValidated()
        {
            ModFacts f = Facts();
            f.Settings.Add(new SettingFact { SettingsType = "SomeMod.Setting", Name = "GrowthRate", Supported = true });
            f.Settings.Add(new SettingFact { SettingsType = "SomeMod.Setting", Name = "Theme", Supported = true });
            f.Settings.Add(new SettingFact { SettingsType = "SomeMod.Other", Name = "Theme", Supported = true });
            f.Settings.Add(new SettingFact { SettingsType = "SomeMod.Setting", Name = "ResetButton", Supported = false });
            f.Systems.Add(System("SomeMod.Panel", "UIUpdate"));
            ModReport r = ModClassifier.Classify(f, Declare(
                "host-setting: GrowthRate",
                "host-setting: SomeMod.Other.Theme",
                "host-setting: Theme",
                "host-setting: ResetButton",
                "host-setting: Nope"));
            Assert.SequenceEqual(new[] { "SomeMod.Setting.GrowthRate", "SomeMod.Other.Theme" }, r.HostSettings.Select(s => s.ToString()));
            Assert.Equal(3, r.Problems.Count, string.Join(" | ", r.Problems));
            Assert.Contains("several settings classes", r.Problems[0]);
            Assert.Contains("bool, number, enum or string", r.Problems[1]);
            Assert.Contains("not a property", r.Problems[2]);
            Assert.True(r.WorldAffecting, "settings that must match the host make the mod world-affecting");
        }

        [Test]
        public void ApiVersionsAreReported()
        {
            var newer = new ModDeclarations { HasIntegrationClass = true, ApiVersion = 7 };
            ModReport r = ModClassifier.Classify(Facts(), newer);
            Assert.Contains("API version 7", r.Problems.Single());

            var none = new ModDeclarations { HasIntegrationClass = true, ApiVersion = 0 };
            r = ModClassifier.Classify(Facts(), none);
            Assert.Equal(0, r.Problems.Count);
            Assert.Contains("no ApiVersion", r.Hints.Single());

            var current = new ModDeclarations { HasIntegrationClass = true, ApiVersion = ModDeclarations.SupportedApiVersion };
            r = ModClassifier.Classify(Facts(), current);
            Assert.Equal(0, r.Problems.Count + r.Hints.Count);
        }

        [Test]
        public void EveryDecisionIsExplainedAndTheResultIsDeterministic()
        {
            ModFacts f = Facts("Explained");
            f.Components.Add(Component("Explained.Data"));
            f.Components.Add(Component("Explained.TempThing"));
            f.Systems.Add(System("Explained.Growth", "GameSimulation"));
            f.Systems.Add(new SystemFact { Name = "Explained.Tool", IsTool = true });
            f.Systems.Add(new SystemFact { Name = "Explained.Saver", SavesState = true, PostDeserialize = true });
            f.Systems[2].Phases.Add("Modification2");
            string a = ModClassifier.Explain(ModClassifier.Classify(f));
            string b = ModClassifier.Explain(ModClassifier.Classify(f));
            Assert.Equal(a, b);
            Assert.Contains("Explained: world-affecting: 2 replicated components, 1 system off on clients, 1 direct-write tool", a);
            Assert.Contains("component Explained.Data: replicated", a);
            Assert.Contains("system Explained.Growth: off on clients (GameSimulation", a);
            Assert.Contains("tool Explained.Tool: direct-write", a);
            Assert.Contains("hint: Explained.TempThing is saved by the game but named like tool or preview data", a);
            Assert.Contains("hint: Explained.Saver saves its own state", a);
            Assert.Contains("hint: Explained.Saver runs after a city loads", a);
        }
    }
}
