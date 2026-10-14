using System.Diagnostics;
using SkylineTogether.Core.Diagnostics;

namespace SkylineTogether.Diagnostics
{
    /// <summary>
    /// The mod's per-frame timings and channel byte rates (<see cref="PerfCounters"/>), shown in
    /// <c>SyncController.Status</c> and the diagnostics dump. Components time themselves with <see cref="Start"/> /
    /// <see cref="Stop"/> (Stopwatch ticks, no allocation); the frame closes once per frame at the end of the capture.
    /// Main thread only.
    /// </summary>
    public static class Perf
    {
        // component names (timings, ms per frame)
        public const string Apply = "apply";
        public const string Capture = "capture";
        public const string Scan = "scan";
        public const string Deferred = "deferred";
        public const string Integrity = "integrity";
        public const string Global = "global";
        public const string MoverCapture = "mover capture";
        public const string MotionSample = "motion sample";
        public const string MoverApply = "mover apply";
        public const string MotionWrite = "motion write";
        public const string AgentScan = "agent scan";
        public const string AgentApply = "agent apply";
        public const string FlowScan = "flow scan";
        // perf pass: components that were not measured before
        public const string SimValues = "sim values";          // host: attention values scan, samples, streams
        public const string PresenceWatch = "presence watch";  // host: component sets changed without a tag (events)
        public const string Icons = "icons";                   // host: icon set capture
        public const string IconFilter = "icon filter";        // client: local icon commands filtered, host icons reconciled
        public const string Signals = "signals";               // client: lane signals from the host's junction phases
        public const string ClientCapture = "client capture";  // client: proposals of the player's edits
        public const string Terrain = "terrain";               // both: stamps, heightmap tiles, audits
        public const string Water = "water";                   // both: water sources and samples
        public const string Maps = "maps+stats";               // both: cell maps (resources, pollution, ...) and statistics
        public const string Bind = "bind";                     // host: new entities bound before the save system
        public const string Network = "network";               // both: session tick (receive, decode, dispatch, send)
        public const string Presence = "presence";             // both: cursors and name tags

        // channel names (bytes per second, host: summed over the peers it was sent to)
        public const string StructureBytes = "structure";
        public const string DeferredBytes = "values";
        public const string GlobalBytes = "global";
        public const string IntegrityBytes = "integrity";
        public const string MoverBytes = "movers";
        public const string MotionBytes = "motion";
        public const string AgentBytes = "agents";
        public const string ProposalBytes = "proposals";
        public const string FlowBytes = "flow";
        public const string ReceivedBytes = "received";

        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

        public static PerfCounters Counters { get; } = CreateCounters();

        /// <summary>perf pass: components measured inside another one are counted once in the totals.</summary>
        private static PerfCounters CreateCounters()
        {
            var c = new PerfCounters();
            c.Nest(Scan, Capture);
            c.Nest(Integrity, Capture);
            c.Nest(Icons, Integrity);
            c.Nest(Deferred, Capture);
            c.Nest(AgentScan, Capture);
            c.Nest(PresenceWatch, Capture);
            c.Nest(SimValues, Capture);
            c.Nest(FlowScan, Capture);
            c.Nest(ClientCapture, Capture);
            c.Nest(AgentApply, Apply);
            c.Nest(Signals, Apply);
            return c;
        }

        /// <summary>A start timestamp for <see cref="Stop"/>.</summary>
        public static long Start() => Stopwatch.GetTimestamp();

        /// <summary>Milliseconds since <paramref name="start"/>.</summary>
        public static double Elapsed(long start) => (Stopwatch.GetTimestamp() - start) * MsPerTick;

        /// <summary>Adds the milliseconds since <paramref name="start"/> to a component's frame time; returns them.</summary>
        public static double Stop(string component, long start)
        {
            double ms = Elapsed(start);
            Counters.AddTime(component, ms);
            return ms;
        }

        public static double Now => UnityEngine.Time.realtimeSinceStartupAsDouble;
    }
}
