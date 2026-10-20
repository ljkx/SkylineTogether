using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>
    /// What the applier writes when a component's references do not all resolve here (R2 G1): a required link (owner,
    /// an edge's nodes) is never written as null over an existing value; the dangling pass removes the entity when the
    /// kept link names something gone.
    /// </summary>
    public sealed class UnresolvedValueRuleTests
    {
        private static int _asked;

        private static bool Live()
        {
            _asked++;
            return true;
        }

        private static bool Dead()
        {
            _asked++;
            return false;
        }

        [Test]
        public void ResolvedValuesAndSpawnsAreWritten()
        {
            _asked = 0;
            Assert.Equal(UnresolvedValueAction.Write, UnresolvedValueRule.Decide(false, true, true, Live));
            Assert.Equal(UnresolvedValueAction.Write, UnresolvedValueRule.Decide(true, false, false, Live), "a spawn is refused elsewhere");
            Assert.Equal(0, _asked, "no liveness check needed");
        }

        [Test]
        public void AnExistingEntityKeepsALiveValueUntilTheNewOneResolves()
        {
            Assert.Equal(UnresolvedValueAction.Keep, UnresolvedValueRule.Decide(true, true, false, Live));
            Assert.Equal(UnresolvedValueAction.Keep, UnresolvedValueRule.Decide(true, true, true, Live));
        }

        [Test]
        public void AnOptionalReferenceNamingSomethingGoneIsWrittenWithNulls()
        {
            Assert.Equal(UnresolvedValueAction.Write, UnresolvedValueRule.Decide(true, true, false, Dead));
        }

        [Test]
        public void ARequiredLinkIsNeverWrittenAsNullOverAnExistingValue()
        {
            // the old owner is gone or despawned by this batch: the old value stays and the dangling pass deletes the
            // entity (and the integrity check asks the host for it), instead of Owner(Null) reaching the game's jobs
            _asked = 0;
            Assert.Equal(UnresolvedValueAction.Keep, UnresolvedValueRule.Decide(true, true, true, Dead));
            Assert.Equal(0, _asked);
        }

        [Test]
        public void OwnersAndEdgesAreTheRequiredLinks()
        {
            Assert.True(UnresolvedValueRule.IsRequiredLink("Game.Common.Owner"));
            Assert.True(UnresolvedValueRule.IsRequiredLink("Game.Net.Edge"));
            Assert.False(UnresolvedValueRule.IsRequiredLink("Game.Objects.Attached"), "a null parent is checked by the game");
            Assert.False(UnresolvedValueRule.IsRequiredLink("Game.Buildings.Building"));
            Assert.False(UnresolvedValueRule.IsRequiredLink(null));
        }
    }
}
