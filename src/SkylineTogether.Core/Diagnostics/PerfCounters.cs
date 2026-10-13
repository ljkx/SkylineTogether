using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SkylineTogether.Core.Diagnostics
{
    /// <summary>
    /// Lightweight per-frame timings of named components (milliseconds per frame: last, smoothed average, peak over the
    /// last window) and byte rates of named channels (bytes per second over the last second, in 100 ms buckets). The
    /// caller supplies the measurements (the game layer times with <c>Stopwatch</c>), so this stays pure. Allocation-free
    /// after the first use of a name. Main thread. Not thread-safe.
    /// </summary>
    public sealed class PerfCounters
    {
        private const double Alpha = 0.05;
        private const double PeakWindowSeconds = 10;
        private const int Buckets = 10;
        private const double BucketSeconds = 0.1;

        private sealed class Timer
        {
            public string Name;
            public double FrameMs;
            public double LastMs;
            public double AverageMs;
            public double PeakMs;
            public double PeakPrevMs;
            public double PeakSince;
            public long Samples;
            public bool Started;
            /// <summary>perf pass: the component this one runs inside (its time is part of the parent's), or null.</summary>
            public string Parent;
        }

        private sealed class Channel
        {
            public string Name;
            public readonly long[] Bytes = new long[Buckets];
            public readonly long[] Ids = new long[Buckets];
            public long Total;
            public long Messages;
        }

        private readonly List<Timer> _timers = new List<Timer>();
        private readonly Dictionary<string, Timer> _timerByName = new Dictionary<string, Timer>(StringComparer.Ordinal);
        private readonly List<Channel> _channels = new List<Channel>();
        private readonly Dictionary<string, Channel> _channelByName = new Dictionary<string, Channel>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _counters = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly List<string> _counterOrder = new List<string>();
        private readonly Dictionary<string, string> _parents = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Adds milliseconds spent by a component during the current frame (several calls per frame add up).</summary>
        public void AddTime(string component, double milliseconds)
        {
            if (!(milliseconds >= 0) || double.IsInfinity(milliseconds)) return;
            Timer t = GetTimer(component);
            t.FrameMs += milliseconds;
            t.Started = true;
        }

        /// <summary>
        /// perf pass: declares that <paramref name="component"/> runs inside <paramref name="parent"/> (its time is already
        /// part of the parent's), so totals count it once; the diagnostics show it as "component (in parent)".
        /// </summary>
        public void Nest(string component, string parent)
        {
            if (component == null || string.Equals(component, parent, StringComparison.Ordinal)) return;
            _parents[component] = parent; // kept across Clear (structure, not measurements)
            if (_timerByName.TryGetValue(component, out Timer t)) t.Parent = parent;
        }

        /// <summary>Closes the frame: each component that reported time this frame records its frame total.</summary>
        public void EndFrame(double now)
        {
            for (int i = 0; i < _timers.Count; i++)
            {
                Timer t = _timers[i];
                if (!t.Started) continue;
                double ms = t.FrameMs;
                t.LastMs = ms;
                t.AverageMs = t.Samples == 0 ? ms : t.AverageMs + (ms - t.AverageMs) * Alpha;
                if (now - t.PeakSince > PeakWindowSeconds)
                {
                    t.PeakPrevMs = t.PeakMs;
                    t.PeakMs = 0;
                    t.PeakSince = now;
                }
                if (ms > t.PeakMs) t.PeakMs = ms;
                t.Samples++;
                t.FrameMs = 0;
                t.Started = false;
            }
        }

        /// <summary>Counts bytes sent (or received) on a channel at time <paramref name="now"/> (seconds).</summary>
        public void AddBytes(string channel, long bytes, double now)
        {
            if (bytes <= 0) return;
            if (!_channelByName.TryGetValue(channel, out Channel c))
            {
                c = new Channel { Name = channel };
                for (int i = 0; i < Buckets; i++) c.Ids[i] = long.MinValue;
                _channelByName.Add(channel, c);
                _channels.Add(c);
            }
            long id = (long)Math.Floor(now / BucketSeconds);
            int slot = (int)(((id % Buckets) + Buckets) % Buckets);
            if (c.Ids[slot] != id)
            {
                c.Ids[slot] = id;
                c.Bytes[slot] = 0;
            }
            c.Bytes[slot] += bytes;
            c.Total += bytes;
            c.Messages++;
        }

        /// <summary>Adds to a named event counter (drops, refusals, coalesced updates, ...).</summary>
        public void Count(string name, long delta = 1)
        {
            if (!_counters.TryGetValue(name, out long v)) _counterOrder.Add(name);
            _counters[name] = v + delta;
        }

        public long GetCount(string name) => _counters.TryGetValue(name, out long v) ? v : 0;

        /// <summary>Bytes per second of a channel over the second ending at <paramref name="now"/>.</summary>
        public double Rate(string channel, double now)
        {
            if (!_channelByName.TryGetValue(channel, out Channel c)) return 0;
            long current = (long)Math.Floor(now / BucketSeconds);
            long sum = 0;
            for (int i = 0; i < Buckets; i++)
            {
                long age = current - c.Ids[i];
                if (c.Ids[i] != long.MinValue && age >= 0 && age < Buckets) sum += c.Bytes[i];
            }
            return sum;
        }

        public long TotalBytes(string channel) => _channelByName.TryGetValue(channel, out Channel c) ? c.Total : 0;

        /// <summary>The smoothed milliseconds per frame of a component (0 when unknown).</summary>
        public double AverageMs(string component) => _timerByName.TryGetValue(component, out Timer t) ? t.AverageMs : 0;

        /// <summary>The highest milliseconds per frame of a component within the last 10-20 s.</summary>
        public double PeakMs(string component) => _timerByName.TryGetValue(component, out Timer t) ? Math.Max(t.PeakMs, t.PeakPrevMs) : 0;

        /// <summary>The sum of the components' smoothed milliseconds per frame (nested components are counted in their parent).</summary>
        public double TotalAverageMs
        {
            get
            {
                double sum = 0;
                for (int i = 0; i < _timers.Count; i++)
                {
                    if (_timers[i].Parent == null) sum += _timers[i].AverageMs;
                }
                return sum;
            }
        }

        /// <summary>One compact line: "a 0.42/1.9 ms, b ..." (average/peak) and "ch 12.3 KB/s".</summary>
        public string Summary(double now)
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            for (int i = 0; i < _timers.Count; i++)
            {
                Timer t = _timers[i];
                if (t.Samples == 0) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(t.Name);
                if (t.Parent != null) sb.Append(" (in ").Append(t.Parent).Append(')');
                sb.Append(' ').Append(t.AverageMs.ToString("0.00", ci)).Append('/').Append(Math.Max(t.PeakMs, t.PeakPrevMs).ToString("0.0", ci)).Append(" ms");
            }
            bool first = true;
            for (int i = 0; i < _channels.Count; i++)
            {
                double rate = Rate(_channels[i].Name, now);
                if (rate <= 0) continue;
                sb.Append(first ? (sb.Length > 0 ? "; " : "") : ", ");
                first = false;
                sb.Append(_channels[i].Name).Append(' ').Append((rate / 1024.0).ToString("0.0", ci)).Append(" KB/s");
            }
            return sb.ToString();
        }

        /// <summary>
        /// A short form for a status line: "1.23 ms/frame (scan 0.80), 12.3 KB/s" — the summed smoothed milliseconds
        /// (top-level components only: a nested one is part of its parent), the costliest component, and the summed rate of every channel except <paramref name="excludedChannel"/>.
        /// </summary>
        public string CompactSummary(double now, string excludedChannel = null)
        {
            var ci = CultureInfo.InvariantCulture;
            double total = 0, worst = 0;
            string worstName = null;
            foreach (Timer t in _timers)
            {
                if (t.Parent == null) total += t.AverageMs;
                if (t.AverageMs > worst)
                {
                    worst = t.AverageMs;
                    worstName = t.Name;
                }
            }
            double rate = 0;
            foreach (Channel c in _channels)
            {
                if (!string.Equals(c.Name, excludedChannel, StringComparison.Ordinal)) rate += Rate(c.Name, now);
            }
            if (worstName == null && rate <= 0) return "";
            var sb = new StringBuilder();
            sb.Append(total.ToString("0.00", ci)).Append(" ms/frame");
            if (worstName != null) sb.Append(" (").Append(worstName).Append(' ').Append(worst.ToString("0.00", ci)).Append(')');
            sb.Append(", ").Append((rate / 1024.0).ToString("0.0", ci)).Append(" KB/s");
            return sb.ToString();
        }

        /// <summary>Every component, channel and counter, one per line (diagnostics dump).</summary>
        public void Describe(double now, List<string> lines)
        {
            var ci = CultureInfo.InvariantCulture;
            foreach (Timer t in _timers)
            {
                lines.Add("time " + t.Name + (t.Parent != null ? " (in " + t.Parent + ")" : "") + ": avg " + t.AverageMs.ToString("0.000", ci) + " ms/frame, last " + t.LastMs.ToString("0.000", ci)
                          + " ms, peak " + Math.Max(t.PeakMs, t.PeakPrevMs).ToString("0.00", ci) + " ms, frames " + t.Samples.ToString(ci));
            }
            foreach (Channel c in _channels)
            {
                lines.Add("bytes " + c.Name + ": " + (Rate(c.Name, now) / 1024.0).ToString("0.00", ci) + " KB/s now, " + c.Total.ToString(ci)
                          + " bytes in " + c.Messages.ToString(ci) + " messages");
            }
            foreach (string name in _counterOrder) lines.Add("count " + name + ": " + _counters[name].ToString(ci));
        }

        public void Clear()
        {
            _timers.Clear();
            _timerByName.Clear();
            _channels.Clear();
            _channelByName.Clear();
            _counters.Clear();
            _counterOrder.Clear();
        }

        private Timer GetTimer(string name)
        {
            if (_timerByName.TryGetValue(name, out Timer t)) return t;
            t = new Timer { Name = name };
            _parents.TryGetValue(name, out t.Parent);
            _timerByName.Add(name, t);
            _timers.Add(t);
            return t;
        }
    }

    /// <summary>
    /// A per-frame work budget with carry-over (milliseconds measured by the caller): work units are taken while the
    /// frame's allowance lasts; an overrun is paid back by the next frames, and unused allowance is not banked beyond one
    /// frame. Pure.
    /// </summary>
    public sealed class FrameBudget
    {
        private readonly double _perFrameMs;
        private double _debtMs;

        public FrameBudget(double perFrameMs)
        {
            if (!(perFrameMs > 0)) throw new ArgumentOutOfRangeException(nameof(perFrameMs));
            _perFrameMs = perFrameMs;
        }

        public double PerFrameMs => _perFrameMs;

        /// <summary>Overrun carried into the next frames.</summary>
        public double DebtMs => _debtMs;

        /// <summary>The allowance of the frame that starts now (at least a tenth of the per-frame budget, so work never starves).</summary>
        public double BeginFrame() => Math.Max(_perFrameMs * 0.1, _perFrameMs - _debtMs);

        /// <summary>Records what the frame spent: time beyond the per-frame budget becomes debt (at most four frames' worth),
        /// time below it repays debt (no credit is banked).</summary>
        public void EndFrame(double spentMs)
        {
            if (!(spentMs >= 0) || double.IsInfinity(spentMs)) return;
            _debtMs = Math.Max(0, Math.Min(_debtMs + spentMs - _perFrameMs, _perFrameMs * 4));
        }

        public void Reset() => _debtMs = 0;
    }
}
