using System;
using System.Collections.Generic;
using System.Text;

namespace SkylineTogether.Core.Integrity
{
    /// <summary>
    /// Client-side decisions of the UI-level line check (ARCH §4.11.6, §8.6 "Line state at UI level"): a line whose
    /// <see cref="LineStateRow"/> differs from the host's in the same way in two consecutive comparisons is re-sent (route,
    /// waypoints, segments); after <see cref="ResendsBeforeTolerance"/> re-sends that did not help, the line is tolerated
    /// for <see cref="ToleranceSeconds"/> and logged with the differing fields, so a value that cannot replicate never
    /// causes an endless re-send loop. Requiring the same difference twice keeps values in flux (a path result that is
    /// still on its way) from triggering re-sends. Single-threaded; time in seconds.
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public sealed class LineCheckTracker
    {
        /// <summary>Re-sends of one line before it is tolerated.</summary>
        public int ResendsBeforeTolerance = 2;

        /// <summary>How long a tolerated line is not re-sent (seconds).</summary>
        public double ToleranceSeconds = 600;

        /// <summary>Lines re-sent per comparison at most.</summary>
        public int MaxResendsPerCheck = 16;

        private readonly Dictionary<uint, Entry> _lines = new Dictionary<uint, Entry>();

        private sealed class Entry
        {
            public string Signature;
            public int Seen;
            public int Attempts;
            public double ToleratedUntil;
        }

        /// <summary>What one comparison asks the caller to do.</summary>
        public sealed class Outcome
        {
            /// <summary>Lines (net ids of routes) to re-send.</summary>
            public readonly List<uint> Resend = new List<uint>();

            /// <summary>Log lines (one per re-sent or newly tolerated line).</summary>
            public readonly List<string> Log = new List<string>();

            /// <summary>Lines that differ in this comparison (tolerated ones included).</summary>
            public int Differing;

            /// <summary>Lines currently tolerated.</summary>
            public int Tolerated;

            /// <summary>Re-sent lines that match now.</summary>
            public int Fixed;

            public void Clear()
            {
                Resend.Clear();
                Log.Clear();
                Differing = 0;
                Tolerated = 0;
                Fixed = 0;
            }
        }

        /// <summary>Lines re-sent since creation.</summary>
        public long ResendsRequested { get; private set; }

        /// <summary>Evaluates one comparison (the differences of <see cref="LineStateReport.Compare"/>; lines without an entry match).</summary>
        public void Observe(IReadOnlyList<LineStateDifference> differences, double now, Outcome outcome)
        {
            if (differences == null) throw new ArgumentNullException(nameof(differences));
            if (outcome == null) throw new ArgumentNullException(nameof(outcome));
            outcome.Clear();
            var byLine = new SortedDictionary<uint, List<LineStateDifference>>();
            foreach (LineStateDifference d in differences)
            {
                if (d == null) continue;
                if (!byLine.TryGetValue(d.Line.Value, out List<LineStateDifference> list)) byLine[d.Line.Value] = list = new List<LineStateDifference>();
                list.Add(d);
            }

            // lines that match now
            var resolved = new List<uint>();
            foreach (KeyValuePair<uint, Entry> pair in _lines)
            {
                if (byLine.ContainsKey(pair.Key)) continue;
                if (pair.Value.Attempts > 0) outcome.Fixed++;
                resolved.Add(pair.Key);
            }
            foreach (uint line in resolved) _lines.Remove(line);

            foreach (KeyValuePair<uint, List<LineStateDifference>> pair in byLine)
            {
                outcome.Differing++;
                string signature = Signature(pair.Value);
                if (!_lines.TryGetValue(pair.Key, out Entry e)) _lines[pair.Key] = e = new Entry();
                if (e.ToleratedUntil > now) continue;
                if (e.ToleratedUntil != 0)
                {
                    e.ToleratedUntil = 0;
                    e.Attempts = 0;
                    e.Seen = 0;
                }
                if (e.Seen == 0 || e.Signature != signature)
                {
                    e.Signature = signature;
                    e.Seen = 1;
                    continue;
                }
                if (e.Attempts >= ResendsBeforeTolerance)
                {
                    e.ToleratedUntil = now + ToleranceSeconds;
                    outcome.Log.Add("line #" + pair.Key + " still differs after " + e.Attempts + " re-sends; not re-sent for " + (int)(ToleranceSeconds / 60) + " min: " + signature);
                    continue;
                }
                if (outcome.Resend.Count >= MaxResendsPerCheck) continue;
                e.Attempts++;
                e.Seen = 0;
                ResendsRequested++;
                outcome.Resend.Add(pair.Key);
                outcome.Log.Add("line #" + pair.Key + " differs from the host (" + signature + "); re-sending it");
            }
            foreach (Entry e in _lines.Values)
            {
                if (e.ToleratedUntil > now) outcome.Tolerated++;
            }
        }

        /// <summary>The differing fields of one line as text, e.g. <c>Stops host 4 / here 3; BrokenSegmentMask host none / here 2</c>.</summary>
        private static string Signature(List<LineStateDifference> diffs)
        {
            diffs.Sort((a, b) => string.CompareOrdinal(a.Field, b.Field));
            var sb = new StringBuilder();
            foreach (LineStateDifference d in diffs)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(d.Field).Append(" host ").Append(d.HostValue).Append(" / here ").Append(d.ClientValue);
            }
            return sb.ToString();
        }

        public void Clear() => _lines.Clear();
    }
}
