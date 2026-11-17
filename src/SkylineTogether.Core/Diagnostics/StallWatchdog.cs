using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace SkylineTogether.Core.Diagnostics
{
    /// <summary>
    /// Main-thread stall detector. The main thread calls <see cref="Beat"/> once per frame and marks the step it is in with
    /// <see cref="Enter"/> / <see cref="Exit"/>; a background thread notices when no frame finished for
    /// <see cref="StallSeconds"/> and logs which Skyline Together step was running (or the last one that ran, when the
    /// stall is outside the mod), then again every <see cref="RepeatSeconds"/> while it lasts, and once when frames resume.
    /// Joined players saw the game freeze for minutes with one core busy and nothing in any log; this names the step.
    /// <para>
    /// The background thread only reads fields the main thread publishes (no ECS access, no game API); the log is
    /// thread-safe (<see cref="ILog"/>). The decision itself is <see cref="Check"/>, pure (time passed in), for tests.
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class StallWatchdog : IDisposable
    {
        /// <summary>Seconds without a finished frame before the first line.</summary>
        public double StallSeconds = 5.0;

        /// <summary>Seconds between the lines while a stall lasts.</summary>
        public double RepeatSeconds = 10.0;

        private static readonly double SecondsPerTick = 1.0 / Stopwatch.Frequency;

        private readonly object _lock = new object();
        private readonly ILog _log;
        private readonly bool _background;
        private Thread _thread;
        private bool _disposed;

        // published by the main thread
        private long _beatTicks;
        private int _active;
        private string _section;
        private long _sectionTicks;
        private string _lastSection;
        private long _frames;

        // the checker's own state
        private bool _stalled;
        private long _stallStartTicks;
        private long _lastReportTicks;

        /// <param name="log">Where the lines go (thread-safe).</param>
        /// <param name="background">False: no background thread (tests call <see cref="Check"/> themselves).</param>
        public StallWatchdog(ILog log, bool background = true)
        {
            _log = log;
            _background = background;
        }

        /// <summary>Stalls reported so far (diagnostics, tests).</summary>
        public long Stalls { get; private set; }

        /// <summary>The step marked now, or null between steps.</summary>
        public string Section => Volatile.Read(ref _section);

        public static long Now => Stopwatch.GetTimestamp();

        /// <summary>Main thread, once per frame. <paramref name="active"/>: a session runs (no lines outside one).</summary>
        public void Beat(bool active)
        {
            Interlocked.Exchange(ref _beatTicks, Now);
            Interlocked.Increment(ref _frames);
            Volatile.Write(ref _active, active ? 1 : 0);
            if (active) EnsureThread();
        }

        /// <summary>Main thread: marks the start of a step; returns the step it interrupts (pass it to <see cref="Exit"/>).</summary>
        public string Enter(string section)
        {
            string previous = Volatile.Read(ref _section);
            Interlocked.Exchange(ref _sectionTicks, Now);
            Volatile.Write(ref _section, section);
            return previous;
        }

        /// <summary>Main thread: the step ended; <paramref name="previous"/> is what <see cref="Enter"/> returned.</summary>
        public void Exit(string previous)
        {
            string current = Volatile.Read(ref _section);
            if (current != null) Volatile.Write(ref _lastSection, current);
            Interlocked.Exchange(ref _sectionTicks, Now);
            Volatile.Write(ref _section, previous);
        }

        /// <summary>
        /// The decision of one check at <paramref name="nowTicks"/>: the line to log, or null. Called by the background
        /// thread every half second; pure apart from its own stall state.
        /// </summary>
        public string Check(long nowTicks, out bool warning)
        {
            warning = false;
            long beat = Interlocked.Read(ref _beatTicks);
            if (beat == 0) return null;
            double since = (nowTicks - beat) * SecondsPerTick;
            if (Volatile.Read(ref _active) == 0)
            {
                _stalled = false;
                return null;
            }
            if (since < StallSeconds)
            {
                if (!_stalled) return null;
                _stalled = false;
                double lasted = (beat - _stallStartTicks) * SecondsPerTick;
                return "[stall] main thread resumed; the frame took about " + Seconds(lasted + since) + " s";
            }
            if (_stalled && (nowTicks - _lastReportTicks) * SecondsPerTick < RepeatSeconds) return null;
            if (!_stalled)
            {
                _stalled = true;
                _stallStartTicks = beat;
                Stalls++;
            }
            _lastReportTicks = nowTicks;
            warning = true;
            string section = Volatile.Read(ref _section);
            double inSection = (nowTicks - Interlocked.Read(ref _sectionTicks)) * SecondsPerTick;
            string where = section != null
                ? "in Skyline Together step '" + section + "' for " + Seconds(inSection) + " s"
                : "outside Skyline Together's marked steps (last step: '" + (Volatile.Read(ref _lastSection) ?? "none") + "', ended " + Seconds(inSection) + " s ago)";
            return "[stall] main thread has not finished a frame for " + Seconds(since) + " s; " + where + "; frames so far "
                   + Interlocked.Read(ref _frames).ToString(CultureInfo.InvariantCulture);
        }

        private static string Seconds(double s) => s.ToString("0.0", CultureInfo.InvariantCulture);

        private void EnsureThread()
        {
            if (_thread != null || !_background) return;
            lock (_lock)
            {
                if (_thread != null || _disposed) return;
                _thread = new Thread(Run) { IsBackground = true, Name = "Skyline Together stall watchdog" };
                _thread.Start();
            }
        }

        private void Run()
        {
            while (true)
            {
                lock (_lock)
                {
                    if (_disposed) return;
                    Monitor.Wait(_lock, 500);
                    if (_disposed) return;
                }
                try
                {
                    string line = Check(Now, out bool warning);
                    if (line == null) continue;
                    if (warning) _log?.Warn(LogCategory.Diagnostics, line);
                    else _log?.Info(LogCategory.Diagnostics, line);
                }
                catch (Exception e) when (!(e is OutOfMemoryException) && !(e is ThreadAbortException))
                {
                    // a diagnostics thread never takes the game down
                }
            }
        }

        public void Dispose()
        {
            Thread thread;
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                thread = _thread;
                Monitor.PulseAll(_lock);
            }
            if (thread != null && thread != Thread.CurrentThread) thread.Join(1000);
        }
    }
}
