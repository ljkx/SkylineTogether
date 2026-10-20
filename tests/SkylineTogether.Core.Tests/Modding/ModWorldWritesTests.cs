using System.Linq;
using SkylineTogether.Core.Modding;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Modding
{
    /// <summary>
    /// Mods that write the world from UI code are world-affecting, and untagged mod state a client's own mod system
    /// derives (no tool, no input) is never proposed to the host.
    /// </summary>
    public sealed class ModWorldWritesTests
    {
        private static ModFacts Facts() => new ModFacts { AssemblyName = "SomeMod", PhasesKnown = true };

        private static SystemFact System(string name, bool writes, params string[] phases)
        {
            var s = new SystemFact { Name = name, WritesEntities = writes };
            s.Phases.AddRange(phases);
            return s;
        }

        [Test]
        public void AUiSystemThatWritesEntitiesIsWorldAffecting()
        {
            ModFacts readOnly = Facts();
            readOnly.Systems.Add(System("SomeMod.InfoPanel", false, "UIUpdate"));
            ModReport r = ModClassifier.Classify(readOnly);
            Assert.False(r.WorldAffecting);
            Assert.Equal(SystemTreatment.Presentation, r.Systems.Single().Treatment);

            ModFacts writing = Facts();
            writing.Systems.Add(System("SomeMod.PolicyPanel", true, "UIUpdate"));
            r = ModClassifier.Classify(writing);
            Assert.True(r.WorldAffecting, "a panel that writes entities changes the city when its player clicks");
            Assert.Equal(SystemTreatment.Everywhere, r.Systems.Single().Treatment);
            Assert.Contains("SomeMod.PolicyPanel in UIUpdate (writes entities)", string.Join(" ", r.ScopeReasons));
        }

        [Test]
        public void WritesOutsideUiPhasesChangeNothingAboutTheTreatment()
        {
            ModFacts f = Facts();
            f.Systems.Add(System("SomeMod.Growth", true, "GameSimulation"));
            f.Systems.Add(System("SomeMod.Derive", true, "Modification3"));
            ModReport r = ModClassifier.Classify(f);
            Assert.Equal(SystemTreatment.OffBySimulation, r.Systems.Single(s => s.Name == "SomeMod.Growth").Treatment);
            Assert.Equal(SystemTreatment.Everywhere, r.Systems.Single(s => s.Name == "SomeMod.Derive").Treatment);
        }

        [Test]
        public void PatchesOfTheGamesWorldWritingUiAreNotPresentation()
        {
            Assert.True(ModPhases.IsPresentationTarget("Game.UI.InGame.SelectedInfoUISystem.OnUpdate"));
            Assert.True(ModPhases.IsPresentationTarget("Colossal.Localization.LocalizationManager.SetActiveLocale"));
            Assert.False(ModPhases.IsPresentationTarget("Game.UI.InGame.PoliciesUISystem.SetPolicy"));
            Assert.False(ModPhases.IsPresentationTarget("Game.UI.InGame.UpgradesSection.OnUpdate"));
            Assert.False(ModPhases.IsPresentationTarget("Game.UI.NameSystem.SetCustomName"));
            Assert.False(ModPhases.IsPresentationTarget("Game.UI.InGame.ActionsSection+Job.Execute"));
            Assert.True(ModPhases.IsPresentationTarget("Game.UI.InGame.ActionsSectionHelper.Format"), "a prefix of a type name is not the type");
            Assert.True(ModPhases.IsWorldWritingUiTarget("Game.UI.InGame.ColorSection.OnSetColor"));
            // the editor's map tile and water source panels live in Game.UI.Editor (tools/ApiCheck found the old names)
            Assert.True(ModPhases.IsWorldWritingUiTarget("Game.UI.Editor.MapPanelSystem.OnUpdate"));
            Assert.True(ModPhases.IsWorldWritingUiTarget("Game.UI.Editor.WaterPanelSystem.OnUpdate"));
            Assert.False(ModPhases.IsPresentationTarget("Game.UI.Editor.WaterPanelSystem.OnUpdate"));

            ModFacts f = Facts();
            f.ReferencesHarmony = true;
            f.PatchedMethods = new System.Collections.Generic.List<string> { "Game.UI.InGame.PoliciesUISystem.SetPolicy" };
            Assert.True(ModClassifier.Classify(f).WorldAffecting);
        }

        [Test]
        public void ModStateChangesWithoutToolOrInputAreDerivations()
        {
            const double now = 100;
            Assert.True(ModStateProposalRule.MayPropose(true, now, now, double.NegativeInfinity), "a tool applies");
            Assert.True(ModStateProposalRule.MayPropose(false, now, now, now - 1), "the player clicked a moment ago (a mod panel)");
            Assert.True(ModStateProposalRule.MayPropose(false, now, now, now - ModStateProposalRule.InputWindowSeconds));
            Assert.False(ModStateProposalRule.MayPropose(false, now, now, now - ModStateProposalRule.InputWindowSeconds - 0.01), "no input for a while: a local derivation");
            Assert.False(ModStateProposalRule.MayPropose(false, now, now, double.NegativeInfinity), "no input this session");
            Assert.True(ModStateProposalRule.MayPropose(false, now, now, double.PositiveInfinity), "input unreadable: proposed as before");
        }
    }
}
