using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SkylineTogether.Core.Diagnostics
{
    /// <summary>
    /// The rate limit of the forensics lines (ARCHITECTURE.md#logs): a native crash leaves no managed stack, so the last
    /// lines before it must say what the player's tool and the mod's clean-ups just did, yet a storm of clean-ups must not
    /// flood the file. A token bucket of <see cref="PerSecond"/> lines with a burst of <see cref="Burst"/>; the next line
    /// allowed after a suppression is preceded by a summary of what was left out. Pure: time is passed in. Main thread only.
    /// </summary>
    public sealed class BreadcrumbLimiter
    {
        public double PerSecond = 20;
        public double Burst = 20;

        private double _tokens = double.NaN;
        private double _at = double.NaN;
        private long _suppressed;
        private double _suppressedSince;

        /// <summary>Lines left out in total.</summary>
        public long SuppressedTotal { get; private set; }

        /// <summary>Lines allowed in total.</summary>
        public long Written { get; private set; }

        /// <summary>
        /// Whether a line may be written at <paramref name="now"/> (seconds). When it may and lines were left out before,
        /// <paramref name="summary"/> is the line to write first; otherwise null.
        /// </summary>
        public bool Allow(double now, out string summary)
        {
            summary = null;
            if (double.IsNaN(_at))
            {
                _tokens = Burst;
                _at = now;
            }
            double elapsed = Math.Max(0, now - _at);
            _at = Math.Max(_at, now);
            _tokens = Math.Min(Burst, _tokens + elapsed * PerSecond);
            if (_tokens < 1)
            {
                if (_suppressed == 0) _suppressedSince = now;
                _suppressed++;
                SuppressedTotal++;
                return false;
            }
            _tokens -= 1;
            Written++;
            if (_suppressed > 0)
            {
                summary = _suppressed.ToString(CultureInfo.InvariantCulture) + " forensics line(s) left out in the last "
                          + Math.Max(0, now - _suppressedSince).ToString("0.0", CultureInfo.InvariantCulture) + " s (more than "
                          + PerSecond.ToString("0", CultureInfo.InvariantCulture) + " per second)";
                _suppressed = 0;
            }
            return true;
        }

        /// <summary>The pending summary without a line to follow it (a frame marker or the session end writes it); null when nothing was left out.</summary>
        public string TakeSummary(double now)
        {
            if (_suppressed == 0) return null;
            string s = _suppressed.ToString(CultureInfo.InvariantCulture) + " forensics line(s) left out in the last "
                       + Math.Max(0, now - _suppressedSince).ToString("0.0", CultureInfo.InvariantCulture) + " s";
            _suppressed = 0;
            return s;
        }
    }

    /// <summary>
    /// What one local tool apply changed, by family, for the client's <c>tool apply:</c> line (ARCHITECTURE.md#logs): the
    /// tool's own changes (tagged <c>Applied</c>) and what the game derived from them in the same frame (lanes, sub-objects,
    /// street aggregates). Formats deterministically (families in ordinal order). Pure.
    /// </summary>
    public sealed class ToolApplyTally
    {
        public enum Change : byte
        {
            Created,
            Updated,
            Deleted,
        }

        private readonly SortedDictionary<string, int[]> _tool = new SortedDictionary<string, int[]>(StringComparer.Ordinal);
        private readonly SortedDictionary<string, int[]> _derived = new SortedDictionary<string, int[]>(StringComparer.Ordinal);

        public int ToolTotal { get; private set; }

        public int DerivedTotal { get; private set; }

        public bool IsEmpty => ToolTotal + DerivedTotal == 0;

        public void Clear()
        {
            _tool.Clear();
            _derived.Clear();
            ToolTotal = DerivedTotal = 0;
        }

        /// <summary>Counts one entity: <paramref name="byTool"/> for the tool's own (<c>Applied</c>), otherwise derived by the game.</summary>
        public void Add(string family, Change change, bool byTool)
        {
            SortedDictionary<string, int[]> map = byTool ? _tool : _derived;
            string key = string.IsNullOrEmpty(family) ? "?" : family;
            if (!map.TryGetValue(key, out int[] counts))
            {
                counts = new int[3];
                map[key] = counts;
            }
            counts[(int)change]++;
            if (byTool) ToolTotal++;
            else DerivedTotal++;
        }

        /// <summary>
        /// <c>tool apply: &lt;tool&gt; '&lt;prefab&gt;'; tool +2 NetEdge, -1 NetEdge, ~3 NetNode; game +12 Lane, -6 Lane; proposal 17</c>
        /// (+ created, ~ updated, - deleted; the proposal part only when one was sent).
        /// </summary>
        public string Format(string tool, string prefab, long proposal, int maxFamilies = 12)
        {
            var sb = new StringBuilder(160);
            sb.Append("tool apply: ").Append(string.IsNullOrEmpty(tool) ? "?" : tool);
            if (!string.IsNullOrEmpty(prefab)) sb.Append(" '").Append(prefab).Append('\'');
            sb.Append("; tool ");
            Append(sb, _tool, maxFamilies);
            sb.Append("; game ");
            Append(sb, _derived, maxFamilies);
            if (proposal > 0) sb.Append("; proposal ").Append(proposal.ToString(CultureInfo.InvariantCulture));
            else sb.Append("; nothing proposed");
            return sb.ToString();
        }

        private static void Append(StringBuilder sb, SortedDictionary<string, int[]> map, int maxFamilies)
        {
            if (map.Count == 0)
            {
                sb.Append("nothing");
                return;
            }
            int parts = 0, families = 0;
            foreach (KeyValuePair<string, int[]> kv in map)
            {
                if (families++ >= maxFamilies)
                {
                    sb.Append(", ...");
                    break;
                }
                for (int c = 0; c < 3; c++)
                {
                    if (kv.Value[c] == 0) continue;
                    if (parts++ > 0) sb.Append(", ");
                    sb.Append(c == 0 ? '+' : c == 1 ? '~' : '-').Append(kv.Value[c].ToString(CultureInfo.InvariantCulture)).Append(' ').Append(kv.Key);
                }
            }
        }
    }

    /// <summary>
    /// When the client writes a <c>frame marker</c> line (ARCHITECTURE.md#logs): every <see cref="IntervalSeconds"/> while
    /// the player has a tool other than the default one selected (building), and once when it is put away, so the log's
    /// last marker brackets a native crash in time. Pure: time is passed in.
    /// </summary>
    public sealed class FrameMarkerClock
    {
        public double IntervalSeconds = 5;

        private bool _active;
        private double _last = double.NegativeInfinity;

        /// <summary>
        /// True when a marker is due at <paramref name="now"/>: the first frame of a tool, every interval while it stays
        /// selected, and the frame it is put away (<paramref name="ended"/> then true).
        /// </summary>
        public bool Due(double now, bool toolActive, out bool ended)
        {
            ended = false;
            if (!toolActive)
            {
                if (!_active) return false;
                _active = false;
                ended = true;
                _last = now;
                return true;
            }
            if (!_active || now - _last >= IntervalSeconds)
            {
                _active = true;
                _last = now;
                return true;
            }
            return false;
        }

        public void Reset()
        {
            _active = false;
            _last = double.NegativeInfinity;
        }
    }
}
