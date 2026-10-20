using SkylineTogether.Core.Diagnostics;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Diagnostics
{
    /// <summary>The mod log's flood guard: a rate limit per second, a size cap per run, and notices about what was left out.</summary>
    public sealed class LogFloodGuardTests
    {
        [Test]
        public void ALineFloodIsCutAndTheNextLineSaysHowManyWereLeftOut()
        {
            var g = new LogFloodGuard { LinesPerSecond = 10, Burst = 20 };
            int written = 0;
            for (int i = 0; i < 1000; i++)
            {
                if (g.Allow(LogLevel.Info, 50, 0.0, out _)) written++;
            }
            Assert.Equal(20, written, "the burst, then nothing in the same instant");
            Assert.Equal(980L, g.SuppressedTotal);
            Assert.True(g.Allow(LogLevel.Info, 10, 1.0, out string notice), "refilled after a second");
            Assert.Equal("980 log line(s) left out (too many at once)", notice);
            Assert.True(g.Allow(LogLevel.Info, 10, 1.0, out notice));
            Assert.Null(notice, "said once");
        }

        [Test]
        public void ErrorsHaveTheirOwnBudget()
        {
            var g = new LogFloodGuard { LinesPerSecond = 1, Burst = 1, ErrorsPerSecond = 1, ErrorBurst = 3 };
            Assert.True(g.Allow(LogLevel.Info, 1, 0, out _));
            Assert.False(g.Allow(LogLevel.Info, 1, 0, out _));
            Assert.True(g.Allow(LogLevel.Error, 1, 0, out _), "a warning flood does not hide errors");
            Assert.True(g.Allow(LogLevel.Critical, 1, 0, out _));
            Assert.True(g.Allow(LogLevel.Error, 1, 0, out _));
            Assert.False(g.Allow(LogLevel.Error, 1, 0, out _));
        }

        [Test]
        public void TheLogStopsGrowingAtItsCap()
        {
            var g = new LogFloodGuard { MaxBytes = 1000, LinesPerSecond = 1e9, Burst = 1e9, ErrorsPerSecond = 1e9, ErrorBurst = 1e9 };
            double now = 0;
            while (g.Allow(LogLevel.Info, 100, now, out _)) now += 0.001;
            Assert.True(g.Written >= 1000 && g.Written < 1100);
            Assert.False(g.Allow(LogLevel.Warn, 100, now, out _), "only errors once the cap is reached");
            Assert.True(g.Allow(LogLevel.Error, 10, now, out string notice));
            Assert.Contains("only errors are written", notice);
            int errors = 0;
            while (g.Allow(LogLevel.Error, 10, now, out _) && errors < 1000) errors++;
            Assert.True(errors < 100, "errors stop too, a quarter of the cap later");
            Assert.True(g.Written <= 1000 + 250 + 200);
        }
    }
}
