using SkylineTogether.Core.Modding;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Modding
{
    /// <summary>
    /// Which input opens the window in which a mod state change counts as the player's edit (R2 G2): panel and hotkey
    /// input, held or not; never camera moves, world clicks without a tool or chat typing.
    /// </summary>
    public sealed class EditInputTests
    {
        [Test]
        public void NothingAtAllIsNoEdit() => Assert.False(EditInput.IsEdit(new InputSample()));

        [Test]
        public void APanelClickOrASliderHeldForLongIsAnEdit()
        {
            Assert.True(EditInput.IsEdit(new InputSample { PointerEdge = true, PointerOverUi = true }));
            Assert.True(EditInput.IsEdit(new InputSample { PointerHeld = true, PointerOverUi = true }), "a drag longer than the window still counts every frame");
            Assert.True(EditInput.IsEdit(new InputSample { Wheel = true, PointerOverUi = true }), "the wheel over a panel's slider");
        }

        [Test]
        public void ClicksAndTheWheelOverTheWorldAreNoEdit()
        {
            // selection and camera zoom; mod tools count through the tool path, not through input
            Assert.False(EditInput.IsEdit(new InputSample { PointerEdge = true }));
            Assert.False(EditInput.IsEdit(new InputSample { PointerHeld = true }));
            Assert.False(EditInput.IsEdit(new InputSample { Wheel = true }));
        }

        [Test]
        public void HotkeysCountPressedOrHeld()
        {
            Assert.True(EditInput.IsEdit(new InputSample { KeyEdge = true }));
            Assert.True(EditInput.IsEdit(new InputSample { KeyHeld = true }), "key repeat makes no new edge");
            Assert.True(EditInput.IsEdit(new InputSample { PadButton = true }));
        }

        [Test]
        public void TypingInTheChatIsNoEditButCommittingAFieldIs()
        {
            Assert.False(EditInput.IsEdit(new InputSample { TextFocus = true, KeyEdge = true, KeyHeld = true }));
            Assert.True(EditInput.IsEdit(new InputSample { TextFocus = true, CommitKey = true }));
            Assert.True(EditInput.IsEdit(new InputSample { TextFocus = true, PointerEdge = true, PointerOverUi = true }));
        }

        [Test]
        public void TheWindowFollowsTheLastEditInput()
        {
            Assert.True(ModStateProposalRule.MayPropose(false, 10, 8));
            Assert.False(ModStateProposalRule.MayPropose(false, 10, 6.5));
            Assert.True(ModStateProposalRule.MayPropose(true, 10, double.NegativeInfinity), "a tool applying");
            Assert.True(ModStateProposalRule.MayPropose(false, 10, double.PositiveInfinity), "input unreadable: as before");
        }
    }
}
