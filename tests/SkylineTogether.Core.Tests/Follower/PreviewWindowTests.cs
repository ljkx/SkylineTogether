using System;
using SkylineTogether.Core.Follower;
using SkylineTogether.Core.Json;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Follower
{
    /// <summary>
    /// <see cref="PreviewWindow"/> (ARCH §6.3, §4.11.4, Appendix D): opens with the tool, lingers exactly
    /// <c>lingerSeconds</c> after deactivation or the last apply, re-opens on reactivation, and never opens while the guard
    /// is unhealthy or the client is not live.
    /// </summary>
    public sealed class PreviewWindowTests
    {
        private const string RouteTool = "Game.Tools.RouteToolSystem";
        private const string DefaultTool = "Game.Tools.DefaultToolSystem";

        private static ToolScopedGateEntry RouteEntry(double linger = 2.0, string guard = "RouteGuard")
        {
            return new ToolScopedGateEntry
            {
                ToolTypeName = RouteTool,
                Systems = new[] { "Game.Routes.WaypointConnectionSystem", "Game.Routes.RoutePathSystem" },
                LingerSeconds = linger,
                Guard = guard
            };
        }

        private static bool Tick(PreviewWindow window, double now, string tool, bool applied = false, bool live = true, bool guard = true)
        {
            return window.Step(now, tool, applied, live, guard);
        }

        [Test]
        public void OpensWhileTheToolIsActive()
        {
            var window = new PreviewWindow(RouteEntry());
            Assert.Equal(PreviewWindowState.Closed, window.State);
            Assert.False(window.IsEnabled);
            Assert.True(double.IsNegativeInfinity(window.LastActiveSeconds), "never active yet");

            Assert.True(Tick(window, 10.0, RouteTool));
            Assert.Equal(PreviewWindowState.Open, window.State);
            Assert.True(window.EnabledChanged, "rising edge");
            Assert.Equal(1, window.OpenCount);
            Assert.Equal(10.0, window.LastActiveSeconds);

            Assert.True(Tick(window, 10.5, RouteTool));
            Assert.False(window.EnabledChanged, "no edge while it stays open");
            Assert.Equal(10.5, window.LastActiveSeconds);
            Assert.Same(window.Entry, window.Entry);
        }

        [Test]
        public void OtherToolsDoNotOpenIt()
        {
            var window = new PreviewWindow(RouteEntry());
            Assert.False(Tick(window, 1.0, DefaultTool));
            Assert.False(Tick(window, 2.0, null));
            Assert.False(Tick(window, 3.0, DefaultTool, applied: true), "another tool's apply never opens the window");
            Assert.False(Tick(window, 4.0, "game.tools.routetoolsystem"), "type names compare ordinally");
            Assert.Equal(PreviewWindowState.Closed, window.State);
            Assert.False(window.EnabledChanged);
            Assert.Equal(0, window.OpenCount);
        }

        [Test]
        public void LingersExactlyLingerSecondsAfterDeactivation()
        {
            var window = new PreviewWindow(RouteEntry(2.0));
            Tick(window, 10.0, RouteTool);
            // first step without the tool: it was active during part of this frame, so the linger starts here
            Assert.True(Tick(window, 10.25, DefaultTool));
            Assert.Equal(PreviewWindowState.Lingering, window.State);
            Assert.Equal(10.25, window.LastActiveSeconds);
            Assert.False(window.EnabledChanged, "still enabled");
            Assert.True(Tick(window, 11.5, DefaultTool));
            Assert.True(Tick(window, 12.2490234375, DefaultTool));
            Assert.Equal(10.25, window.LastActiveSeconds, "lingering steps do not move the reference time");
            Assert.False(Tick(window, 12.25, DefaultTool), "closes lingerSeconds after the deactivation step");
            Assert.Equal(PreviewWindowState.Closed, window.State);
            Assert.True(window.EnabledChanged, "falling edge");
            Assert.False(Tick(window, 12.5, DefaultTool));
            Assert.False(window.EnabledChanged);
        }

        [Test]
        public void TheLastApplyRestartsTheLinger()
        {
            var window = new PreviewWindow(RouteEntry(2.0));
            Assert.True(Tick(window, 20.0, RouteTool, applied: true));
            Assert.Equal(20.0, window.LastActiveSeconds);
            Assert.True(Tick(window, 20.25, RouteTool, applied: true), "applying keeps the tool active");
            Assert.Equal(20.25, window.LastActiveSeconds);
            // the tool applied and switched away in one frame: ToolSystem.applyMode still reports its apply while
            // activeTool already names the next tool; the linger starts at this step either way
            Assert.True(Tick(window, 20.5, DefaultTool, applied: true));
            Assert.Equal(20.5, window.LastActiveSeconds);
            Assert.Equal(3, window.AppliesSeen);
            Assert.True(Tick(window, 22.25, DefaultTool));
            Assert.False(Tick(window, 22.5, DefaultTool), "2 s after the last apply");

            // the same frame sequence reported with the next tool's applyMode (no apply flag) gives the same window
            var window2 = new PreviewWindow(RouteEntry(2.0));
            Tick(window2, 20.0, RouteTool, applied: true);
            Assert.True(Tick(window2, 20.5, DefaultTool));
            Assert.True(Tick(window2, 22.25, DefaultTool));
            Assert.False(Tick(window2, 22.5, DefaultTool));
        }

        [Test]
        public void AnotherToolsAppliesDoNotExtendTheLinger()
        {
            var window = new PreviewWindow(RouteEntry(2.0));
            Tick(window, 30.0, RouteTool);
            Tick(window, 31.0, DefaultTool);
            Assert.True(Tick(window, 31.75, DefaultTool, applied: true));
            Assert.Equal(31.0, window.LastActiveSeconds, "they are not this tool's applies");
            Assert.True(Tick(window, 32.5, DefaultTool, applied: true));
            Assert.False(Tick(window, 33.0, DefaultTool, applied: true), "closes 2 s after the route tool's last frame");
            Assert.Equal(0, window.AppliesSeen);
        }

        [Test]
        public void AnotherToolApplyingEveryFrameCannotHoldTheWindowOpen()
        {
            // a terrain brush reports applyMode == Apply in every frame while painting (frames of 1/64 s, exact in binary)
            const string TerrainTool = "Game.Tools.TerrainToolSystem";
            var window = new PreviewWindow(RouteEntry(2.0));
            Assert.True(Tick(window, 0.0, RouteTool));
            int closedAt = -1;
            for (int frame = 1; frame <= 640 && closedAt < 0; frame++)
            {
                if (!Tick(window, frame / 64.0, TerrainTool, applied: true)) closedAt = frame;
            }
            Assert.Equal(1 + 128, closedAt, "2 s after the deactivation step at frame 1");
            Assert.Equal(PreviewWindowState.Closed, window.State);
        }

        [Test]
        public void ReopensOnReactivation()
        {
            var window = new PreviewWindow(RouteEntry(2.0));
            Tick(window, 1.0, RouteTool);
            Tick(window, 2.0, DefaultTool);
            Assert.Equal(PreviewWindowState.Lingering, window.State);
            Assert.True(Tick(window, 2.5, RouteTool));
            Assert.Equal(PreviewWindowState.Open, window.State);
            Assert.False(window.EnabledChanged, "lingering to open is no edge");
            Assert.Equal(1, window.OpenCount);
            Assert.True(Tick(window, 4.0, DefaultTool), "deactivated again: a new linger starts");
            Assert.True(Tick(window, 5.5, DefaultTool), "measured from the second deactivation, not the first");
            Assert.False(Tick(window, 6.0, DefaultTool));

            Assert.True(Tick(window, 9.0, RouteTool));
            Assert.True(window.EnabledChanged);
            Assert.Equal(2, window.OpenCount, "a second opening from Closed");
        }

        [Test]
        public void NeverOpensWhileTheGuardIsUnhealthy()
        {
            var window = new PreviewWindow(RouteEntry());
            Assert.False(Tick(window, 1.0, RouteTool, guard: false));
            Assert.Equal(PreviewWindowState.Closed, window.State);
            Assert.False(window.EnabledChanged);
            Assert.True(Tick(window, 2.0, RouteTool), "opens once the guard is healthy");

            // an open window closes at once when the guard fails, without lingering
            Assert.False(Tick(window, 2.1, RouteTool, guard: false));
            Assert.True(window.EnabledChanged);
            Assert.Equal(PreviewWindowState.Closed, window.State);

            // also while lingering
            Tick(window, 3.0, RouteTool);
            Tick(window, 3.5, DefaultTool);
            Assert.Equal(PreviewWindowState.Lingering, window.State);
            Assert.False(Tick(window, 3.6, DefaultTool, guard: false));
            Assert.Equal(PreviewWindowState.Closed, window.State);
        }

        [Test]
        public void NeverOpensWhileTheClientIsNotLive()
        {
            var window = new PreviewWindow(RouteEntry());
            Assert.False(Tick(window, 1.0, RouteTool, live: false));
            Assert.Equal(0, window.OpenCount);
            Assert.True(Tick(window, 2.0, RouteTool));
            Assert.False(Tick(window, 2.5, RouteTool, live: false), "resync or fault closes the window at once");
            Assert.True(window.EnabledChanged);
            Assert.False(Tick(window, 2.6, RouteTool, live: false, guard: false));
            Assert.False(window.EnabledChanged);
        }

        [Test]
        public void AnEntryWithoutGuardIgnoresGuardHealth()
        {
            var window = new PreviewWindow(RouteEntry(guard: null));
            Assert.True(Tick(window, 1.0, RouteTool, guard: false));
            Assert.False(Tick(window, 1.5, RouteTool, live: false), "liveness still applies");
        }

        [Test]
        public void ZeroLingerClosesWithTheTool()
        {
            var window = new PreviewWindow(RouteEntry(0));
            Assert.True(Tick(window, 1.0, RouteTool));
            Assert.False(Tick(window, 1.0, DefaultTool));
            Assert.True(window.EnabledChanged);
            Assert.Equal(PreviewWindowState.Closed, window.State);
        }

        [Test]
        public void CloseForcesTheWindowShut()
        {
            var window = new PreviewWindow(RouteEntry());
            Assert.False(window.Close(), "nothing to disable");
            Assert.False(window.EnabledChanged);
            Tick(window, 1.0, RouteTool);
            Assert.True(window.Close());
            Assert.True(window.EnabledChanged);
            Assert.Equal(PreviewWindowState.Closed, window.State);
            Assert.False(Tick(window, 1.5, DefaultTool), "a closed window does not linger");
            Assert.True(Tick(window, 2.0, RouteTool), "the tool opens it again");
        }

        [Test]
        public void InvalidTimeClosesTheWindow()
        {
            var window = new PreviewWindow(RouteEntry());
            Tick(window, 1.0, RouteTool);
            Assert.False(Tick(window, double.NaN, RouteTool));
            Assert.True(window.EnabledChanged);
            Assert.False(Tick(window, double.PositiveInfinity, RouteTool));
            Assert.True(Tick(window, 2.0, RouteTool));
        }

        [Test]
        public void ATimeStepBackwardsRebasesTheLinger()
        {
            // IClock is monotonic; a caller passing another time base must neither close the window early nor keep it
            // open until the old time base is reached again
            var window = new PreviewWindow(RouteEntry(2.0));
            Tick(window, 10.0, RouteTool);
            Assert.True(Tick(window, 10.5, DefaultTool));
            Assert.True(Tick(window, 3.0, DefaultTool), "backwards by 7.5 s");
            Assert.Equal(3.0, window.LastActiveSeconds);
            Assert.True(Tick(window, 4.5, DefaultTool));
            Assert.False(Tick(window, 5.0, DefaultTool), "2 s after the rebase");

            var deactivatedBackwards = new PreviewWindow(RouteEntry(2.0));
            Tick(deactivatedBackwards, 10.0, RouteTool);
            Assert.True(Tick(deactivatedBackwards, 9.0, DefaultTool));
            Assert.True(Tick(deactivatedBackwards, 10.5, DefaultTool));
            Assert.False(Tick(deactivatedBackwards, 11.0, DefaultTool));
        }

        [Test]
        public void ConstructorValidatesTheEntry()
        {
            Assert.Throws<ArgumentNullException>(() => new PreviewWindow(null));
            Assert.Throws<ArgumentException>(() => new PreviewWindow(new ToolScopedGateEntry { ToolTypeName = null, LingerSeconds = 2 }));
            Assert.Throws<ArgumentException>(() => new PreviewWindow(new ToolScopedGateEntry { ToolTypeName = string.Empty, LingerSeconds = 2 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PreviewWindow(RouteEntry(-1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PreviewWindow(RouteEntry(double.NaN)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PreviewWindow(RouteEntry(double.PositiveInfinity)));
        }

        [Test]
        public void WorksWithAParsedGatesJsonEntry()
        {
            GateTable table = GateTable.Parse(MiniJson.Parse(
                "{ \"version\": 1, \"toolScoped\": [ { \"tool\": \"Game.Tools.RouteToolSystem\", "
                + "\"systems\": [ \"Game.Routes.WaypointConnectionSystem\", \"Game.Routes.RoutePathSystem\" ], \"guard\": \"RouteGuard\" } ] }"));
            var window = new PreviewWindow(table.ToolScopedEntries[0]);
            Assert.Equal(ToolScopedGateEntry.DefaultLingerSeconds, window.Entry.LingerSeconds);
            Assert.True(Tick(window, 100.0, RouteTool));
            Assert.True(Tick(window, 101.0, DefaultTool));
            Assert.True(Tick(window, 102.5, DefaultTool));
            Assert.False(Tick(window, 103.0, DefaultTool), "default linger is 2 s (Appendix D)");
        }

        [Test]
        public void AWindowHeldClosedOrOpenAllocatesNothingPerFrame()
        {
            // a failed guard self-test keeps the window closed for the whole session: stepping it every frame (also with a
            // log attached) must not build strings
            var window = new PreviewWindow(RouteEntry()) { Log = new ListLog() };
            var open = new PreviewWindow(RouteEntry()) { Log = new ListLog() };
            for (int i = 0; i < 200; i++)
            {
                Tick(window, i, RouteTool, guard: false);
                Tick(window, i, RouteTool, live: false);
                Tick(open, i, RouteTool, applied: true);
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 200; i < 2200; i++)
            {
                Tick(window, i, RouteTool, guard: false);
                Tick(window, i, RouteTool, live: false);
                Tick(open, i, RouteTool, applied: true);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated < 1024, "allocated " + allocated + " bytes over 2000 frames");
            Assert.False(window.IsEnabled);
            Assert.True(open.IsEnabled);
        }

        [Test]
        public void LogsTheEdges()
        {
            var log = new ListLog();
            var window = new PreviewWindow(RouteEntry()) { Log = log };
            Tick(window, 1.0, RouteTool);
            Tick(window, 1.5, DefaultTool);
            Tick(window, 3.5, DefaultTool);
            Tick(window, 4.0, RouteTool);
            Tick(window, 4.5, RouteTool, guard: false);
            Assert.True(log.Contains("Debug [gate] preview window Game.Tools.RouteToolSystem opened (tool active)"));
            Assert.True(log.Contains("Debug [gate] preview window Game.Tools.RouteToolSystem closed 2 s after the tool's last frame"));
            Assert.True(log.Contains("closed: guard RouteGuard unhealthy"));
            Assert.Equal(4, log.Lines.Count, "edges only");
            Assert.Contains("Closed", window.ToString());
        }
    }
}
