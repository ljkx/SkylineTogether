using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using SkylineTogether.Core;
using SkylineTogether.Core.Diagnostics;
using ILog = SkylineTogether.Core.ILog;

namespace SkylineTogether.Diagnostics
{
    /// <summary>
    /// P-REFS (ARCHITECTURE.md#probes-and-self-test): checks once, on a background thread after <c>OnLoad</c>, with
    /// <see cref="ReferenceCheck"/>, that the game layer and Core still bind to the game the player runs (what
    /// <c>tools/ApiCheck</c> checks offline, here against the assemblies the game actually loaded):
    /// <list type="bullet">
    /// <item>every type and member reference resolves;</item>
    /// <item>every type of the mod loads (the game base classes and interfaces it uses still exist) and can be set up (it
    /// still implements every member of the game interfaces and abstract classes it derives from; Mono checks that only
    /// when a type is first used);</item>
    /// <item>when something failed: which method of which type uses it.</item>
    /// </list>
    /// Each failure is attributed to the mod's type (and method) whose code uses it, and <see cref="ModFeatureMap"/> names
    /// the feature(s) that code belongs to; those features are switched off at startup (a critical one blocks hosting and
    /// joining with the reason) instead of the code throwing <c>MissingMethodException</c> or <c>TypeLoadException</c> in
    /// the middle of a session. A failure in code of no feature (diagnostics, solo testing), in a type the map does not
    /// know, or in no code at all is a warning and switches nothing off. Nothing of the mod runs and no static constructor
    /// is triggered (reflection only loads metadata); the run is bounded (<see cref="BudgetSeconds"/>) and normally takes
    /// well under a second. <see cref="Probes"/> picks the result up on the main thread.
    /// </summary>
    public static class ReferenceProbe
    {
        /// <summary>The longest the background check may take; what is left unchecked then is reported in the log.</summary>
        public const double BudgetSeconds = 30;

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
            }
            // the DLL files, found on the main thread (the game's asset database); read only when a type does not load
            string ownPath = OwnDllPath();
            string coreDir = ownPath == null ? null : System.IO.Path.GetDirectoryName(ownPath);
            string corePath = FileOrNull(SafeLocation(typeof(ReferenceCheck).Assembly))
                              ?? (coreDir == null ? null : FileOrNull(System.IO.Path.Combine(coreDir, "SkylineTogether.Core.dll")));
            lock (Lock)
            {
                if (_thread != null) return;
                _thread = new Thread(() => RunGuarded(log, ownPath, corePath)) { IsBackground = true, Name = "SkylineTogether reference check", Priority = ThreadPriority.BelowNormal };
            }
            _thread.Start();
        }

        /// <summary>Asks a running check to stop (the game is quitting).</summary>
        public static void Stop() => _stop = true;

        // the game layer's DLL: the assembly's location, else the file of the mod manager's asset (a game that loads
        // mods from bytes); null when neither is a file
        private static string OwnDllPath()
        {
            string location = FileOrNull(SafeLocation(typeof(ReferenceProbe).Assembly));
            if (location != null) return location;
            try
            {
                return FileOrNull(InstallInfo.OwnAsset()?.path);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string SafeLocation(Assembly assembly)
        {
            try
            {
                return assembly.Location;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string FileOrNull(string path)
        {
            try
            {
                return !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void RunGuarded(ILog log, string ownPath, string corePath)
        {
            try
            {
                var failures = new List<ReferenceCheck.Failure>();
                Func<double> clock = ReferenceCheck.WallClock();
                double start = clock();
                foreach ((Assembly assembly, string path) in new[] { (typeof(ReferenceProbe).Assembly, ownPath), (typeof(ReferenceCheck).Assembly, corePath) })
                {
                    var typeFailures = new List<ReferenceCheck.Failure>();
                    List<Type> types = ReferenceCheck.TypesOf(assembly, typeFailures, path == null ? (Func<byte[]>)null : () => File.ReadAllBytes(path));
                    failures.AddRange(typeFailures);
                    double left = BudgetSeconds - (clock() - start);
                    ReferenceCheck.Result result = ReferenceCheck.Run(assembly.ManifestModule, clock, left, () => _stop, types);
                    failures.AddRange(result.Failures);
                    log?.Info(LogCategory.Probe, "P-REFS " + assembly.GetName().Name + ": " + ReferenceCheck.Summary(result)
                                                 + (typeFailures.Count > 0 ? "; " + typeFailures.Count + " type(s) do not load" : ""));
                    foreach (ReferenceCheck.Failure f in typeFailures) log?.Warn(LogCategory.Probe, "P-REFS: " + f);
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

        /// <summary>
        /// The probe results of the finished run (none while running): per failure, one failed probe for each feature its
        /// code belongs to, or one warning (see the class summary).
        /// </summary>
        public static List<ProbeResult> Results()
        {
            var results = new List<ProbeResult>();
            List<ReferenceCheck.Failure> failures;
            lock (Lock)
            {
                if (_failures == null) return results;
                failures = new List<ReferenceCheck.Failure>(_failures);
            }
            foreach (ReferenceCheck.Failure f in failures) results.AddRange(ResultsOf(f));
            return results;
        }

        private static List<ProbeResult> ResultsOf(ReferenceCheck.Failure f)
        {
            var results = new List<ProbeResult>();
            string target = (f.User ?? f.Reference) + (f.Method != null ? "." + f.Method : "");
            List<FeatureHealth> features = FeaturesOf(f.User, f.Method, out string why);
            // only a binding failure (missing member, type or assembly) in code of a feature switches it off; anything else
            // may be a quirk of the runtime, and code of no feature has nothing to switch off: warnings
            if (!f.Binding || features.Count == 0)
            {
                results.Add(new ProbeResult
                {
                    Id = "P-REFS",
                    Target = target,
                    Ok = false,
                    WarningOnly = true,
                    Detail = (f.Binding ? "uses something this game version does not have (" : "a reference could not be resolved (") + f.Error + "); "
                             + (why ?? "not a missing member, nothing is switched off"),
                });
                return results;
            }
            foreach (FeatureHealth feature in features)
            {
                results.Add(new ProbeResult
                {
                    Id = "P-REFS",
                    Feature = feature.Id,
                    Target = target,
                    Ok = false,
                    Detail = "uses something this game version does not have (" + f.Error + "); " + feature.Id + " is switched off",
                });
            }
            return results;
        }

        /// <summary>
        /// The features that code of <paramref name="typeName"/> (and <paramref name="method"/>) belongs to
        /// (<see cref="ModFeatureMap"/>); empty when there is none, and <paramref name="why"/> then says why (for the
        /// warning): no code uses the reference, diagnostics or solo-testing code, or a type the map does not know.
        /// </summary>
        public static List<FeatureHealth> FeaturesOf(string typeName, string method, out string why)
        {
            var features = new List<FeatureHealth>();
            why = null;
            if (typeName == null)
            {
                why = "no code of Skyline Together uses it, nothing is switched off";
                return features;
            }
            string[] ids = ModFeatureMap.Of(typeName, method);
            if (ids == null)
            {
                why = typeName + " is not in the feature map (please report this), nothing is switched off";
                return features;
            }
            foreach (string id in ids)
            {
                FeatureHealth feature = Features.Registry.Get(id);
                if (feature != null) features.Add(feature);
            }
            if (features.Count == 0)
            {
                why = ids.Length == 0 ? "diagnostics or solo-testing code, nothing is switched off" : "unknown feature " + string.Join(", ", ids) + ", nothing is switched off";
            }
            return features;
        }
    }
}
