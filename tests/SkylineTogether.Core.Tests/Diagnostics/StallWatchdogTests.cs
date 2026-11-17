using System.Diagnostics;
using SkylineTogether.Core.Diagnostics;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Diagnostics
{
    /// <summary>The stall watchdog's decision (<see cref="StallWatchdog.Check"/>), with time passed in.</summary>
    public sealed class StallWatchdogTests
    {
        private static long After(long start, double seconds) => start + (long)(seconds * Stopwatch.Frequency);

        [Test]
        public void NothingIsReportedWhileFramesKeepComing()
        {
            using var w = new StallWatchdog(null, background: false);
            w.Beat(true);
            long beat = StallWatchdog.Now;
            Assert.Null(w.Check(After(beat, 1.0), out bool warning));
            Assert.False(warning);
            Assert.Equal(0L, w.Stalls);
        }

        [Test]
        public void AStallNamesTheMarkedStepThenRepeatsThenReportsTheResume()
        {
            using var w = new StallWatchdog(null, background: false);
            w.Beat(true);
            string previous = w.Enter("orphan sweep");
            long t0 = StallWatchdog.Now;

            string first = w.Check(After(t0, 6.0), out bool warning);
            Assert.NotNull(first);
            Assert.True(warning);
            Assert.Contains("'orphan sweep'", first);
            Assert.Equal(1L, w.Stalls);

            Assert.Null(w.Check(After(t0, 8.0), out _), "no repeat before RepeatSeconds");
            string again = w.Check(After(t0, 17.0), out bool warningAgain);
            Assert.NotNull(again);
            Assert.True(warningAgain);
            Assert.Equal(1L, w.Stalls, "one stall, two lines");

            w.Exit(previous);
            w.Beat(true);
            string resumed = w.Check(StallWatchdog.Now, out bool warningResumed);
            Assert.NotNull(resumed);
            Assert.False(warningResumed);
            Assert.Contains("resumed", resumed);
        }

        [Test]
        public void AStallOutsideTheMarkedStepsNamesTheLastStep()
        {
            using var w = new StallWatchdog(null, background: false);
            w.Beat(true);
            string previous = w.Enter("frame-end capture");
            w.Exit(previous);
            string line = w.Check(After(StallWatchdog.Now, 6.0), out _);
            Assert.NotNull(line);
            Assert.Contains("outside Skyline Together", line);
            Assert.Contains("'frame-end capture'", line);
        }

        [Test]
        public void NothingIsReportedOutsideASession()
        {
            using var w = new StallWatchdog(null, background: false);
            w.Beat(false);
            Assert.Null(w.Check(After(StallWatchdog.Now, 60.0), out _));
        }

        [Test]
        public void NestedStepsRestoreTheOuterOne()
        {
            using var w = new StallWatchdog(null, background: false);
            string outer = w.Enter("frame-start apply");
            string inner = w.Enter("orphan sweep");
            Assert.Equal("orphan sweep", w.Section);
            w.Exit(inner);
            Assert.Equal("frame-start apply", w.Section);
            w.Exit(outer);
            Assert.Null(w.Section);
        }
    }
}
