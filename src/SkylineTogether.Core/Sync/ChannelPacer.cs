using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// w3/audit: per-channel minimum intervals of the host's global-state channels. The global capture runs at 1 Hz;
    /// panel statistics that change with every simulation update (happiness factors, company counts) would then resend
    /// several kilobytes a second, so each channel may ask for a longer interval. A dirty channel (a joiner, a change
    /// the host just applied) is always due. Not thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public sealed class ChannelPacer
    {
        private readonly Dictionary<uint, double> _last = new Dictionary<uint, double>();

        /// <summary>
        /// True when <paramref name="channel"/> may be offered at <paramref name="now"/>: it is dirty, was never offered,
        /// or its last offer is at least <paramref name="interval"/> seconds old. A due channel counts as offered.
        /// </summary>
        public bool Due(uint channel, double interval, double now, bool dirty)
        {
            if (!dirty && interval > 0 && _last.TryGetValue(channel, out double last) && now - last < interval) return false;
            _last[channel] = now;
            return true;
        }

        public void Clear() => _last.Clear();
    }

    /// <summary>w3/audit: the members to add and to remove so a local set equals a received one.</summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class IdSetDiff
    {
        /// <summary>
        /// Fills <paramref name="add"/> with the ids of <paramref name="target"/> missing from <paramref name="current"/>,
        /// and <paramref name="remove"/> with the ids of <paramref name="current"/> missing from <paramref name="target"/>
        /// (both ascending).
        /// </summary>
        public static void Compute(IEnumerable<uint> current, IEnumerable<uint> target, List<uint> add, List<uint> remove)
        {
            if (add == null) throw new ArgumentNullException(nameof(add));
            if (remove == null) throw new ArgumentNullException(nameof(remove));
            add.Clear();
            remove.Clear();
            var have = current == null ? new HashSet<uint>() : new HashSet<uint>(current);
            var want = target == null ? new HashSet<uint>() : new HashSet<uint>(target);
            foreach (uint id in want)
            {
                if (!have.Contains(id)) add.Add(id);
            }
            foreach (uint id in have)
            {
                if (!want.Contains(id)) remove.Add(id);
            }
            add.Sort();
            remove.Sort();
        }
    }
}
