using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// The mover roster (ARCHITECTURE.md#movers): the host's set of live movers in one window of net ids, written as the last
    /// record of a movers batch (<see cref="SyncRecordKind.MoverRoster"/>). A client applies movers batches strictly in
    /// order, so when it reaches the roster it holds exactly the host's movers of that window, unless a record was lost
    /// or failed: movers only it has (the host lost them without a despawn, or a despawn failed here) are removed, movers
    /// it lacks are asked for again. The ids travel as runs (<c>gap, length</c> pairs), because movers bound together at
    /// session start occupy long runs of ids. Pure and allocation-light; not thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class MoverRoster
    {
        /// <summary>Ids a roster window covers at most.</summary>
        public const int MaxSpan = 1 << 20;

        /// <summary>Runs one roster record holds at most.</summary>
        public const int MaxRuns = 1 << 16;

        /// <summary>
        /// Turns strictly increasing ids into runs, flattened as <c>start, length</c> pairs into <paramref name="runs"/>
        /// (cleared first). Throws on ids that are not strictly increasing.
        /// </summary>
        public static void BuildRuns(IReadOnlyList<uint> sortedIds, List<uint> runs)
        {
            if (sortedIds == null) throw new ArgumentNullException(nameof(sortedIds));
            if (runs == null) throw new ArgumentNullException(nameof(runs));
            runs.Clear();
            for (int i = 0; i < sortedIds.Count; i++)
            {
                uint id = sortedIds[i];
                if (i > 0 && id <= sortedIds[i - 1]) throw new ArgumentException("ids must be strictly increasing", nameof(sortedIds));
                int last = runs.Count - 2;
                if (last >= 0 && runs[last] + runs[last + 1] == id) runs[last + 1]++;
                else
                {
                    runs.Add(id);
                    runs.Add(1);
                }
            }
        }

        /// <summary>
        /// Compares the host's runs of the window <c>[first, first + span)</c> with the client's own movers of that window
        /// (<paramref name="localSorted"/>, strictly increasing; ids outside the window are ignored): ids only the client has go
        /// to <paramref name="extra"/>, ids only the host has to <paramref name="missing"/> (at most
        /// <paramref name="maxMissing"/>). Both lists are appended to in increasing order.
        /// </summary>
        public static void Diff(uint first, uint span, uint[] runs, IReadOnlyList<uint> localSorted, List<uint> extra, List<uint> missing, int maxMissing)
        {
            if (localSorted == null) throw new ArgumentNullException(nameof(localSorted));
            if (extra == null) throw new ArgumentNullException(nameof(extra));
            if (missing == null) throw new ArgumentNullException(nameof(missing));
            ulong end = (ulong)first + span;
            int li = 0;
            int found = 0;
            // local ids before the window are not this roster's business
            while (li < localSorted.Count && localSorted[li] < first) li++;
            int runCount = runs == null ? 0 : runs.Length / 2;
            for (int r = 0; r < runCount; r++)
            {
                uint start = runs[2 * r];
                ulong stop = Math.Min((ulong)start + runs[2 * r + 1], end);
                // local ids before this run: only here
                while (li < localSorted.Count && localSorted[li] < start)
                {
                    if (localSorted[li] >= end) break;
                    extra.Add(localSorted[li++]);
                }
                for (ulong id = start; id < stop; id++)
                {
                    if (li < localSorted.Count && localSorted[li] == id)
                    {
                        li++;
                        continue;
                    }
                    if (found < maxMissing)
                    {
                        missing.Add((uint)id);
                        found++;
                    }
                }
            }
            while (li < localSorted.Count && localSorted[li] < end) extra.Add(localSorted[li++]);
        }
    }

    /// <summary>
    /// Host: when the next roster window is due (ARCHITECTURE.md#movers). The id space <c>[1, idLimit)</c> is cut into
    /// <see cref="Windows"/> windows (at least <see cref="MinWidth"/> ids each); one window goes out every
    /// <see cref="IntervalSeconds"/>, so a full pass takes about half a minute, and after a player joined a burst sends one
    /// window per frame until a whole pass from the current position is done. Not thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class MoverRosterSchedule
    {
        public const int Windows = 64;
        public const int MinWidth = 1024;
        public const double IntervalSeconds = 0.5;

        private uint _cursor = 1;
        private double _last = double.NegativeInfinity;
        private int _burst;

        /// <summary>Completed passes over the id space.</summary>
        public long Passes { get; private set; }

        /// <summary>Windows left of the current burst.</summary>
        public int BurstRemaining => _burst;

        /// <summary>A player joined: the next <see cref="Windows"/> windows go out one per call, without waiting.</summary>
        public void Burst() => _burst = Windows;

        public void Reset()
        {
            _cursor = 1;
            _last = double.NegativeInfinity;
            _burst = 0;
            Passes = 0;
        }

        /// <summary>The next window when one is due; <paramref name="idLimit"/> is one above the highest id in use.</summary>
        public bool TryNext(double now, uint idLimit, out uint first, out uint span)
        {
            first = span = 0;
            if (idLimit <= 1) return false;
            if (_burst > 0) _burst--;
            else if (now - _last < IntervalSeconds) return false;
            _last = now;
            if (_cursor >= idLimit) _cursor = 1;
            uint width = (uint)Math.Min(MoverRoster.MaxSpan, Math.Max(MinWidth, ((ulong)idLimit - 1 + Windows - 1) / Windows));
            first = _cursor;
            span = (uint)Math.Min(width, (ulong)idLimit - first);
            _cursor = first + span;
            if (_cursor >= idLimit)
            {
                _cursor = 1;
                Passes++;
            }
            return true;
        }
    }

    /// <summary>
    /// Client: what to do with a roster (ARCHITECTURE.md#movers). Movers only this game has are removed at once (the stream is
    /// ordered, so they cannot be in flight). A mover only the host has is asked for when it was missing in two rosters
    /// in a row (its spawn may simply have failed for a missing asset, or the player deleted it locally and the host's
    /// despawn is on its way); asking resets the count, so a mover that never arrives is asked for every second pass.
    /// Not thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class MoverRosterAudit
    {
        /// <summary>Missing ids remembered at most (more are compared again next pass).</summary>
        public const int MaxTracked = 16384;

        /// <summary>
        /// The longest wait, in rosters of the id's window, between two requests of a mover that keeps not arriving (the
        /// waits double from 1: a mover this client can never create, for a vehicle asset it lacks, was asked for and
        /// rebuilt by the host about once a minute for the whole session).
        /// </summary>
        public const int MaxBackoffRosters = 64;

        private readonly HashSet<uint> _missingOnce = new HashSet<uint>();
        private readonly List<uint> _extra = new List<uint>();
        private readonly List<uint> _missing = new List<uint>();
        private readonly HashSet<uint> _previous = new HashSet<uint>();
        private readonly HashSet<uint> _missingNow = new HashSet<uint>();
        private readonly Dictionary<uint, (int Requests, int Wait)> _backoff = new Dictionary<uint, (int, int)>();
        private readonly List<uint> _backoffScratch = new List<uint>();

        /// <summary>Requests skipped because the mover was asked for before and still did not arrive.</summary>
        public long BackedOff { get; private set; }

        /// <summary>Rosters compared.</summary>
        public long Rosters { get; private set; }

        /// <summary>Movers removed because the host does not have them.</summary>
        public long Removed { get; private set; }

        /// <summary>Movers asked for because only the host has them.</summary>
        public long Requested { get; private set; }

        /// <summary>Ids currently missing once (waiting for a second roster).</summary>
        public int Pending => _missingOnce.Count;

        public void Clear()
        {
            _missingOnce.Clear();
            _backoff.Clear();
            Rosters = Removed = Requested = BackedOff = 0;
        }

        /// <summary>
        /// Compares one roster with the client's movers of its window (<paramref name="localSorted"/>, strictly increasing):
        /// ids to remove go to <paramref name="remove"/>, ids to ask for to <paramref name="request"/>.
        /// </summary>
        public void OnRoster(uint first, uint span, uint[] runs, IReadOnlyList<uint> localSorted, List<uint> remove, List<uint> request)
        {
            if (remove == null) throw new ArgumentNullException(nameof(remove));
            if (request == null) throw new ArgumentNullException(nameof(request));
            Rosters++;
            _extra.Clear();
            _missing.Clear();
            MoverRoster.Diff(first, span, runs, localSorted, _extra, _missing, MaxTracked);
            remove.AddRange(_extra);
            Removed += _extra.Count;
            // ids of this window that were missing before and are here now (or gone on the host too) are forgotten
            ulong end = (ulong)first + span;
            _previous.Clear();
            foreach (uint id in _missingOnce)
            {
                if (id >= first && id < end) _previous.Add(id);
            }
            foreach (uint id in _previous) _missingOnce.Remove(id);
            _missingNow.Clear();
            foreach (uint id in _missing)
            {
                _missingNow.Add(id);
                if (_previous.Contains(id))
                {
                    if (_backoff.TryGetValue(id, out (int Requests, int Wait) b) && b.Wait > 0)
                    {
                        // asked for before and still not here: wait (it stays tracked as missing)
                        _backoff[id] = (b.Requests, b.Wait - 1);
                        _missingOnce.Add(id);
                        BackedOff++;
                        continue;
                    }
                    request.Add(id);
                    Requested++;
                    int requests = b.Requests + 1;
                    if (_backoff.Count < MaxTracked || _backoff.ContainsKey(id)) _backoff[id] = (requests, Math.Min(MaxBackoffRosters, (1 << Math.Min(requests, 7)) - 1));
                }
                else if (_missingOnce.Count < MaxTracked)
                {
                    _missingOnce.Add(id);
                }
            }
            // a mover of this window that arrived (or that the host no longer has) starts over
            _backoffScratch.Clear();
            foreach (uint id in _backoff.Keys)
            {
                if (id >= first && id < end && !_missingNow.Contains(id)) _backoffScratch.Add(id);
            }
            foreach (uint id in _backoffScratch) _backoff.Remove(id);
        }
    }
}
