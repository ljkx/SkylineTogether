using System.Collections.Generic;

namespace SkylineTogether.Core.Integrity
{
    /// <summary>
    /// A sliding window of event times (seconds): "at most N per T seconds" limits and "more than N within T" triggers.
    /// An event at time t leaves the window at <c>t + span</c>. Not thread-safe; owners lock around it.
    /// </summary>
    internal sealed class TimeWindow
    {
        private readonly Queue<double> _times = new Queue<double>();
        private readonly double _span;

        internal TimeWindow(double spanSeconds)
        {
            _span = spanSeconds > 0 ? spanSeconds : 1.0;
        }

        internal double Span => _span;

        /// <summary>Events inside the window ending at <paramref name="now"/>.</summary>
        internal int Count(double now)
        {
            Prune(now);
            return _times.Count;
        }

        internal void Add(double now)
        {
            Prune(now);
            _times.Enqueue(now);
        }

        /// <summary>Adds an event if fewer than <paramref name="limit"/> are inside the window; false otherwise (nothing added).</summary>
        internal bool TryAdd(double now, int limit)
        {
            Prune(now);
            if (_times.Count >= limit) return false;
            _times.Enqueue(now);
            return true;
        }

        /// <summary>True when an event happened within the window ending at <paramref name="now"/>.</summary>
        internal bool Any(double now)
        {
            Prune(now);
            return _times.Count > 0;
        }

        internal void Clear() => _times.Clear();

        private void Prune(double now)
        {
            while (_times.Count > 0 && now - _times.Peek() >= _span) _times.Dequeue();
        }
    }
}
