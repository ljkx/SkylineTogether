using System;
using System.Globalization;

namespace SkylineTogether.Core.Follower
{
    /// <summary>
    /// The preview window of one tool-scoped gate (ARCH §6.3, §4.11.4, Appendix D): the entry's systems (for the route
    /// tool <c>WaypointConnectionSystem</c> and <c>RoutePathSystem</c>) run on a client only while
    /// <c>ToolSystem.activeTool</c> is the entry's tool, and for <see cref="ToolScopedGateEntry.LingerSeconds"/> after the
    /// tool was deactivated or last applied, so path results for temp and predicted entities can land. The window never
    /// opens, and an open window closes at once, while the client is not live or the entry's guard (the route guard, whose
    /// failed self-test keeps route previews off) is unhealthy.
    /// <para>
    /// The linger is measured from the last step at which the tool was active or, for the step at which it is first seen
    /// inactive, from that step: the tool was active during part of that frame and may have applied before it switched
    /// away (<c>ToolSystem.applyMode</c> reports <c>m_LastTool</c>, the tool that ran this frame's ToolUpdate, while
    /// <c>activeTool</c> already names the next one, <c>ToolSystem.cs:147-157, 312-320</c>). The window therefore stays
    /// enabled for at least <see cref="ToolScopedGateEntry.LingerSeconds"/> after the tool's last frame, whether its last
    /// act was an apply or a deactivation, and an apply of this tool can never extend it further.
    /// </para>
    /// <para>
    /// game-follower's <c>ToolScopedGates</c> calls <see cref="Step"/> once per frame from <c>ClientClockSystem</c>
    /// (LateUpdate) and flips the systems' <c>Enabled</c> when <see cref="EnabledChanged"/> is set, so a window opened in
    /// frame N takes effect from frame N+1. Main thread only.
    /// </para>
    /// </summary>
    public sealed class PreviewWindow
    {
        private readonly ToolScopedGateEntry _entry;
        private readonly string _toolTypeName;
        private readonly double _lingerSeconds;
        private readonly bool _hasGuard;

        private PreviewWindowState _state;
        private double _lastActiveSeconds = double.NegativeInfinity;

        public PreviewWindow(ToolScopedGateEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (string.IsNullOrEmpty(entry.ToolTypeName)) throw new ArgumentException("the entry names no tool", nameof(entry));
            if (double.IsNaN(entry.LingerSeconds) || double.IsInfinity(entry.LingerSeconds) || entry.LingerSeconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(entry), entry.LingerSeconds, "lingerSeconds must be a finite number of seconds >= 0");
            }
            _entry = entry;
            _toolTypeName = entry.ToolTypeName;
            _lingerSeconds = entry.LingerSeconds;
            _hasGuard = !string.IsNullOrEmpty(entry.Guard);
            _state = PreviewWindowState.Closed;
        }

        /// <summary>The gate this window belongs to.</summary>
        public ToolScopedGateEntry Entry => _entry;

        public PreviewWindowState State => _state;

        /// <summary>
        /// The time the linger is measured from: the last step at which the tool was active, or the step at which it was
        /// first seen inactive after an open window (it was active during part of that frame); negative infinity until the
        /// tool was first seen active.
        /// </summary>
        public double LastActiveSeconds => _lastActiveSeconds;

        /// <summary>True while the scoped systems run (<see cref="PreviewWindowState.Open"/> or
        /// <see cref="PreviewWindowState.Lingering"/>).</summary>
        public bool IsEnabled => _state != PreviewWindowState.Closed;

        /// <summary>The last <see cref="Step"/> or <see cref="Close"/> changed <see cref="IsEnabled"/> (a window edge).</summary>
        public bool EnabledChanged { get; private set; }

        /// <summary>How often the window opened from <see cref="PreviewWindowState.Closed"/> (diagnostics).</summary>
        public int OpenCount { get; private set; }

        /// <summary>Steps that reported an apply of this window's tool: while it was active, or in the step it was first
        /// seen inactive (diagnostics; with the window closed by liveness or the guard, applies are not counted).</summary>
        public int AppliesSeen { get; private set; }

        /// <summary>Optional log for window edges (category <see cref="LogCategory.Gate"/>); null keeps the window silent.</summary>
        public ILog Log { get; set; }

        /// <summary>
        /// Advances the window to <paramref name="now"/> and returns whether the scoped systems are enabled.
        /// </summary>
        /// <param name="now">Monotonic time in seconds (<see cref="IClock.Seconds"/>). A non-finite value closes the window.
        /// A value below <see cref="LastActiveSeconds"/> (a clock that is not monotonic) rebases the linger on it, so the
        /// window neither closes early nor lingers unboundedly.</param>
        /// <param name="activeToolTypeName">Full type name of <c>ToolSystem.activeTool</c> (null when there is none).</param>
        /// <param name="appliedThisFrame"><c>applyMode == ApplyMode.Apply</c> this frame, as the game layer reads it. It
        /// never extends the window on its own: this tool's apply is reported only while the tool is active or in the step
        /// it is first seen inactive, both of which already restart the linger, and another tool's applies while the
        /// window lingers (a terrain brush applies every frame) must not keep the window, and with it the guard, open.
        /// Used for diagnostics (<see cref="AppliesSeen"/>).</param>
        /// <param name="clientLive">The client is live (not loading, resyncing, catching up or Faulted).</param>
        /// <param name="guardHealthy">Health of the entry's guard; ignored when the entry names no guard.</param>
        public bool Step(double now, string activeToolTypeName, bool appliedThisFrame, bool clientLive, bool guardHealthy)
        {
            bool wasEnabled = _state != PreviewWindowState.Closed;
            EnabledChanged = false;

            if (double.IsNaN(now) || double.IsInfinity(now))
            {
                SetClosed(wasEnabled, ClosedReason.InvalidTime);
                return false;
            }
            if (!clientLive || (_hasGuard && !guardHealthy))
            {
                // an unhealthy guard is a lasting state (failed self-test): no per-frame work beyond this branch
                SetClosed(wasEnabled, clientLive ? ClosedReason.GuardUnhealthy : ClosedReason.NotLive);
                return false;
            }

            bool active = activeToolTypeName != null && string.Equals(activeToolTypeName, _toolTypeName, StringComparison.Ordinal);
            if (active)
            {
                _lastActiveSeconds = now;
                if (appliedThisFrame) AppliesSeen++;
                if (_state == PreviewWindowState.Closed) OpenCount++;
                _state = PreviewWindowState.Open;
            }
            else if (_state == PreviewWindowState.Open)
            {
                // first step without the tool: it was active during part of this frame (and may have applied before it
                // switched away, which ToolSystem.applyMode reports in this step), so the linger starts now
                _lastActiveSeconds = now;
                if (appliedThisFrame) AppliesSeen++;
                _state = _lingerSeconds > 0 ? PreviewWindowState.Lingering : PreviewWindowState.Closed;
            }
            else if (_state == PreviewWindowState.Lingering)
            {
                if (now < _lastActiveSeconds) _lastActiveSeconds = now;
                if (now - _lastActiveSeconds >= _lingerSeconds) _state = PreviewWindowState.Closed;
            }

            bool enabled = _state != PreviewWindowState.Closed;
            EnabledChanged = enabled != wasEnabled;
            if (EnabledChanged && Log != null) LogEdge(enabled ? "opened (tool active)" : "closed " + Seconds(_lingerSeconds) + " s after the tool's last frame");
            return enabled;
        }

        /// <summary>
        /// Closes the window at once (leaving, resync, Faulted). Returns true when it was enabled, i.e. the caller must
        /// disable the scoped systems.
        /// </summary>
        public bool Close()
        {
            bool wasEnabled = _state != PreviewWindowState.Closed;
            EnabledChanged = false;
            SetClosed(wasEnabled, ClosedReason.Controller);
            return wasEnabled;
        }

        public override string ToString()
        {
            return "preview window " + _toolTypeName + ": " + _state + (_hasGuard ? " (guard " + _entry.Guard + ")" : string.Empty);
        }

        private enum ClosedReason : byte { InvalidTime, NotLive, GuardUnhealthy, Controller }

        /// <summary>Closes the window; the log text is built only at an edge, so a window held closed every frame (client
        /// not live, guard unhealthy) allocates nothing.</summary>
        private void SetClosed(bool wasEnabled, ClosedReason reason)
        {
            _state = PreviewWindowState.Closed;
            EnabledChanged = wasEnabled;
            if (!wasEnabled) return;
            switch (reason)
            {
                case ClosedReason.InvalidTime: LogEdge("closed: invalid time"); break;
                case ClosedReason.NotLive: LogEdge("closed: client not live"); break;
                case ClosedReason.GuardUnhealthy: LogEdge("closed: guard " + _entry.Guard + " unhealthy"); break;
                default: LogEdge("closed: closed by the controller"); break;
            }
        }

        private void LogEdge(string what)
        {
            ILog log = Log;
            if (log == null || !log.IsEnabled(LogLevel.Debug)) return;
            log.Write(LogLevel.Debug, LogCategory.Gate, "preview window " + _toolTypeName + " " + what);
        }

        private static string Seconds(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
