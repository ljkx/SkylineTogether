using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Session
{
    /// <summary>The joiner's download generations: an older import or load never continues on a newer download.</summary>
    public sealed class LoadGenerationTests
    {
        [Test]
        public void AStepOfAnOlderDownloadIsToldItWasSuperseded()
        {
            var g = new LoadGeneration();
            int first = g.Next();
            g.Started(first);
            Assert.True(g.Busy);
            Assert.True(g.CurrentBusy);
            g.DisposeRequested = true;

            // a rejoin opens a newer download while the first one's asset import still runs
            int second = g.Next();
            Assert.False(g.CurrentBusy, "the new download has no step running yet");
            Assert.True(g.Busy, "but no further join may start while the old step runs");
            Assert.False(g.DisposeRequested, "a dispose request belonged to the old download");
            g.Started(second);
            Assert.False(g.Finished(first), "the old import completes: superseded, it must not start a load");
            Assert.True(g.Busy, "the new download's step still runs");
            Assert.True(g.CurrentBusy);
            Assert.True(g.Finished(second));
            Assert.False(g.Busy);
            Assert.False(g.CurrentBusy);
            Assert.Equal(0, g.Running);
        }

        [Test]
        public void TheCurrentDownloadsStepsAreCounted()
        {
            var g = new LoadGeneration();
            int gen = g.Next();
            Assert.False(g.Busy);
            g.Started(gen); // the import
            Assert.True(g.Finished(gen));
            g.Started(gen); // then the load
            Assert.True(g.CurrentBusy);
            Assert.True(g.Finished(gen));
            Assert.False(g.Finished(gen) && g.Running < 0, "an extra completion never goes negative");
            Assert.Equal(0, g.Running);
        }
    }
}
