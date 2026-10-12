using System.Diagnostics;
using SkylineTogether.Core.Diagnostics;

namespace SkylineTogether.Diagnostics
{
    /// <summary>
    /// w2/ids: the mod's per-frame timings and channel byte rates (<see cref="PerfCounters"/>), shown in
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

        // channel names (bytes per second, host: summed over the peers it was sent to)
        public const string StructureBytes = "structure";
        public const string DeferredBytes = "values";
        public const string GlobalBytes = "global";
        public const string IntegrityBytes = "integrity";
        public const string MoverBytes = "movers";
        public const string MotionBytes = "motion";
        public const string AgentBytes = "agents";
        public const string ProposalBytes = "proposals";
        public const string ReceivedBytes = "received";

        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

        public static PerfCounters Counters { get; } = new PerfCounters();

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
