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
    /// P-REFS (ARCHITECTURE.md#probes-and-self-test): resolves every type and member reference of the game layer and of
    /// Core once, on a background thread after <c>OnLoad</c>, with <see cref="ReferenceCheck"/> (what <c>tools/ApiCheck</c>
    /// checks offline, against the assemblies the game actually loaded). A reference a game update removed or changed is
    /// mapped to the feature whose code uses it (by the using type's namespace and name, <see cref="FeatureOf"/>); that
    /// feature is switched off at startup (a critical feature blocks hosting and joining with the reason) instead of the
    /// method throwing <c>MissingMethodException</c> in the middle of a session. Nothing of the mod runs and no static
    /// constructor is triggered (resolving a reference only loads metadata); the run is bounded
    /// (<see cref="BudgetSeconds"/>) and normally takes well under a second. <see cref="Probes"/> picks the result up on
    /// the main thread.
    /// </summary>
    public static class ReferenceProbe
    {
        /// <summary>The longest the background check may take; what is left unchecked then is reported in the log.</summary>
        public const double BudgetSeconds = 30;

        // type name prefixes of the mod -> the feature whose code they are (first match wins; default replication)
        private static readonly PrefixMap<FeatureHealth> FeatureByType = new PrefixMap<FeatureHealth>(new (string, FeatureHealth)[]
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
        }, Features.Replication);

        private static readonly object Lock = new object();
        private static Thread _thread;
        private static volatile bool _done;
        private static volatile bool _stop;
        private static List<ReferenceCheck.Failure> _failures;
        private static bool _reported;

        /// <summary>Starts the background run once per game start (later calls do nothing).</summary>
        public static void Start(ILog log)
        {
            lock (Lock)
            {
                if (_thread != null) return;
                _thread = new Thread(() => RunGuarded(log)) { IsBackground = true, Name = "SkylineTogether reference check", Priority = ThreadPriority.BelowNormal };
            }
            _thread.Start();
        }

        /// <summary>Asks a running check to stop (the game is quitting).</summary>
        public static void Stop() => _stop = true;

        private static void RunGuarded(ILog log)
        {
            try
            {
                var failures = new List<ReferenceCheck.Failure>();
                Func<double> clock = ReferenceCheck.WallClock();
                double start = clock();
                foreach (Assembly assembly in new[] { typeof(ReferenceProbe).Assembly, typeof(ReferenceCheck).Assembly })
                {
                    List<Type> types = ReferenceCheck.TypesOf(assembly, failures);
                    double left = BudgetSeconds - (clock() - start);
                    ReferenceCheck.Result result = ReferenceCheck.Run(assembly.ManifestModule, clock, left, () => _stop, types);
                    failures.AddRange(result.Failures);
                    log?.Info(LogCategory.Probe, "P-REFS " + assembly.GetName().Name + ": " + ReferenceCheck.Summary(result));
                    foreach (ReferenceCheck.Failure f in result.Failures) log?.Warn(LogCategory.Probe, "P-REFS: " + f);
                    if (result.TimedOut || result.Cancelled) break;
                }
                lock (Lock) _failures = failures;
            }
            catch (Exception ex)
            {
                // the check itself must never hurt the game: it just reports nothing
                log?.Warn(LogCategory.Probe, "P-REFS: the reference check could not run: " + ex.Message);
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

        /// <summary>The probe results of the finished run: one failed probe per missing reference and user (none while running).</summary>
        public static List<ProbeResult> Results()
        {
            var results = new List<ProbeResult>();
            List<ReferenceCheck.Failure> failures;
            lock (Lock)
            {
                if (_failures == null) return results;
                failures = new List<ReferenceCheck.Failure>(_failures);
            }
            foreach (ReferenceCheck.Failure f in failures)
            {
                // a reference no code of the mod uses has no feature to switch off: a warning
                bool attributed = f.User != null;
                FeatureHealth feature = FeatureOf(f.User);
                results.Add(new ProbeResult
                {
                    Id = "P-REFS",
                    Feature = feature.Id,
                    Target = (f.User ?? f.Reference) + (f.Method != null ? "." + f.Method : ""),
                    Ok = false,
                    // only a binding failure (missing member, type or assembly) of code the mod runs switches the feature off;
                    // anything else may be a quirk of the runtime and is reported as a warning
                    WarningOnly = !f.Binding || !attributed,
                    Detail = f.Binding && attributed
                        ? "uses something this game version does not have (" + f.Error + "); " + feature.Id + " is switched off"
                        : "a reference could not be resolved (" + f.Error + ")",
                });
            }
            return results;
        }

        /// <summary>The feature a type of the mod belongs to (by its namespace and name).</summary>
        public static FeatureHealth FeatureOf(string typeName) => FeatureByType.Of(typeName);
    }
}
