using System;
using System.Collections.Generic;
using System.Text;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// Client: the frame budget of the reference sweep (ARCHITECTURE.md#reference-audit). A lap over the whole link graph
    /// must finish within <see cref="LapSeconds"/>: each frame gets <see cref="MinMs"/>, and more (up to
    /// <see cref="MaxMs"/>) while the lap is behind its schedule, so a large city is covered in bounded time without a
    /// fixed high cost in a small one. Progress is the share of the lap's work units (chunks) already visited.
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public sealed class SweepPacer
    {
        private double _lapStart = double.NaN;

        public SweepPacer(double lapSeconds, double minMs, double maxMs)
        {
            if (lapSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(lapSeconds));
            if (minMs <= 0 || maxMs < minMs) throw new ArgumentOutOfRangeException(nameof(maxMs));
            LapSeconds = lapSeconds;
            MinMs = minMs;
            MaxMs = maxMs;
        }

        public double LapSeconds { get; private set; }

        public double MinMs { get; }

        public double MaxMs { get; }

        /// <summary>Completed laps.</summary>
        public long Laps { get; private set; }

        /// <summary>Duration of the last completed lap in seconds (0 before the first).</summary>
        public double LastLapSeconds { get; private set; }

        /// <summary>Longest completed lap in seconds.</summary>
        public double LongestLapSeconds { get; private set; }

        /// <summary>True while a lap is running.</summary>
        public bool InLap => !double.IsNaN(_lapStart);

        /// <summary>Starts a lap (the first one, or the next after <see cref="LapDone"/>); <paramref name="lapSeconds"/> &gt; 0 changes the target.</summary>
        public void StartLap(double now, double lapSeconds = 0)
        {
            if (lapSeconds > 0) LapSeconds = lapSeconds;
            _lapStart = now;
        }

        /// <summary>The lap is complete: records its duration; the next lap starts with <see cref="StartLap"/>.</summary>
        public void LapDone(double now)
        {
            if (double.IsNaN(_lapStart)) return;
            LastLapSeconds = Math.Max(0, now - _lapStart);
            if (LastLapSeconds > LongestLapSeconds) LongestLapSeconds = LastLapSeconds;
            Laps++;
            _lapStart = double.NaN;
        }

        /// <summary>
        /// This frame's budget in milliseconds for a lap <paramref name="progress"/> (0..1) done. On schedule (progress at
        /// least the elapsed share of the lap): <see cref="MinMs"/>; behind: proportionally more, <see cref="MaxMs"/> once the
        /// deficit reaches a quarter of the lap or the lap is overdue.
        /// </summary>
        public double BudgetMs(double now, double progress)
        {
            if (double.IsNaN(_lapStart)) StartLap(now);
            if (double.IsNaN(progress) || progress < 0) progress = 0;
            if (progress > 1) progress = 1;
            double elapsed = Math.Max(0, now - _lapStart);
            double due = elapsed / LapSeconds;
            if (due >= 1) return MaxMs; // overdue
            double deficit = due - progress;
            if (deficit <= 0) return MinMs;
            double share = Math.Min(1.0, deficit * 4.0);
            return MinMs + (MaxMs - MinMs) * share;
        }

        public void Reset()
        {
            _lapStart = double.NaN;
            Laps = 0;
            LastLapSeconds = 0;
            LongestLapSeconds = 0;
        }
    }

    /// <summary>
    /// Client: counters and log gating of reference repairs (ARCHITECTURE.md#reference-audit). Every repair is counted
    /// per link; at most <see cref="LinesPerLinkPerMinute"/> detail lines per link and minute are logged, and a summary of
    /// the repairs of the last minute (<see cref="TakeSummary"/>) keeps the log short when a large deletion repairs
    /// thousands of references at once.
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public sealed class LinkRepairStats
    {
        public const int LinesPerLinkPerMinute = 3;
        public const double SummarySeconds = 60;

        private readonly long[] _found;
        private readonly long[] _repaired;
        private readonly long[] _minute;
        private readonly int[] _logged;
        private readonly double[] _windowStart;
        private double _summaryStart = double.NaN;
        private long _sinceSummary;

        public LinkRepairStats(int links)
        {
            if (links <= 0) throw new ArgumentOutOfRangeException(nameof(links));
            _found = new long[links];
            _repaired = new long[links];
            _minute = new long[links];
            _logged = new int[links];
            _windowStart = new double[links];
            for (int i = 0; i < links; i++) _windowStart[i] = double.NegativeInfinity;
        }

        public int Links => _found.Length;

        /// <summary>Dangling references found this session (repaired or only reported).</summary>
        public long Found { get; private set; }

        /// <summary>References repaired this session (elements dropped, references cleared, components removed).</summary>
        public long Repaired { get; private set; }

        /// <summary>Repairs made right after an apply (the targeted pass), a part of <see cref="Repaired"/>.</summary>
        public long RepairedAfterApply { get; private set; }

        /// <summary>Repair requests sent to the host for the holders.</summary>
        public long Requested { get; private set; }

        public long FoundOf(int link) => _found[link];

        public long RepairedOf(int link) => _repaired[link];

        /// <summary>
        /// Counts <paramref name="count"/> dangling references of <paramref name="link"/> found on one holder; returns true
        /// when its detail line may be logged now (one line per holder at most).
        /// </summary>
        public bool Note(int link, int count, bool repaired, bool afterApply, double now)
        {
            if (count <= 0) return false;
            _found[link] += count;
            Found += count;
            if (repaired)
            {
                _repaired[link] += count;
                _minute[link] += count;
                Repaired += count;
                _sinceSummary += count;
                if (afterApply) RepairedAfterApply += count;
            }
            if (double.IsNaN(_summaryStart)) _summaryStart = now;
            if (now - _windowStart[link] >= 60)
            {
                _windowStart[link] = now;
                _logged[link] = 0;
            }
            if (_logged[link] >= LinesPerLinkPerMinute) return false;
            _logged[link]++;
            return true;
        }

        public void NoteRequested(int count = 1) => Requested += count;

        /// <summary>
        /// The summary of the repairs since the last summary (null while nothing was repaired or the minute is not over):
        /// "N in the last 60 s (name a, name b ...)". <paramref name="name"/> names a link.
        /// </summary>
        public string TakeSummary(double now, Func<int, string> name)
        {
            if (double.IsNaN(_summaryStart) || now - _summaryStart < SummarySeconds) return null;
            double span = now - _summaryStart;
            _summaryStart = now;
            if (_sinceSummary == 0) return null;
            var parts = new List<string>();
            for (int i = 0; i < _minute.Length; i++)
            {
                if (_minute[i] == 0) continue;
                parts.Add((name != null ? name(i) : i.ToString(System.Globalization.CultureInfo.InvariantCulture)) + " " + _minute[i]);
                _minute[i] = 0;
            }
            var text = new StringBuilder();
            text.Append(_sinceSummary).Append(" in the last ").Append(((int)Math.Round(span)).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" s (")
                .Append(string.Join(", ", parts)).Append(')');
            _sinceSummary = 0;
            return text.ToString();
        }

        public void Reset()
        {
            Array.Clear(_found, 0, _found.Length);
            Array.Clear(_repaired, 0, _repaired.Length);
            Array.Clear(_minute, 0, _minute.Length);
            Array.Clear(_logged, 0, _logged.Length);
            for (int i = 0; i < _windowStart.Length; i++) _windowStart[i] = double.NegativeInfinity;
            _summaryStart = double.NaN;
            _sinceSummary = 0;
            Found = Repaired = RepairedAfterApply = Requested = 0;
        }
    }
    /// <summary>
    /// Client: ids to ask the host for after a reference repair (the holder, so the host's own state replaces the
    /// clean-up) or after a reference to an id this client does not have (the missing agent). Each id waits
    /// <see cref="DelaySeconds"/> first, because the record that brings it is often already queued behind the current
    /// batch; ids are deduplicated and the queue is bounded. Not thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public sealed class RepairRequestQueue
    {
        /// <summary>Requests per id within <see cref="WindowSeconds"/> at most (an id that never arrives, such as an object of a missing asset, is not asked for forever).</summary>
        public const int MaxPerWindow = 3;
        public const double WindowSeconds = 300;

        private readonly Queue<(uint Id, double Due)> _queue = new Queue<(uint, double)>();
        private readonly HashSet<uint> _queued = new HashSet<uint>();
        private readonly Dictionary<uint, (double Start, int Count)> _history = new Dictionary<uint, (double, int)>();
        private readonly List<uint> _expired = new List<uint>();

        public RepairRequestQueue(double delaySeconds, int capacity)
        {
            if (delaySeconds < 0) throw new ArgumentOutOfRangeException(nameof(delaySeconds));
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            DelaySeconds = delaySeconds;
            Capacity = capacity;
        }

        public double DelaySeconds { get; }

        public int Capacity { get; }

        public int Count => _queue.Count;

        /// <summary>Ids not queued because the queue was full.</summary>
        public long Dropped { get; private set; }

        /// <summary>Ids not queued because they were asked for <see cref="MaxPerWindow"/> times within the window.</summary>
        public long Refused { get; private set; }

        /// <summary>Queues <paramref name="id"/> (once); false for 0, an id already queued, one asked for too often or a full queue.</summary>
        public bool Add(uint id, double now)
        {
            if (id == 0 || _queued.Contains(id)) return false;
            if (_history.TryGetValue(id, out (double Start, int Count) h) && now - h.Start < WindowSeconds && h.Count >= MaxPerWindow)
            {
                Refused++;
                return false;
            }
            if (_queue.Count >= Capacity)
            {
                Dropped++;
                return false;
            }
            _history[id] = h.Count == 0 || now - h.Start >= WindowSeconds ? (now, 1) : (h.Start, h.Count + 1);
            _queued.Add(id);
            _queue.Enqueue((id, now + DelaySeconds));
            if (_history.Count > Capacity * 4) Sweep(now);
            return true;
        }

        private void Sweep(double now)
        {
            _expired.Clear();
            foreach (KeyValuePair<uint, (double Start, int Count)> pair in _history)
            {
                if (now - pair.Value.Start >= WindowSeconds && !_queued.Contains(pair.Key)) _expired.Add(pair.Key);
            }
            foreach (uint id in _expired) _history.Remove(id);
        }

        /// <summary>
        /// Moves up to <paramref name="max"/> due ids that <paramref name="stillNeeded"/> accepts (null: all) into
        /// <paramref name="output"/>; due ids it refuses are dropped. Returns the number added.
        /// </summary>
        public int TakeDue(double now, int max, Func<uint, bool> stillNeeded, List<uint> output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            int added = 0;
            while (added < max && _queue.Count > 0 && _queue.Peek().Due <= now)
            {
                uint id = _queue.Dequeue().Id;
                _queued.Remove(id);
                if (stillNeeded != null && !stillNeeded(id)) continue;
                output.Add(id);
                added++;
            }
            return added;
        }

        public void Clear()
        {
            _queue.Clear();
            _queued.Clear();
            _history.Clear();
            Dropped = Refused = 0;
        }
    }

    /// <summary>
    /// Client: whether the holder of a repaired agent link is asked from the host again (ARCHITECTURE.md#reference-audit).
    /// The host's record replaces the client's clean-up only where it can carry something the client lacks.
    /// </summary>
    public static class RepairRequestPolicy
    {
        /// <summary>
        /// A cleared or removed forward link: yes (the holder may have moved on on the host). A reverse list: only an
        /// agent's (a building's list follows its renters' own records), and only when an element named an entity that was
        /// destroyed or deleted here (<paramref name="goneElements"/>), which means the list was out of date. Elements naming
        /// nobody are what a save writes for a member that is already gone: the host's list
        /// holds the same gone member, so its record would bring nothing, and a join snapshot has many such lists (a real
        /// session asked for 29 companies within 15 s of going live, each answered with a full record).
        /// </summary>
        public static bool AskHolder(bool buffer, bool agentHolder, int goneElements)
        {
            if (!buffer) return true;
            if (!agentHolder) return false;
            return goneElements > 0;
        }
    }
}
