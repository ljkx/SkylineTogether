using System;
using SkylineTogether.Core.Follower;
using SkylineTogether.Core.Json;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Follower
{
    /// <summary>
    /// <see cref="PreviewWindowSet"/>: the per-system OR over overlapping windows, guard health (fail closed until
    /// reported), the <c>IToolScopedGateState</c> queries and the edges the game layer applies.
    /// </summary>
    public sealed class PreviewWindowSetTests
    {
        private const string RouteTool = "Game.Tools.RouteToolSystem";
        private const string MoveItTool = "MoveIt.Tools.MoveItTool";
        private const string DefaultTool = "Game.Tools.DefaultToolSystem";
        private const string Waypoint = "Game.Routes.WaypointConnectionSystem";
        private const string RoutePath = "Game.Routes.RoutePathSystem";

        // the gates.json sample: both tools scope WaypointConnectionSystem; the Move It entry has no guard
        private static PreviewWindowSet SampleSet()
        {
            GateTable table = GateTable.Parse(MiniJson.Parse((@"{ 'version': 1, 'toolScoped': [
                { 'tool': 'Game.Tools.RouteToolSystem', 'systems': [ 'Game.Routes.WaypointConnectionSystem', 'Game.Routes.RoutePathSystem' ],
                  'lingerSeconds': 2, 'guard': 'RouteGuard' },
                { 'tool': 'MoveIt.Tools.MoveItTool', 'systems': [ 'Game.Routes.WaypointConnectionSystem' ], 'lingerSeconds': 1 } ] }").Replace('\'', '"')));
            return new PreviewWindowSet(table.ToolScopedEntries);
        }

        [Test]
        public void StartsWithEverySystemDisabled()
        {
            PreviewWindowSet set = SampleSet();
            Assert.SequenceEqual(new[] { Waypoint, RoutePath }, set.Systems);
            Assert.Equal(2, set.Windows.Count);
            Assert.False(set.IsSystemEnabled(Waypoint));
            Assert.False(set.IsSystemEnabled(RoutePath));
            Assert.False(set.IsSystemEnabled(null));
            Assert.False(set.AnyWindowOpenOrLingering);
            Assert.Equal(0, set.SystemsToEnable.Count);
            Assert.Equal(0, set.SystemsToDisable.Count);
        }

        [Test]
        public void GuardsAreUnhealthyUntilReported()
        {
            PreviewWindowSet set = SampleSet();
            Assert.False(set.IsGuardHealthy("RouteGuard"));
            Assert.True(set.IsGuardHealthy(null), "no guard needed");
            Assert.False(set.Step(1.0, RouteTool, false, true), "the route guard's self-test has not reported yet");
            Assert.False(set.IsWindowOpen("RouteGuard"));

            set.ReportGuardHealth("RouteGuard", true);
            Assert.True(set.Step(1.1, RouteTool, false, true));
            Assert.SequenceEqual(new[] { Waypoint, RoutePath }, set.SystemsToEnable);
            Assert.Equal(0, set.SystemsToDisable.Count);
            Assert.True(set.IsWindowOpen("RouteGuard"));
            Assert.True(set.AnyWindowOpenOrLingering);
            Assert.False(set.IsWindowOpen("OtherGuard"));
            Assert.False(set.IsWindowOpen(null));

            // a later failure closes the window at the next step, without lingering
            set.ReportGuardHealth("RouteGuard", false);
            Assert.True(set.Step(1.2, RouteTool, false, true));
            Assert.SequenceEqual(new[] { Waypoint, RoutePath }, set.SystemsToDisable);
            Assert.False(set.AnyWindowOpenOrLingering);
        }

        [Test]
        public void OverlappingWindowsKeepASharedSystemEnabled()
        {
            PreviewWindowSet set = SampleSet();
            set.ReportGuardHealth("RouteGuard", true);

            set.Step(10.0, RouteTool, false, true);
            Assert.True(set.IsSystemEnabled(Waypoint));
            Assert.True(set.IsSystemEnabled(RoutePath));

            // switch straight to Move It: the route window lingers 2 s from this step, the Move It window opens
            Assert.False(set.Step(10.5, MoveItTool, false, true), "no system changes state");
            Assert.Equal(PreviewWindowState.Lingering, set.Windows[0].State);
            Assert.Equal(PreviewWindowState.Open, set.Windows[1].State);
            Assert.False(set.Step(12.0, MoveItTool, false, true), "still lingering");

            // the route window closes: RoutePathSystem goes off, WaypointConnectionSystem stays on for Move It
            Assert.True(set.Step(12.5, MoveItTool, false, true));
            Assert.SequenceEqual(new[] { RoutePath }, set.SystemsToDisable);
            Assert.Equal(0, set.SystemsToEnable.Count);
            Assert.True(set.IsSystemEnabled(Waypoint));
            Assert.False(set.IsWindowOpen("RouteGuard"));
            Assert.True(set.AnyWindowOpenOrLingering);

            // Move It deactivated: its 1 s linger ends, the shared system goes off
            Assert.False(set.Step(13.0, DefaultTool, false, true));
            Assert.False(set.Step(13.5, DefaultTool, false, true));
            Assert.True(set.Step(14.0, DefaultTool, false, true));
            Assert.SequenceEqual(new[] { Waypoint }, set.SystemsToDisable);
            Assert.False(set.AnyWindowOpenOrLingering);
        }

        [Test]
        public void CloseAllDisablesEverythingEnabled()
        {
            PreviewWindowSet set = SampleSet();
            set.ReportGuardHealth("RouteGuard", true);
            set.Step(1.0, RouteTool, false, true);
            Assert.True(set.CloseAll());
            Assert.SequenceEqual(new[] { Waypoint, RoutePath }, set.SystemsToDisable);
            Assert.False(set.CloseAll(), "nothing left to disable");
            Assert.Equal(0, set.SystemsToDisable.Count);
        }

        [Test]
        public void NotLiveClosesEverything()
        {
            PreviewWindowSet set = SampleSet();
            set.ReportGuardHealth("RouteGuard", true);
            set.Step(1.0, RouteTool, false, true);
            Assert.True(set.Step(1.1, RouteTool, false, false));
            Assert.SequenceEqual(new[] { Waypoint, RoutePath }, set.SystemsToDisable);
        }

        [Test]
        public void EntriesCanBeAddedLaterAndValidated()
        {
            var set = new PreviewWindowSet(null);
            Assert.Equal(0, set.Systems.Count);
            set.Add(new ToolScopedGateEntry { ToolTypeName = "Mod.Tools.Painter", Systems = new[] { "Mod.Paint.PreviewSystem", "", null }, LingerSeconds = 0 });
            Assert.SequenceEqual(new[] { "Mod.Paint.PreviewSystem" }, set.Systems);
            Assert.True(set.Step(5.0, "Mod.Tools.Painter", false, true), "no guard: opens at once");
            Assert.SequenceEqual(new[] { "Mod.Paint.PreviewSystem" }, set.SystemsToEnable);
            Assert.True(set.Step(5.1, DefaultTool, false, true), "zero linger closes with the tool");

            Assert.Throws<ArgumentNullException>(() => set.Add(null));
            Assert.Throws<ArgumentException>(() => set.Add(new ToolScopedGateEntry { ToolTypeName = null }));
            set.Add(new ToolScopedGateEntry { ToolTypeName = "Mod.Tools.Empty", Systems = null, LingerSeconds = 1 });
            Assert.Equal(2, set.Windows.Count);
        }

        [Test]
        public void SteppingAndReadingTheEdgesAllocatesNothing()
        {
            // ToolScopedGates steps the set and reads the edge lists every frame
            PreviewWindowSet set = SampleSet();
            set.ReportGuardHealth("RouteGuard", false);   // failed self-test: route window held closed all session
            Assert.Same(set.SystemsToEnable, set.SystemsToEnable, "cached views");
            Assert.Same(set.Windows, set.Windows);
            int edges = 0;
            for (int i = 0; i < 200; i++) edges += set.Step(i, RouteTool, false, true) ? 1 : 0;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 200; i < 2200; i++)
            {
                if (set.Step(i, RouteTool, true, true)) edges++;
                edges += set.SystemsToEnable.Count + set.SystemsToDisable.Count;
                if (set.AnyWindowOpenOrLingering || set.IsWindowOpen("RouteGuard")) edges++;
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated < 1024, "allocated " + allocated + " bytes over 2000 frames");
            Assert.Equal(0, edges, "the route window never opens while its guard is unhealthy");
        }

        [Test]
        public void LogsGuardChangesAndWindowEdges()
        {
            var log = new ListLog();
            PreviewWindowSet set = SampleSet();
            set.Log = log;
            set.ReportGuardHealth("RouteGuard", true);
            set.ReportGuardHealth("RouteGuard", true);
            set.Step(1.0, RouteTool, false, true);
            set.ReportGuardHealth("RouteGuard", false);
            Assert.True(log.Contains("Info [gate] guard RouteGuard healthy"));
            Assert.True(log.Contains("Warn [gate] guard RouteGuard unhealthy"));
            Assert.True(log.Contains("Debug [gate] preview window Game.Tools.RouteToolSystem opened (tool active)"));
            Assert.Equal(3, log.Lines.Count, "repeated reports are not logged");
            Assert.Contains("2 window(s), 2/2 system(s) enabled", set.ToString());
        }
    }
}
