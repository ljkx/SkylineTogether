using SkylineTogether.Core.Diagnostics;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Diagnostics
{
    /// <summary>
    /// The forensics lines of a client (ARCHITECTURE.md#logs): their rate limit, the tool apply line and the frame marker
    /// clock.
    /// </summary>
    public sealed class ForensicsTrailTests
    {
        [Test]
        public void AtMostTwentyLinesASecondAndTheNextOneSaysHowManyWereLeftOut()
        {
            var l = new BreadcrumbLimiter();
            int written = 0;
            for (int i = 0; i < 100; i++)
            {
                if (l.Allow(10.0, out string s))
                {
                    written++;
                    Assert.Null(s);
                }
            }
            Assert.Equal(20, written, "the burst in one instant");
            Assert.Equal(80L, l.SuppressedTotal);
            Assert.False(l.Allow(10.02, out _), "less than a token refilled");
            Assert.True(l.Allow(10.5, out string summary), "refilled after half a second");
            Assert.Equal("81 forensics line(s) left out in the last 0.5 s (more than 20 per second)", summary);
            Assert.True(l.Allow(10.5, out summary));
            Assert.Null(summary, "said once");
            Assert.Equal(22L, l.Written);
        }

        [Test]
        public void ASustainedRateIsHeldAtTwentyPerSecond()
        {
            var l = new BreadcrumbLimiter();
            int written = 0;
            for (int frame = 0; frame < 600; frame++) // ten seconds at 60 fps, three lines a frame
            {
                for (int k = 0; k < 3; k++)
                {
                    if (l.Allow(frame / 60.0, out _)) written++;
                }
            }
            Assert.InRange(written, 20 + 199, 20 + 201);
        }

        [Test]
        public void ThePendingSummaryCanBeTakenWithoutALine()
        {
            var l = new BreadcrumbLimiter { PerSecond = 1, Burst = 1 };
            Assert.Null(l.TakeSummary(0), "nothing left out");
            Assert.True(l.Allow(0, out _));
            Assert.False(l.Allow(0, out _));
            Assert.False(l.Allow(0.3, out _));
            Assert.Equal("2 forensics line(s) left out in the last 0.3 s", l.TakeSummary(0.3));
            Assert.Null(l.TakeSummary(0.4), "taken");
            Assert.True(l.Allow(2, out string s));
            Assert.Null(s, "the taken summary is not repeated");
        }

        [Test]
        public void AToolApplyLineCountsTheToolsAndTheGamesChangesByFamily()
        {
            var t = new ToolApplyTally();
            Assert.True(t.IsEmpty);
            t.Add("NetEdge", ToolApplyTally.Change.Created, byTool: true);
            t.Add("NetEdge", ToolApplyTally.Change.Created, byTool: true);
            t.Add("NetEdge", ToolApplyTally.Change.Deleted, byTool: true);
            t.Add("NetNode", ToolApplyTally.Change.Updated, byTool: true);
            t.Add("Lane", ToolApplyTally.Change.Created, byTool: false);
            t.Add("Lane", ToolApplyTally.Change.Deleted, byTool: false);
            t.Add("Aggregate", ToolApplyTally.Change.Updated, byTool: false);
            t.Add(null, ToolApplyTally.Change.Created, byTool: false);
            Assert.Equal(4, t.ToolTotal);
            Assert.Equal(4, t.DerivedTotal);
            Assert.Equal("tool apply: Net Tool 'Small Road'; tool +2 NetEdge, -1 NetEdge, ~1 NetNode; game +1 ?, ~1 Aggregate, +1 Lane, -1 Lane; proposal 17",
                t.Format("Net Tool", "Small Road", 17));
            t.Clear();
            Assert.True(t.IsEmpty);
            Assert.Equal("tool apply: ?; tool nothing; game nothing; nothing proposed", t.Format(null, null, 0));
        }

        [Test]
        public void AToolApplyLineStaysShort()
        {
            var t = new ToolApplyTally();
            for (int i = 0; i < 40; i++) t.Add("Family" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture), ToolApplyTally.Change.Created, byTool: false);
            string line = t.Format("Bulldoze Tool", null, 3, maxFamilies: 4);
            Assert.Contains("game +1 Family00, +1 Family01, +1 Family02, +1 Family03, ...; proposal 3", line);
            Assert.DoesNotContain("Family04", line);
        }

        [Test]
        public void FrameMarkersBracketABuildingSession()
        {
            var c = new FrameMarkerClock { IntervalSeconds = 5 };
            Assert.False(c.Due(0, false, out _), "no tool: no marker");
            Assert.True(c.Due(1, true, out bool ended), "the first frame of a tool");
            Assert.False(ended);
            Assert.False(c.Due(5.9, true, out _));
            Assert.True(c.Due(6.0, true, out _), "every interval while building");
            Assert.False(c.Due(7, true, out _));
            Assert.True(c.Due(8, false, out ended), "the frame the tool is put away");
            Assert.True(ended);
            Assert.False(c.Due(9, false, out _), "said once");
            Assert.True(c.Due(9.5, true, out ended), "a new tool starts a new bracket at once");
            Assert.False(ended);
            c.Reset();
            Assert.False(c.Due(10, false, out _), "a reset clock has no tool to put away");
        }
    }
}
