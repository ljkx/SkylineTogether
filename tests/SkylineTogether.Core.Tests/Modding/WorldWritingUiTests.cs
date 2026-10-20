using SkylineTogether.Core.Modding;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Modding
{
    /// <summary>The game UI types that write shared state, with their real namespaces (R2 G9).</summary>
    public sealed class WorldWritingUiTests
    {
        [Test]
        public void BudgetTaxLoanTileChirpAndDistrictPanelsWriteTheWorld()
        {
            foreach (string t in new[]
            {
                "Game.UI.InGame.ServiceBudgetUISystem.SetServiceBudget", "Game.UI.InGame.TaxationUISystem.SetTaxRate",
                "Game.UI.InGame.LoanUISystem.RequestLoan", "Game.UI.InGame.MapTilesUISystem.PurchaseTiles",
                "Game.UI.InGame.ChirperUISystem.AddLike", "Game.UI.InGame.DistrictsSection.OnUpdate",
                "Game.UI.Editor.MapPanelSystem.OnUpdate", "Game.UI.Editor.WaterPanelSystem.OnUpdate",
            })
            {
                Assert.True(ModPhases.IsWorldWritingUiTarget(t), t);
                Assert.False(ModPhases.IsPresentationTarget(t), t);
            }
        }

        [Test]
        public void TheNamesThatDoNotExistAreGone()
        {
            Assert.False(ModPhases.IsWorldWritingUiTarget("Game.UI.InGame.MapPanelSystem.OnUpdate"));
            Assert.True(ModPhases.IsPresentationTarget("Game.UI.InGame.BudgetUISystem.OnUpdate"), "the budget overview only shows");
        }
    }
}
