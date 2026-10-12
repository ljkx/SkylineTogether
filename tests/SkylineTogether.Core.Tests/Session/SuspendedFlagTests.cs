using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>polish: the joiner's achievements flag is given back as it was before the session.</summary>
    public sealed class SuspendedFlagTests
    {
        [Test]
        public void RestoresTheValueFromBeforeTheFirstSuspension()
        {
            var f = new SuspendedFlag();
            Assert.False(f.Suspend(true));
            Assert.True(f.Held);
            Assert.False(f.Suspend(false)); // a rejoin suspends again while the flag is already off
            Assert.True(f.TryRestore(out bool value));
            Assert.True(value);
            Assert.False(f.Held);
            Assert.False(f.TryRestore(out _));
        }

        [Test]
        public void APlayerWithAchievementsOffKeepsThemOff()
        {
            var f = new SuspendedFlag();
            f.Suspend(false);
            Assert.True(f.TryRestore(out bool value));
            Assert.False(value);
        }

        [Test]
        public void ForgetDropsTheHeldValue()
        {
            var f = new SuspendedFlag();
            f.Suspend(true);
            f.Forget();
            Assert.False(f.Held);
            Assert.False(f.TryRestore(out _));
            // a later session records afresh
            f.Suspend(true);
            Assert.True(f.TryRestore(out bool value));
            Assert.True(value);
        }
    }
}
