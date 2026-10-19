using System;
using System.Globalization;

namespace SkylineTogether.Core.Diagnostics
{
    /// <summary>
    /// Keeps the mod's log file bounded whatever peers send (ARCHITECTURE.md#logs): lines are rate-limited (a token bucket,
    /// errors with a bucket of their own), and once <see cref="MaxBytes"/> were written in this run only errors are
    /// written, up to a further quarter of it, then nothing. Suppressed lines are counted; the next line written is
    /// preceded by a notice saying how many were left out. The game's logger owns the file (it starts it anew with every
    /// game start), so the cap applies per run. Pure: time is passed in. Thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class LogFloodGuard
    {
        /// <summary>Bytes (characters) written per run before only errors are written.</summary>
        public long MaxBytes = 64L * 1024 * 1024;

        /// <summary>Non-error lines per second on average, and the burst allowed at once.</summary>
        public double LinesPerSecond = 200;
        public double Burst = 2000;

        /// <summary>Error lines per second on average, and their burst.</summary>
        public double ErrorsPerSecond = 20;
        public double ErrorBurst = 200;

        private readonly object _lock = new object();
        private double _tokens = double.NaN;
        private double _errorTokens = double.NaN;
        private double _refilledAt = double.NaN;
        private long _written;
        private long _suppressed;
        private bool _capNoticed;

        /// <summary>Characters written so far in this run (the lines allowed).</summary>
        public long Written
        {
            get { lock (_lock) return _written; }
        }

        /// <summary>Lines suppressed in total.</summary>
        public long SuppressedTotal { get; private set; }

        /// <summary>
        /// Whether a line of <paramref name="length"/> characters at <paramref name="level"/> may be written at
        /// <paramref name="now"/> (seconds). When it may and lines were suppressed before (or the size cap was just
        /// reached), <paramref name="notice"/> is a line to write first; otherwise null.
        /// </summary>
        public bool Allow(LogLevel level, int length, double now, out string notice)
        {
            notice = null;
            lock (_lock)
            {
                if (double.IsNaN(_refilledAt))
                {
                    _tokens = Burst;
                    _errorTokens = ErrorBurst;
                    _refilledAt = now;
                }
                double elapsed = Math.Max(0, now - _refilledAt);
                _refilledAt = Math.Max(_refilledAt, now);
                _tokens = Math.Min(Burst, _tokens + elapsed * LinesPerSecond);
                _errorTokens = Math.Min(ErrorBurst, _errorTokens + elapsed * ErrorsPerSecond);
                bool error = level >= LogLevel.Error;
                bool overCap = _written >= MaxBytes;
                if (overCap && (!error || _written >= MaxBytes + MaxBytes / 4))
                {
                    Suppress();
                    return false;
                }
                if (error ? _errorTokens < 1 : _tokens < 1)
                {
                    Suppress();
                    return false;
                }
                if (error) _errorTokens -= 1;
                else _tokens -= 1;
                if (overCap && !_capNoticed)
                {
                    _capNoticed = true;
                    notice = "the log reached " + (MaxBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MiB in this run; from now on only errors are written"
                             + (_suppressed > 0 ? " (" + _suppressed.ToString(CultureInfo.InvariantCulture) + " line(s) left out)" : "");
                    _suppressed = 0;
                }
                else if (_suppressed > 0)
                {
                    notice = _suppressed.ToString(CultureInfo.InvariantCulture) + " log line(s) left out (too many at once)";
                    _suppressed = 0;
                }
                _written += Math.Max(0, length) + (notice?.Length ?? 0);
                return true;
            }
        }

        private void Suppress()
        {
            _suppressed++;
            SuppressedTotal++;
        }
    }
}
