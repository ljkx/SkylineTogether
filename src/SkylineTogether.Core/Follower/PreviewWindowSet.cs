using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Follower
{
    /// <summary>
    /// All tool-scoped gates of a session (gates.json <c>toolScoped</c> plus the adapters' entries): the Core side of
    /// game-follower's <c>ToolScopedGates</c> and its <c>IToolScopedGateState</c> (ARCH §6.3, §4.11.4). It keeps one
    /// <see cref="PreviewWindow"/> per entry and the health of each guard, and derives the enabled state of every scoped
    /// system as the OR of the windows that scope it: a system scoped by two tools (for example
    /// <c>WaypointConnectionSystem</c> for the route tool and a mod tool) stays enabled while either window is enabled, so
    /// a closing window must not disable it. <see cref="Step"/> returns the edges; the game layer only flips <c>Enabled</c>.
    /// <para>
    /// Every scoped system starts disabled (it is off on clients outside its windows), and a guard is unhealthy until
    /// <see cref="ReportGuardHealth"/> says otherwise, so windows of a guard whose self-test never ran stay closed (fail
    /// closed: the client then draws lines with straight previews, ARCH §4.11.4). Main thread only.
    /// </para>
    /// </summary>
    public sealed class PreviewWindowSet
    {
        private readonly List<PreviewWindow> _windows = new List<PreviewWindow>();
        private readonly Dictionary<string, bool> _guardHealth = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly List<string> _systems = new List<string>();
        private readonly Dictionary<string, int> _systemIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<List<int>> _windowsOfSystem = new List<List<int>>();
        private readonly List<bool> _enabled = new List<bool>();
        private readonly List<string> _toEnable = new List<string>();
        private readonly List<string> _toDisable = new List<string>();
        private readonly IReadOnlyList<PreviewWindow> _windowsView;
        private readonly IReadOnlyList<string> _systemsView;
        private readonly IReadOnlyList<string> _toEnableView;
        private readonly IReadOnlyList<string> _toDisableView;
        private ILog _log;

        public PreviewWindowSet()
        {
            // cached views: the game layer reads them every frame after Step, which must not allocate
            _windowsView = _windows.AsReadOnly();
            _systemsView = _systems.AsReadOnly();
            _toEnableView = _toEnable.AsReadOnly();
            _toDisableView = _toDisable.AsReadOnly();
        }

        /// <summary>Creates the set with <paramref name="entries"/> (null = none); see <see cref="Add"/>.</summary>
        public PreviewWindowSet(IEnumerable<ToolScopedGateEntry> entries) : this()
        {
            if (entries == null) return;
            foreach (ToolScopedGateEntry entry in entries) Add(entry);
        }

        /// <summary>
        /// Adds a gate (adapter entries arrive through <c>IGateRegistry.AddToolScoped</c>). Its systems start disabled.
        /// Several entries may name the same tool or the same system.
        /// </summary>
        /// <exception cref="ArgumentNullException">A null entry.</exception>
        /// <exception cref="ArgumentException">An entry without a tool or with an invalid linger (see <see cref="PreviewWindow"/>).</exception>
        public void Add(ToolScopedGateEntry entry)
        {
            var window = new PreviewWindow(entry) { Log = _log };
            int windowIndex = _windows.Count;
            _windows.Add(window);
            if (entry.Systems == null) return;
            foreach (string system in entry.Systems)
            {
                if (string.IsNullOrEmpty(system)) continue;
                if (!_systemIndex.TryGetValue(system, out int index))
                {
                    index = _systems.Count;
                    _systemIndex.Add(system, index);
                    _systems.Add(system);
                    _windowsOfSystem.Add(new List<int>(2));
                    _enabled.Add(false);
                }
                if (!_windowsOfSystem[index].Contains(windowIndex)) _windowsOfSystem[index].Add(windowIndex);
            }
        }

        /// <summary>The windows, one per added entry, in the order they were added.</summary>
        public IReadOnlyList<PreviewWindow> Windows => _windowsView;

        /// <summary>Every scoped system, each once, in the order first added. The caller disables all of them when the
        /// gates are applied (off outside windows) and flips them only at the edges afterwards.</summary>
        public IReadOnlyList<string> Systems => _systemsView;

        /// <summary>Systems to set <c>Enabled = true</c> after the last <see cref="Step"/> or <see cref="CloseAll"/>.</summary>
        public IReadOnlyList<string> SystemsToEnable => _toEnableView;

        /// <summary>Systems to set <c>Enabled = false</c> after the last <see cref="Step"/> or <see cref="CloseAll"/>.</summary>
        public IReadOnlyList<string> SystemsToDisable => _toDisableView;

        /// <summary>Optional log for window edges and guard changes (category <see cref="LogCategory.Gate"/>).</summary>
        public ILog Log
        {
            get => _log;
            set
            {
                _log = value;
                foreach (PreviewWindow window in _windows) window.Log = value;
            }
        }

        /// <summary>True while any window is open or lingering (the route guard is active only then).</summary>
        public bool AnyWindowOpenOrLingering
        {
            get
            {
                foreach (PreviewWindow window in _windows)
                {
                    if (window.IsEnabled) return true;
                }
                return false;
            }
        }

        /// <summary>True while a window guarded by <paramref name="guardName"/> is open or lingering.</summary>
        public bool IsWindowOpen(string guardName)
        {
            if (string.IsNullOrEmpty(guardName)) return false;
            foreach (PreviewWindow window in _windows)
            {
                if (window.IsEnabled && string.Equals(window.Entry.Guard, guardName, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// Records a guard's health (its self-test result, or a later failure). An unhealthy guard keeps its windows
        /// closed and closes open ones at the next <see cref="Step"/>.
        /// </summary>
        public void ReportGuardHealth(string guardName, bool healthy)
        {
            if (string.IsNullOrEmpty(guardName)) return;
            bool known = _guardHealth.TryGetValue(guardName, out bool previous);
            _guardHealth[guardName] = healthy;
            if (known && previous == healthy) return;
            ILog log = _log;
            if (log != null)
            {
                LogLevel level = healthy ? LogLevel.Info : LogLevel.Warn;
                if (log.IsEnabled(level)) log.Write(level, LogCategory.Gate, "guard " + guardName + (healthy ? " healthy" : " unhealthy: its preview windows stay closed"));
            }
        }

        /// <summary>The last reported health of a guard; false until reported. An empty name (no guard) is healthy.</summary>
        public bool IsGuardHealthy(string guardName)
        {
            if (string.IsNullOrEmpty(guardName)) return true;
            return _guardHealth.TryGetValue(guardName, out bool healthy) && healthy;
        }

        /// <summary>True when the system is currently enabled by an open or lingering window.</summary>
        public bool IsSystemEnabled(string systemTypeName)
        {
            return systemTypeName != null && _systemIndex.TryGetValue(systemTypeName, out int index) && _enabled[index];
        }

        /// <summary>
        /// Steps every window (once per frame, LateUpdate) and returns true when some scoped system changed state; the
        /// changes are in <see cref="SystemsToEnable"/> and <see cref="SystemsToDisable"/>. Allocation-free.
        /// </summary>
        /// <param name="now">Monotonic time in seconds.</param>
        /// <param name="activeToolTypeName">Full type name of <c>ToolSystem.activeTool</c>.</param>
        /// <param name="appliedThisFrame"><c>activeTool.applyMode == ApplyMode.Apply</c>.</param>
        /// <param name="clientLive">The client is live.</param>
        public bool Step(double now, string activeToolTypeName, bool appliedThisFrame, bool clientLive)
        {
            for (int i = 0; i < _windows.Count; i++)
            {
                PreviewWindow window = _windows[i];
                window.Step(now, activeToolTypeName, appliedThisFrame, clientLive, IsGuardHealthy(window.Entry.Guard));
            }
            return Recompute();
        }

        /// <summary>Closes every window at once (leaving, resync, Faulted); returns true when systems must be disabled.</summary>
        public bool CloseAll()
        {
            foreach (PreviewWindow window in _windows) window.Close();
            return Recompute();
        }

        public override string ToString()
        {
            int enabled = 0;
            foreach (bool on in _enabled)
            {
                if (on) enabled++;
            }
            return "tool-scoped gates: " + _windows.Count + " window(s), " + enabled + "/" + _systems.Count + " system(s) enabled";
        }

        private bool Recompute()
        {
            _toEnable.Clear();
            _toDisable.Clear();
            for (int s = 0; s < _systems.Count; s++)
            {
                bool wanted = false;
                List<int> windows = _windowsOfSystem[s];
                for (int w = 0; w < windows.Count && !wanted; w++) wanted = _windows[windows[w]].IsEnabled;
                if (wanted == _enabled[s]) continue;
                _enabled[s] = wanted;
                (wanted ? _toEnable : _toDisable).Add(_systems[s]);
            }
            return _toEnable.Count > 0 || _toDisable.Count > 0;
        }
    }
}
