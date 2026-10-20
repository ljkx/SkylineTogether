using System.Linq;
using SkylineTogether.Core.Modding;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Modding
{
    /// <summary>
    /// Visual mods stay visual (R2 G7): only a system of the UI a player clicks (UIUpdate, UITooltip) that writes entities
    /// makes a mod world-affecting; rendering, culling, raycast and gizmo systems write entities of their own.
    /// </summary>
    public sealed class VisualModTests
    {
        private static ModReport Classify(string phase, bool writes)
        {
            var f = new ModFacts { AssemblyName = "VisualMod", PhasesKnown = true };
            var s = new SystemFact { Name = "VisualMod.System", WritesEntities = writes };
            s.Phases.Add(phase);
            f.Systems.Add(s);
            return ModClassifier.Classify(f);
        }

        [Test]
        public void RenderingAndRaycastSystemsThatWriteTheirOwnEntitiesAreNotWorldAffecting()
        {
            foreach (string phase in new[] { "Rendering", "PreCulling", "Raycast", "DebugGizmos", "CompleteRendering" })
            {
                ModReport r = Classify(phase, true);
                Assert.False(r.WorldAffecting, phase);
                Assert.Equal(SystemTreatment.Presentation, r.Systems.Single().Treatment, phase);
            }
        }

        [Test]
        public void PanelsThatWriteEntitiesStillAre()
        {
            Assert.True(Classify("UIUpdate", true).WorldAffecting);
            Assert.True(Classify("UITooltip", true).WorldAffecting);
            Assert.False(Classify("UIUpdate", false).WorldAffecting);
            Assert.True(ModPhases.IsUiPhase("UIUpdate"));
            Assert.False(ModPhases.IsUiPhase("Rendering"));
        }
    }
}
