using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Scheduling
{
    /// <summary>
    /// The order of a time-sliced rolling scan over a large id set (the host's agent value scan, ARCH §4.4.3
    /// SDC): ids touched since the last look (<see cref="Touch"/>: tagged by the game, members of a focus set, refresh
    /// requests) come first and once each, then the snapshot of the current pass in order. A pass ends when the snapshot
    /// is exhausted; the caller then starts the next one with a fresh snapshot (<see cref="Restart"/>), so ids bound
    /// during a pass are seen in the next. Not thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class RollingScan
    {
        private readonly List<uint> _pass = new List<uint>();
        private readonly Queue<uint> _hot = new Queue<uint>();
        private readonly HashSet<uint> _hotSet = new HashSet<uint>();
        private int _cursor;
        private long _passes;

        /// <summary>Ids left in the current pass (hot ids not counted).</summary>
        public int Remaining => _pass.Count - _cursor;

        /// <summary>Ids of the current pass.</summary>
        public int PassLength => _pass.Count;

        /// <summary>Hot ids waiting.</summary>
        public int HotCount => _hot.Count;

        /// <summary>Completed passes since the last <see cref="Clear"/>.</summary>
        public long Passes => _passes;

        /// <summary>True when the current pass is exhausted (or none was started).</summary>
        public bool PassDone => _cursor >= _pass.Count;

        /// <summary>Starts a new pass over <paramref name="ids"/> (copied; zero ids skipped).</summary>
        public void Restart(IEnumerable<uint> ids)
        {
            if (_pass.Count > 0 && _cursor >= _pass.Count) _passes++;
            _pass.Clear();
            _cursor = 0;
            if (ids == null) return;
            foreach (uint id in ids)
            {
                if (id != 0) _pass.Add(id);
            }
        }

        /// <summary>Puts an id in front of the pass (at most once until it was taken).</summary>
        public void Touch(uint id)
        {
            if (id != 0 && _hotSet.Add(id)) _hot.Enqueue(id);
        }

        /// <summary>The next id: hot ids first, then the pass. False when both are exhausted.</summary>
        public bool TryNext(out uint id, out bool hot)
        {
            if (_hot.Count > 0)
            {
                id = _hot.Dequeue();
                _hotSet.Remove(id);
                hot = true;
                return true;
            }
            hot = false;
            if (_cursor < _pass.Count)
            {
                id = _pass[_cursor++];
                return true;
            }
            id = 0;
            return false;
        }

        public void Clear()
        {
            _pass.Clear();
            _hot.Clear();
            _hotSet.Clear();
            _cursor = 0;
            _passes = 0;
        }
    }

    /// <summary>
    /// A bounded breadth-first closure over an id graph (the agents behind a selected building, citizen or
    /// vehicle, ARCH §4.8.3 inspection closure). <c>expand</c> appends the neighbours of an id; the closure stops after
    /// <c>maxDepth</c> hops or at <c>maxCount</c> ids, and every id appears once. Pure.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class ClosureBuilder
    {
        public static void Build(IEnumerable<uint> roots, Action<uint, List<uint>> expand, int maxDepth, int maxCount, HashSet<uint> output)
        {
            if (expand == null) throw new ArgumentNullException(nameof(expand));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (roots == null || maxCount <= 0) return;
            var frontier = new List<uint>();
            foreach (uint r in roots)
            {
                if (r == 0 || output.Count >= maxCount) continue;
                if (output.Add(r)) frontier.Add(r);
            }
            var next = new List<uint>();
            var neighbours = new List<uint>();
            for (int depth = 0; depth < maxDepth && frontier.Count > 0 && output.Count < maxCount; depth++)
            {
                next.Clear();
                foreach (uint id in frontier)
                {
                    neighbours.Clear();
                    expand(id, neighbours);
                    foreach (uint n in neighbours)
                    {
                        if (n == 0) continue;
                        if (output.Count >= maxCount) return;
                        if (output.Add(n)) next.Add(n);
                    }
                }
                List<uint> swap = frontier;
                frontier = next;
                next = swap;
            }
        }
    }
}
