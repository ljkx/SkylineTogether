using System.Linq;
using SkylineTogether.Core.Diagnostics;
using SkylineTogether.Core.Session;
using SkylineTogether.Core.Tests.Framework;
using SkylineTogether.Core.Tests.Session;

namespace SkylineTogether.Core.Tests.Diagnostics
{
    /// <summary>
    /// Strangers cannot silence the host's log (R2 N5): warnings have a bucket of their own, and lines about connections
    /// that are not admitted are throttled at the source.
    /// </summary>
    public sealed class LogBudgetTests
    {
        [Test]
        public void AFloodOfInformationLinesDoesNotHideWarnings()
        {
            var g = new LogFloodGuard { LinesPerSecond = 1, Burst = 1, WarningsPerSecond = 1, WarningBurst = 2 };
            Assert.True(g.Allow(LogLevel.Info, 1, 0, out _));
            for (int i = 0; i < 100; i++) Assert.False(g.Allow(LogLevel.Info, 1, 0, out _));
            Assert.True(g.Allow(LogLevel.Warn, 1, 0, out _), "the host's warning still gets through");
            Assert.True(g.Allow(LogLevel.Warn, 1, 0, out _));
            Assert.False(g.Allow(LogLevel.Warn, 1, 0, out _), "warnings are bounded too");
            Assert.True(g.Allow(LogLevel.Error, 1, 0, out _));
        }

        [Test]
        public void ConnectionLinesOfStrangersAreThrottled()
        {
            var rig = new SessionRig(new HostOptions { HostName = "Hosty", MaxConnectionsPerRemotePerMinute = 0 });
            for (int i = 0; i < 200; i++)
            {
                // each stranger connects and goes again (the listener holds only a few connections at a time)
                var stranger = new RawPeer(rig, "Stranger" + i, (ulong)(20000 + i));
                rig.Clock.Advance(0.05);
                rig.Host.Tick();
                stranger.Channel.Connection.Close(0, "bye", false);
                rig.Clock.Advance(0.05);
                rig.Host.Tick();
            }
            int ConnectionLines(string level) => rig.Log.Lines.Count(l => l.StartsWith(level + " ", System.StringComparison.Ordinal) && l.Contains("connection from", System.StringComparison.Ordinal));
            Assert.Equal(200, ConnectionLines("Info") + ConnectionLines("Debug"), "every connection is logged, at some level");
            Assert.Equal(60, ConnectionLines("Info"), "at most 60 connection lines a minute at info level");
            Assert.False(rig.Log.Contains("more connection line(s)"), "the summary comes with the next line allowed");

            // a minute later the next connection is logged again, after a line that counts the quiet ones
            rig.Clock.Advance(61);
            rig.Host.Tick();
            new RawPeer(rig, "Late", 20999);
            rig.Clock.Advance(0.05);
            rig.Host.Tick();
            Assert.Equal(61, ConnectionLines("Info"));
            Assert.True(rig.Log.Lines.Any(l => l.StartsWith("Info ", System.StringComparison.Ordinal) && l.Contains("140 more connection line(s) were logged at debug level only", System.StringComparison.Ordinal)),
                        "the quiet lines are counted");
        }
    }
}
