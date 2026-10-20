using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using SkylineTogether.Core;
using SkylineTogether.Core.Diagnostics;
using ILog = SkylineTogether.Core.ILog;

namespace SkylineTogether.Diagnostics
{
    /// <summary>
    /// P-JIT (ARCHITECTURE.md#probes-and-self-test): compiles every method of the game layer once, on a background thread
    /// after <c>OnLoad</c>, with <see cref="JitSelfTest"/>. A method that uses a game member which a game update removed
    /// or changed fails to compile; its feature is then switched off at startup (a critical feature blocks hosting and
    /// joining with the reason) instead of the method throwing in the middle of a session. Nothing is executed, only
    /// compiled; the run is bounded (<see cref="BudgetSeconds"/>) and runs below normal priority, so it costs a few
    /// seconds of one background core once per game start. <see cref="Probes"/> picks the result up on the main thread.
    /// </summary>
    public static class JitProbe
    {
        /// <summary>The longest the background compilation may take; the rest is left to the runtime as usual.</summary>
        public const double BudgetSeconds = 60;

        // type name prefixes of the game layer -> the feature whose code they are (first match wins; default replication)
        private static readonly (string Prefix, FeatureHealth Feature)[] FeatureByType =
        {
            ("SkylineTogether.Sync.Agent", Features.Agents),
            ("SkylineTogether.Sync.FlowStatus", Features.FlowStatus),
            ("SkylineTogether.Sync.Event", Features.Events),
            ("SkylineTogether.Sync.Chirp", Features.Events),
            ("SkylineTogether.Sync.Mover", Features.Movers),
            ("SkylineTogether.Sync.Terrain", Features.Terrain),
            ("SkylineTogether.Sync.Water", Features.Water),
            ("SkylineTogether.Sync.Statistics", Features.SimValues),
            ("SkylineTogether.Sync.SimValue", Features.SimValues),
            ("SkylineTogether.Sync.CellMap", Features.SimValues),
            ("SkylineTogether.Sync.Integrity", Features.Integrity),
            ("SkylineTogether.Sync.Icon", Features.Icons),
            ("SkylineTogether.Sync.GlobalState", Features.GlobalState),
            ("SkylineTogether.Sync.ClientGate", Features.Gate),
            ("SkylineTogether.Sync.ViewerGate", Features.Gate),
            ("SkylineTogether.Sync.FollowerClock", Features.Clock),
            ("SkylineTogether.Sync.Action", Features.Actions),
            ("SkylineTogether.Presence.ToolPreview", Features.Previews),
            ("SkylineTogether.Presence.", Features.Presence),
            ("SkylineTogether.Compat.", Features.ModCompat),
            ("SkylineTogether.Net.", Features.Steam),
            ("SkylineTogether.Session.SteamInvite", Features.Invites),
            ("SkylineTogether.Session.", Features.Session),
            ("SkylineTogether.Systems.Speed", Features.Speed),
            ("SkylineTogether.UI.", Features.Ui),
        };

        private static readonly object Lock = new object();
        private static Thread _thread;
        private static volatile bool _done;
        private static volatile bool _stop;
        private static JitSelfTest.Result _result;
        private static List<JitSelfTest.Failure> _loadFailures;
        private static bool _reported;

        /// <summary>Starts the background run once per game start (later calls do nothing).</summary>
        public static void Start(ILog log)
        {
            lock (Lock)
            {
                if (_thread != null) return;
                _thread = new Thread(() => RunGuarded(log)) { IsBackground = true, Name = "SkylineTogether JIT self-test", Priority = ThreadPriority.BelowNormal };
            }
            _thread.Start();
        }

        /// <summary>Asks a running compilation to stop (the game is quitting).</summary>
        public static void Stop() => _stop = true;

        private static void RunGuarded(ILog log)
        {
            try
            {
                var loadFailures = new List<JitSelfTest.Failure>();
                Assembly assembly = typeof(JitProbe).Assembly;
                List<Type> types = JitSelfTest.TypesOf(assembly, loadFailures);
                var methods = JitSelfTest.PreparableMethods(types, loadFailures);
                JitSelfTest.Result result = JitSelfTest.Run(methods, JitSelfTest.PrepareWithRuntime, JitSelfTest.WallClock(), BudgetSeconds, () => _stop);
                lock (Lock)
                {
                    _result = result;
                    _loadFailures = loadFailures;
                }
                log?.Info(LogCategory.Probe, "P-JIT: " + JitSelfTest.Summary(result) + (loadFailures.Count > 0 ? ", " + loadFailures.Count + " type(s) failed to load" : ""));
            }
            catch (Exception ex)
            {
                // the self-test itself must never hurt the game: it just reports nothing
                log?.Warn(LogCategory.Probe, "P-JIT: the compilation self-test could not run: " + ex.Message);
            }
            finally
            {
                _done = true;
            }
        }

        /// <summary>
        /// Main thread (Probes): true once, when the background run has finished and its results should be reported
        /// (the probe report is then refreshed).
        /// </summary>
        public static bool TakeCompletion()
        {
            if (!_done || _reported) return false;
            _reported = true;
            return true;
        }

        /// <summary>The probe results of the finished run: one failed probe per broken method or type (none while running).</summary>
        public static List<ProbeResult> Results()
        {
            var results = new List<ProbeResult>();
            List<JitSelfTest.Failure> failures;
            lock (Lock)
            {
                if (_result == null && _loadFailures == null) return results;
                failures = new List<JitSelfTest.Failure>(_loadFailures ?? new List<JitSelfTest.Failure>());
                if (_result != null) failures.AddRange(_result.Failures);
            }
            foreach (JitSelfTest.Failure f in failures)
            {
                FeatureHealth feature = FeatureOf(f.Type);
                results.Add(new ProbeResult
                {
                    Id = "P-JIT",
                    Feature = feature.Id,
                    Target = f.Type + (f.Method != null ? "." + f.Method : ""),
                    Ok = false,
                    // only a binding failure (missing member, type or assembly) switches the feature off; any other compiler
                    // complaint may be a runtime quirk and is reported as a warning
                    WarningOnly = !f.Binding,
                    Detail = f.Binding
                        ? "does not compile against this game version (" + f.Error + "); " + feature.Id + " is switched off"
                        : "could not be compiled ahead of use (" + f.Error + ")",
                });
            }
            return results;
        }

        /// <summary>The feature a game-layer type belongs to (by its namespace and name).</summary>
        public static FeatureHealth FeatureOf(string typeName)
        {
            if (typeName != null)
            {
                foreach ((string prefix, FeatureHealth feature) in FeatureByType)
                {
                    if (typeName.StartsWith(prefix, StringComparison.Ordinal)) return feature;
                }
            }
            return Features.Replication;
        }
    }
}
