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
            // a change seen now that happened since the previous frame (9.98)
            Assert.True(ModStateProposalRule.MayPropose(false, 9.98, 10, 8));
            Assert.False(ModStateProposalRule.MayPropose(false, 9.98, 10, 6.5));
            Assert.True(ModStateProposalRule.MayPropose(true, 9.98, 10, double.NegativeInfinity), "a tool applying");
            Assert.True(ModStateProposalRule.MayPropose(false, 9.98, 10, double.PositiveInfinity), "input unreadable: as before");
            Assert.False(ModStateProposalRule.MayPropose(false, 9.98, 10, double.NegativeInfinity), "no input ever");
        }

        [Test]
        public void AChangeTheSweepFoundLateCountsInputAroundWhenItMayHaveHappened()
        {
            // the slow sweep saw it at 20; it happened after the previous pass started (12)
            Assert.True(ModStateProposalRule.MayPropose(false, 12, 20, 10), "input up to 3 s before the change may have happened");
            Assert.True(ModStateProposalRule.MayPropose(false, 12, 20, 19), "input between the change and its detection");
            Assert.False(ModStateProposalRule.MayPropose(false, 12, 20, 8.9), "input long before");
        }
    }
}
