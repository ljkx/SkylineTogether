using System;

namespace SkylineTogether.Core.Scheduling
{
    /// <summary>
    /// Measured staleness for the network panel (ARCH §4.8.4: "measured staleness (p50/p95 per class)"): the dirty age of
    /// each value when it was scheduled, in log-spaced bins over a sliding window of two half windows (so percentiles
    /// cover the last 10 to 20 seconds). Allocation-free; single owner (the peer's scheduler state).
    /// </summary>
    internal sealed class StalenessHistogram
    {
        // upper bin edges in seconds; the last bin is open-ended
        private static readonly double[] Edges =
        {
            0.1, 0.25, 0.5, 1, 1.5, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128, 192, 256, 384, 512
        };

        private readonly double _halfWindow;
        private long[] _current = new long[Edges.Length + 1];
        private long[] _previous = new long[Edges.Length + 1];
        private double _windowStart = double.NaN;

        public StalenessHistogram(double halfWindowSeconds = 10)
        {
            _halfWindow = halfWindowSeconds > 0 ? halfWindowSeconds : 10;
        }

        /// <summary>Samples in the window.</summary>
        public long Count
        {
            get
            {
                long n = 0;
                for (int i = 0; i < _current.Length; i++) n += _current[i] + _previous[i];
                return n;
            }
        }

        public void Add(double ageSeconds, double now)
        {
            if (double.IsNaN(ageSeconds)) return;
            Roll(now);
            if (ageSeconds < 0) ageSeconds = 0;
            int bin = 0;
            while (bin < Edges.Length && ageSeconds > Edges[bin]) bin++;
            _current[bin]++;
        }

        /// <summary>The <paramref name="fraction"/> percentile (0 ... 1) in seconds, interpolated within its bin; 0 without samples.</summary>
        public double Percentile(double fraction, double now)
        {
            Roll(now);
            long total = Count;
            if (total == 0) return 0;
            if (fraction < 0) fraction = 0;
            if (fraction > 1) fraction = 1;
            double target = fraction * total;
            long seen = 0;
            for (int i = 0; i < _current.Length; i++)
            {
                long inBin = _current[i] + _previous[i];
                if (inBin == 0) continue;
                if (seen + inBin >= target)
                {
                    double lower = i == 0 ? 0 : Edges[i - 1];
                    double upper = i < Edges.Length ? Edges[i] : Edges[Edges.Length - 1] * 2;
                    double within = (target - seen) / inBin;
                    return lower + (upper - lower) * Math.Max(0, Math.Min(1, within));
                }
                seen += inBin;
            }
            return Edges[Edges.Length - 1] * 2;
        }

        public void Clear()
        {
            Array.Clear(_current, 0, _current.Length);
            Array.Clear(_previous, 0, _previous.Length);
            _windowStart = double.NaN;
        }

        private void Roll(double now)
        {
            if (double.IsNaN(now) || double.IsInfinity(now)) return;
            if (double.IsNaN(_windowStart) || now < _windowStart)
            {
                _windowStart = now;
                return;
            }
            double elapsed = now - _windowStart;
            if (elapsed < _halfWindow) return;
            if (elapsed >= 2 * _halfWindow)
            {
                Array.Clear(_previous, 0, _previous.Length);
                Array.Clear(_current, 0, _current.Length);
            }
            else
            {
                long[] swap = _previous;
                _previous = _current;
                _current = swap;
                Array.Clear(_current, 0, _current.Length);
            }
            _windowStart = now;
        }
    }
}
