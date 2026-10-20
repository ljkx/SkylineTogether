using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SkylineTogether.Core.Diagnostics
{
    /// <summary>What <see cref="FeatureHealth.ReportFailure"/> asks the caller to do.</summary>
    public struct FailureVerdict
    {
        /// <summary>Write this failure to the log (the first one with its stack, then at most one line per interval).</summary>
        public bool Log;

        /// <summary>Failures of the feature not logged since the last logged one (to mention in that line).</summary>
        public int Suppressed;

        /// <summary>This failure turned the feature off (tell the player once).</summary>
        public bool JustTripped;
    }

    /// <summary>
    /// The health of one feature of the mod (ARCHITECTURE.md#feature-health-and-guards): whether its startup probes passed and whether it
    /// keeps failing at run time. Every entry point the game calls reports its exceptions here; the feature logs with a
    /// rate limit and turns itself off after <see cref="TripCount"/> failures within <see cref="TripWindowSeconds"/>,
    /// instead of throwing (or logging) every frame. Callers check <see cref="IsAvailable"/> before doing work.
    /// Pure logic (times are passed in); main thread only.
    /// </summary>
    public sealed class FeatureHealth
    {
        /// <summary>Failures within <see cref="TripWindowSeconds"/> that turn the feature off.</summary>
        public const int TripCount = 5;

        public const double TripWindowSeconds = 30;

        /// <summary>At most one log line per feature in this many seconds after the first failure.</summary>
        public const double LogIntervalSeconds = 10;

        private readonly double[] _recent = new double[TripCount];
        private int _recentCount;
        private int _recentNext;
        private double _lastLog = double.NegativeInfinity;
        private int _suppressed;

        public FeatureHealth(string id, string title, bool critical)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Title = title ?? id;
            Critical = critical;
        }

        /// <summary>Short stable name (log, diagnostics).</summary>
        public string Id { get; }

        /// <summary>What the player reads ("icon filter", "terrain sync").</summary>
        public string Title { get; }

        /// <summary>
        /// A critical feature cannot be degraded: a failed probe blocks hosting and joining, and a run-time trip ends the
        /// session (a client then rejoins with a fresh snapshot).
        /// </summary>
        public bool Critical { get; }

        /// <summary>A startup probe failed (<see cref="ProbeReason"/>); stays until the next successful probe.</summary>
        public bool ProbeFailed { get; private set; }

        public string ProbeReason { get; private set; }

        /// <summary>Repeated run-time failures turned the feature off until <see cref="Rearm"/>.</summary>
        public bool Tripped { get; private set; }

        public string TripReason { get; private set; }

        /// <summary>All failures reported so far (diagnostics).</summary>
        public int TotalFailures { get; private set; }

        /// <summary>The last failure (type and message), for the diagnostics dump.</summary>
        public string LastFailure { get; private set; }

        /// <summary>True when the feature may run.</summary>
        public bool IsAvailable => !ProbeFailed && !Tripped;

        /// <summary>Why the feature is off ("" when available).</summary>
        public string UnavailableReason => ProbeFailed ? "self-test failed: " + ProbeReason : Tripped ? "turned off after repeated errors: " + TripReason : "";

        public void SetProbeResult(bool ok, string reason)
        {
            ProbeFailed = !ok;
            ProbeReason = ok ? null : (reason ?? "probe failed");
        }

        /// <summary>Turns a tripped feature on again (a new session starts) and forgets the failure history.</summary>
        public void Rearm()
        {
            Tripped = false;
            TripReason = null;
            _recentCount = 0;
            _recentNext = 0;
            _suppressed = 0;
            _lastLog = double.NegativeInfinity;
        }

        /// <summary>Turns the feature off at once (e.g. a dependency is gone).</summary>
        public bool Trip(string reason)
        {
            if (Tripped) return false;
            Tripped = true;
            TripReason = reason ?? "turned off";
            return true;
        }

        /// <summary>Records one failure at <paramref name="now"/> (seconds) and says whether to log it and whether it tripped the feature.</summary>
        public FailureVerdict ReportFailure(Exception exception, double now)
        {
            TotalFailures++;
            LastFailure = exception != null ? exception.GetType().Name + ": " + exception.Message : "failure";
            var verdict = new FailureVerdict();
            if (now - _lastLog >= LogIntervalSeconds)
            {
                verdict.Log = true;
                verdict.Suppressed = _suppressed;
                _suppressed = 0;
                _lastLog = now;
            }
            else
            {
                _suppressed++;
            }
            if (Tripped) return verdict;
            _recent[_recentNext] = now;
            _recentNext = (_recentNext + 1) % TripCount;
            if (_recentCount < TripCount) _recentCount++;
            if (_recentCount == TripCount)
            {
                double oldest = _recent[_recentNext]; // the slot written TripCount failures ago
                if (now - oldest <= TripWindowSeconds)
                {
                    Tripped = true;
                    TripReason = LastFailure;
                    verdict.JustTripped = true;
                    verdict.Log = true;
                }
            }
            return verdict;
        }

        public override string ToString()
        {
            return Id + ": " + (IsAvailable ? "ok" : UnavailableReason) + (TotalFailures > 0 ? " (" + TotalFailures.ToString(CultureInfo.InvariantCulture) + " errors, last: " + LastFailure + ")" : "");
        }
    }

    /// <summary>One line of the startup self-test (ARCHITECTURE.md#probes-and-self-test).</summary>
    public struct ProbeResult
    {
        /// <summary>Probe id (P-CTX, P-CLOCK, ...).</summary>
        public string Id;

        /// <summary>The feature the probe belongs to.</summary>
        public string Feature;

        /// <summary>What was checked.</summary>
        public string Target;

        public bool Ok;

        /// <summary>A failed probe that is only a warning (the feature still runs, with less fidelity).</summary>
        public bool WarningOnly;

        public string Detail;
    }

    /// <summary>
    /// Every feature's health and the latest self-test report (ARCHITECTURE.md#feature-health-and-guards, #probes-and-self-test): one place for
    /// the log, the diagnostics dump and the one-line summary in the panel.
    /// </summary>
    public sealed class FeatureRegistry
    {
        private readonly List<FeatureHealth> _features = new List<FeatureHealth>();
        private readonly Dictionary<string, FeatureHealth> _byId = new Dictionary<string, FeatureHealth>(StringComparer.Ordinal);
        private readonly List<ProbeResult> _results = new List<ProbeResult>();

        public IReadOnlyList<FeatureHealth> Features => _features;

        public IReadOnlyList<ProbeResult> Results => _results;

        /// <summary>When the report was last produced ("startup", "city loaded", ...).</summary>
        public string ReportStage { get; private set; } = "";

        /// <summary>Bumped whenever the report or a feature's availability changes (UI refresh key).</summary>
        public int Version { get; private set; }

        public FeatureHealth Add(string id, string title, bool critical)
        {
            if (_byId.TryGetValue(id, out FeatureHealth existing)) return existing;
            var f = new FeatureHealth(id, title, critical);
            _features.Add(f);
            _byId.Add(id, f);
            return f;
        }

        public FeatureHealth Get(string id) => id != null && _byId.TryGetValue(id, out FeatureHealth f) ? f : null;

        /// <summary>Replaces the report: each feature's probe state becomes the AND of its non-warning probes.</summary>
        public void SetReport(string stage, IEnumerable<ProbeResult> results)
        {
            _results.Clear();
            _results.AddRange(results);
            ReportStage = stage ?? "";
            var failed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (ProbeResult r in _results)
            {
                if (r.Ok || r.WarningOnly || r.Feature == null) continue;
                if (!failed.ContainsKey(r.Feature)) failed[r.Feature] = r.Id + " " + r.Target + (string.IsNullOrEmpty(r.Detail) ? "" : " (" + r.Detail + ")");
            }
            foreach (FeatureHealth f in _features)
            {
                failed.TryGetValue(f.Id, out string reason);
                f.SetProbeResult(reason == null, reason);
            }
            Version++;
        }

        /// <summary>Marks a change of availability (a trip) for the UI.</summary>
        public void Touch() => Version++;

        /// <summary>Re-enables every tripped feature (a new session starts).</summary>
        public void RearmAll()
        {
            foreach (FeatureHealth f in _features)
            {
                if (f.Tripped)
                {
                    f.Rearm();
                    Version++;
                }
            }
        }

        /// <summary>The first critical feature that is unavailable, with its reason; null when all critical features are fine.</summary>
        public string BlockingReason()
        {
            foreach (FeatureHealth f in _features)
            {
                if (f.Critical && !f.IsAvailable) return f.Title + " " + f.UnavailableReason;
            }
            return null;
        }

        /// <summary>One line for the panel: "Self-test: 24/24 checks passed" or which features are off.</summary>
        public string Summary()
        {
            int ok = 0, warn = 0, total = _results.Count;
            foreach (ProbeResult r in _results)
            {
                if (r.Ok) ok++;
                else if (r.WarningOnly) warn++;
            }
            var off = new List<string>();
            foreach (FeatureHealth f in _features)
            {
                if (!f.IsAvailable) off.Add(f.Title);
            }
            var sb = new StringBuilder();
            sb.Append("Self-test: ").Append(ok.ToString(CultureInfo.InvariantCulture)).Append('/').Append(total.ToString(CultureInfo.InvariantCulture)).Append(" checks passed");
            if (warn > 0) sb.Append(", ").Append(warn.ToString(CultureInfo.InvariantCulture)).Append(warn == 1 ? " warning" : " warnings");
            if (off.Count > 0) sb.Append("; off: ").Append(string.Join(", ", off.ToArray()));
            if (total == 0) return off.Count > 0 ? "off: " + string.Join(", ", off.ToArray()) : "";
            return sb.ToString();
        }

        /// <summary>The full report (log and diagnostics dump), one probe or feature per line.</summary>
        public IEnumerable<string> ReportLines()
        {
            yield return "self-test (" + ReportStage + "): " + Summary();
            foreach (ProbeResult r in _results)
            {
                string status = r.Ok ? "OK  " : r.WarningOnly ? "WARN" : "FAIL";
                yield return "  " + status + " " + r.Id + " [" + r.Feature + "] " + r.Target + (string.IsNullOrEmpty(r.Detail) ? "" : ": " + r.Detail);
            }
            foreach (FeatureHealth f in _features) yield return "  feature " + f;
        }
    }
}
