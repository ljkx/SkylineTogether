using System;
using System.Collections.Generic;
using System.Globalization;

namespace SkylineTogether.Core.Diagnostics
{
    /// <summary>w9/perf: the order in which features draw from the shared frame budget (lower value = served first).</summary>
    public enum BudgetPriority : byte
    {
        /// <summary>Applying what arrived (host: proposals and acks, client: the host's batches). Never limited by the others.</summary>
        Apply = 0,

        /// <summary>Sends the replicas depend on: the value scan and the deferred records per peer, agents, attention values.</summary>
        CriticalSend = 1,

        /// <summary>Integrity: digests, audits, repairs.</summary>
        Integrity = 2,

        /// <summary>Statistics, cell maps, flow status and other views that only feed info panels.</summary>
        Statistics = 3,
    }

    /// <summary>
    /// w9/perf: one per-frame time budget shared by every feature that does bounded per-frame work (ARCH §13 perf). Each
    /// feature asks for its allowance right before it works (<see cref="Begin(Feature)"/>) and reports what it spent
    /// (<see cref="End"/>); the allowance is the feature's own cap, reduced only when the frame is already over budget:
    /// the frame total minus what was spent so far and minus what the higher-priority features that did not run yet this
    /// frame usually spend (their average), never below the feature's floor (a fraction of its cap, so nothing starves).
    /// The highest priority (<see cref="BudgetPriority.Apply"/>) always gets its cap. An overrun of the frame total is
    /// carried into the next frames (at most two frames' worth).
    ///
    /// Telemetry per feature over the last <see cref="Window"/> frames: average, 95th percentile and maximum milliseconds,
    /// how often its allowance was clipped below its cap, and the last allowance. The caller measures the time (the game
    /// layer uses <c>Stopwatch</c>), so this stays pure. Allocation-free per frame after registration. Main thread; not
    /// thread-safe.
    /// </summary>
    public sealed class FrameBudgetService
    {
        /// <summary>Frames kept for the statistics (about five seconds at 60 frames per second).</summary>
        public const int Window = 300;

        /// <summary>A registered feature. Obtain with <see cref="Register"/>.</summary>
        public sealed class Feature
        {
            internal readonly double[] Samples = new double[Window];
            internal int SampleCount;
            internal int SampleNext;
            internal double FrameMs;
            internal bool RanThisFrame;
            internal bool ClippedThisFrame;
            internal double AverageMs; // smoothed per frame, for the reservation of features that did not run yet
            internal long Frames;
            internal long ClippedFrames;
            internal long OverrunFrames;

            internal Feature(string name, BudgetPriority priority, double capMs, double floorFraction, int index)
            {
                Name = name;
                Priority = priority;
                CapMs = capMs;
                FloorFraction = floorFraction;
                Index = index;
            }

            public string Name { get; }

            public BudgetPriority Priority { get; }

            /// <summary>The most this feature may spend in one frame (milliseconds).</summary>
            public double CapMs { get; set; }

            /// <summary>The share of <see cref="CapMs"/> the feature always gets, however full the frame is.</summary>
            public double FloorFraction { get; }

            internal int Index { get; }

            /// <summary>The allowance of the latest <see cref="Begin(Feature)"/>.</summary>
            public double LastAllowanceMs { get; internal set; }
        }

        /// <summary>A snapshot of one feature's statistics (<see cref="GetStats"/>).</summary>
        public struct FeatureStats
        {
            public string Name;
            public BudgetPriority Priority;
            public double CapMs;
            public double AverageMs;
            public double P95Ms;
            public double MaxMs;
            public double LastAllowanceMs;
            public long Frames;
            public long ClippedFrames;
            public long OverrunFrames;
        }

        private const double Alpha = 0.1;
        private readonly List<Feature> _features = new List<Feature>();
        private readonly Dictionary<string, Feature> _byName = new Dictionary<string, Feature>(StringComparer.Ordinal);
        private double[] _sortScratch = new double[Window];
        private double _frameMs;
        private double _spentMs;
        private double _debtMs;
        private long _frames;
        private long _overBudgetFrames;
        private double _lastFrameSpentMs;

        public FrameBudgetService(double frameMs)
        {
            if (!(frameMs > 0) || double.IsInfinity(frameMs)) throw new ArgumentOutOfRangeException(nameof(frameMs));
            _frameMs = frameMs;
        }

        /// <summary>The shared budget of one frame (milliseconds).</summary>
        public double FrameMs
        {
            get => _frameMs;
            set
            {
                if (!(value > 0) || double.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value));
                _frameMs = value;
            }
        }

        /// <summary>Spent so far in the current frame by every feature.</summary>
        public double SpentMs => _spentMs;

        /// <summary>The total spent in the last closed frame.</summary>
        public double LastFrameSpentMs => _lastFrameSpentMs;

        /// <summary>The overrun carried into the next frames.</summary>
        public double DebtMs => _debtMs;

        public long Frames => _frames;

        /// <summary>Closed frames whose total exceeded <see cref="FrameMs"/>.</summary>
        public long OverBudgetFrames => _overBudgetFrames;

        public IReadOnlyList<Feature> Features => _features;

        /// <summary>Registers a feature (or returns the one registered under that name).</summary>
        public Feature Register(string name, BudgetPriority priority, double capMs, double floorFraction = 0.25)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("a feature needs a name", nameof(name));
            if (!(capMs > 0) || double.IsInfinity(capMs)) throw new ArgumentOutOfRangeException(nameof(capMs));
            if (!(floorFraction >= 0) || floorFraction > 1) throw new ArgumentOutOfRangeException(nameof(floorFraction));
            if (_byName.TryGetValue(name, out Feature known)) return known;
            var f = new Feature(name, priority, capMs, floorFraction, _features.Count);
            _features.Add(f);
            _byName.Add(name, f);
            return f;
        }

        public Feature Find(string name) => name != null && _byName.TryGetValue(name, out Feature f) ? f : null;

        /// <summary>The milliseconds <paramref name="feature"/> may spend now (with its own cap).</summary>
        public double Begin(Feature feature) => Begin(feature, feature.CapMs);

        /// <summary>
        /// The milliseconds <paramref name="feature"/> may spend now, with <paramref name="capMs"/> as this call's cap (a
        /// feature may ask for less than its registered cap, e.g. when no peer needs its output).
        /// </summary>
        public double Begin(Feature feature, double capMs)
        {
            if (feature == null) throw new ArgumentNullException(nameof(feature));
            if (!(capMs > 0) || double.IsInfinity(capMs)) capMs = feature.CapMs;
            double allowance = capMs;
            if (feature.Priority != BudgetPriority.Apply)
            {
                double reserved = 0;
                for (int i = 0; i < _features.Count; i++)
                {
                    Feature other = _features[i];
                    if (other.Priority < feature.Priority && !other.RanThisFrame) reserved += Math.Min(other.AverageMs, other.CapMs);
                }
                double available = _frameMs - _debtMs - _spentMs - reserved;
                double floor = capMs * feature.FloorFraction;
                allowance = Math.Min(capMs, Math.Max(floor, available));
            }
            feature.LastAllowanceMs = allowance;
            if (allowance < capMs) feature.ClippedThisFrame = true;
            return allowance;
        }

        /// <summary>Records what <paramref name="feature"/> spent (several calls per frame add up).</summary>
        public void End(Feature feature, double spentMs)
        {
            if (feature == null) throw new ArgumentNullException(nameof(feature));
            if (!(spentMs >= 0) || double.IsInfinity(spentMs)) return;
            feature.FrameMs += spentMs;
            feature.RanThisFrame = true;
            _spentMs += spentMs;
        }

        /// <summary>
        /// Closes the frame: records each feature's frame total (zero for a feature that did not run, so averages are per
        /// frame), carries an overrun of the frame total into the next frames and starts the next frame.
        /// </summary>
        public void EndFrame()
        {
            _frames++;
            _lastFrameSpentMs = _spentMs;
            if (_spentMs > _frameMs) _overBudgetFrames++;
            _debtMs = Math.Max(0, Math.Min(_debtMs + _spentMs - _frameMs, _frameMs * 2));
            for (int i = 0; i < _features.Count; i++)
            {
                Feature f = _features[i];
                double ms = f.FrameMs;
                f.Samples[f.SampleNext] = ms;
                f.SampleNext = (f.SampleNext + 1) % Window;
                if (f.SampleCount < Window) f.SampleCount++;
                // smoothed per frame (zero when it did not run): what to keep free for it before it runs
                f.AverageMs = f.SampleCount == 1 ? ms : f.AverageMs + (ms - f.AverageMs) * Alpha;
                if (f.RanThisFrame)
                {
                    f.Frames++;
                    if (ms > f.LastAllowanceMs + 0.05 && f.LastAllowanceMs > 0) f.OverrunFrames++;
                }
                f.FrameMs = 0;
                f.RanThisFrame = false;
                if (f.ClippedThisFrame) f.ClippedFrames++;
                f.ClippedThisFrame = false;
            }
            _spentMs = 0;
        }

        /// <summary>Statistics of one feature over the last <see cref="Window"/> frames.</summary>
        public FeatureStats GetStats(Feature f)
        {
            var s = new FeatureStats
            {
                Name = f.Name,
                Priority = f.Priority,
                CapMs = f.CapMs,
                LastAllowanceMs = f.LastAllowanceMs,
                Frames = f.Frames,
                ClippedFrames = f.ClippedFrames,
                OverrunFrames = f.OverrunFrames,
            };
            int n = f.SampleCount;
            if (n == 0) return s;
            if (_sortScratch.Length < n) _sortScratch = new double[n];
            double sum = 0, max = 0;
            for (int i = 0; i < n; i++)
            {
                double v = f.Samples[i];
                _sortScratch[i] = v;
                sum += v;
                if (v > max) max = v;
            }
            Array.Sort(_sortScratch, 0, n);
            s.AverageMs = sum / n;
            s.MaxMs = max;
            s.P95Ms = _sortScratch[Percentile95Index(n)];
            return s;
        }

        /// <summary>The index of the 95th percentile in <paramref name="n"/> sorted samples (nearest rank).</summary>
        public static int Percentile95Index(int n)
        {
            if (n <= 0) return 0;
            int rank = (int)Math.Ceiling(0.95 * n);
            return Math.Max(0, Math.Min(n - 1, rank - 1));
        }

        /// <summary>The summed average over the window of every feature (milliseconds per frame).</summary>
        public double TotalAverageMs()
        {
            double sum = 0;
            for (int i = 0; i < _features.Count; i++)
            {
                Feature f = _features[i];
                int n = f.SampleCount;
                if (n == 0) continue;
                double s = 0;
                for (int k = 0; k < n; k++) s += f.Samples[k];
                sum += s / n;
            }
            return sum;
        }

        /// <summary>One line per feature for the diagnostics dump: "budget apply (apply): avg 0.420 ms, p95 1.10 ms, max 3.20 ms, ...".</summary>
        public void Describe(List<string> lines)
        {
            var ci = CultureInfo.InvariantCulture;
            lines.Add("frame budget: " + _frameMs.ToString("0.0", ci) + " ms shared, " + _frames.ToString(ci) + " frames, " + _overBudgetFrames.ToString(ci) + " over budget, debt "
                      + _debtMs.ToString("0.00", ci) + " ms, last frame " + _lastFrameSpentMs.ToString("0.00", ci) + " ms, average total " + TotalAverageMs().ToString("0.00", ci) + " ms");
            for (int i = 0; i < _features.Count; i++)
            {
                FeatureStats s = GetStats(_features[i]);
                lines.Add("budget " + s.Name + " (" + PriorityName(s.Priority) + ", cap " + s.CapMs.ToString("0.00", ci) + " ms): avg " + s.AverageMs.ToString("0.000", ci) + " ms, p95 "
                          + s.P95Ms.ToString("0.00", ci) + " ms, max " + s.MaxMs.ToString("0.00", ci) + " ms over the last " + Math.Min(_frames, Window).ToString(ci) + " frames; clipped "
                          + s.ClippedFrames.ToString(ci) + ", overran " + s.OverrunFrames.ToString(ci) + " of " + s.Frames.ToString(ci) + " frames run");
            }
        }

        public static string PriorityName(BudgetPriority p)
        {
            switch (p)
            {
                case BudgetPriority.Apply: return "apply";
                case BudgetPriority.CriticalSend: return "critical send";
                case BudgetPriority.Integrity: return "integrity";
                case BudgetPriority.Statistics: return "statistics";
                default: return "priority " + (int)p;
            }
        }

        /// <summary>Forgets the measurements (session end); the registrations stay.</summary>
        public void Reset()
        {
            _spentMs = 0;
            _debtMs = 0;
            _frames = 0;
            _overBudgetFrames = 0;
            _lastFrameSpentMs = 0;
            for (int i = 0; i < _features.Count; i++)
            {
                Feature f = _features[i];
                Array.Clear(f.Samples, 0, Window);
                f.SampleCount = 0;
                f.SampleNext = 0;
                f.FrameMs = 0;
                f.RanThisFrame = false;
                f.ClippedThisFrame = false;
                f.AverageMs = 0;
                f.Frames = 0;
                f.ClippedFrames = 0;
                f.OverrunFrames = 0;
                f.LastAllowanceMs = 0;
            }
        }
    }
}
