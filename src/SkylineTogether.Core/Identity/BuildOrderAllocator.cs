using System;
using System.Collections.Generic;
using System.Globalization;

namespace SkylineTogether.Core.Identity
{
    /// <summary>
    /// The host's <c>BuildOrder</c> counter for committed client edges (ARCHITECTURE.md#host-validation-and-commit).
    /// <para>
    /// Background: <c>GenerateEdgesSystem</c> gives every new edge <c>[counter + 16·i, counter + 16·i + 15]</c>
    /// (<c>Game.Tools/GenerateEdgesSystem.cs:1630-1632</c>) and raises its counter past every created edge to
    /// <c>max(Start, End) + 1</c> (<c>UpdateBuildOrderJob</c>, <c>:1919-1942</c>); <c>ResetBuildOrderSystem</c> compacts all
    /// build orders on every load (<c>Game.Serialization/ResetBuildOrderSystem.cs:62-148</c>). A client's predicted
    /// edges carry values from its own counter, so the host re-bases a proposal's ranges onto this allocator before
    /// committing them. Unique values keep node-lane order and zone-block priority deterministic (R13 §2.4, §4.1).
    /// </para>
    /// <para>
    /// <see cref="Next"/> is the monotonic maximum + 1 of every value observed since session start (primed at host
    /// session start, then every captured <c>BuildOrder</c>), so a re-based range never overlaps a live edge's range;
    /// it is also the snapshot manifest's <c>HostBuildOrderNext</c>, from which joiners set their counter (ARCHITECTURE.md#snapshot-join).
    /// The counter saturates at <see cref="uint.MaxValue"/> (<see cref="IsSaturated"/>); a re-base then fails.
    /// </para>
    /// <para>Host main thread. Not thread-safe.</para>
    /// </summary>
    public sealed class BuildOrderAllocator
    {
        private uint _next;
        private bool _saturated;

        /// <summary>One above the largest build-order value observed or assigned; 0 before any.</summary>
        public uint Next => _next;

        /// <summary>True once <see cref="uint.MaxValue"/> itself was observed or assigned: no value above it exists.</summary>
        public bool IsSaturated => _saturated;

        /// <summary>Records one build-order range (either orientation): <c>Next = max(Next, max(start, end) + 1)</c>.</summary>
        public void Observe(uint start, uint end)
        {
            Raise(Math.Max(start, end));
        }

        /// <summary>Records one build-order range.</summary>
        public void Observe(BuildOrderRange range) => Raise(range.Max);

        /// <summary>
        /// Re-bases <paramref name="ranges"/> in place onto values from <see cref="Next"/> upwards and advances
        /// <see cref="Next"/> past them. Ranges are grouped like <c>ResetBuildOrderSystem</c> does (a range whose minimum
        /// lies at or below the running maximum of the previous ones joins their group); groups are laid out
        /// consecutively in ascending order, and inside a group every range keeps its offset to the group start. So
        /// relative order, widths, orientation (inverted ranges stay inverted) and overlaps are preserved, gaps between
        /// groups are removed, and no re-based value lies below <see cref="Next"/>, which is above every observed range.
        /// Throws <see cref="InvalidOperationException"/> (leaving the list unchanged) when the result would exceed
        /// <see cref="uint.MaxValue"/>.
        /// </summary>
        public void Rebase(IList<BuildOrderRange> ranges)
        {
            if (ranges == null) throw new ArgumentNullException(nameof(ranges));
            int n = ranges.Count;
            if (n == 0) return;
            if (_saturated) throw new InvalidOperationException("the build-order space is exhausted (a value of uint.MaxValue is in use)");

            var mins = new uint[n];
            var maxs = new uint[n];
            var order = new int[n];
            for (int i = 0; i < n; i++)
            {
                BuildOrderRange r = ranges[i];
                mins[i] = r.Min;
                maxs[i] = r.Max;
                order[i] = i;
            }
            Array.Sort(order, (a, b) =>
            {
                int c = mins[a].CompareTo(mins[b]);
                if (c != 0) return c;
                c = maxs[a].CompareTo(maxs[b]);
                return c != 0 ? c : a.CompareTo(b);
            });

            var newMins = new ulong[n];
            ulong groupOldMin = mins[order[0]];
            ulong groupOldMax = maxs[order[0]];
            ulong groupNewMin = _next;
            ulong newMax = 0;
            for (int k = 0; k < n; k++)
            {
                int i = order[k];
                if (k > 0)
                {
                    if (mins[i] > groupOldMax)
                    {
                        // a new group starts right after the previous group's re-based maximum
                        groupNewMin += groupOldMax - groupOldMin + 1;
                        groupOldMin = mins[i];
                        groupOldMax = maxs[i];
                    }
                    else if (maxs[i] > groupOldMax)
                    {
                        groupOldMax = maxs[i];
                    }
                }
                ulong rebasedMin = groupNewMin + (mins[i] - groupOldMin);
                ulong rebasedMax = rebasedMin + (maxs[i] - mins[i]);
                if (rebasedMax > uint.MaxValue)
                {
                    throw new InvalidOperationException("re-basing " + n.ToString(CultureInfo.InvariantCulture)
                                                        + " build-order range(s) from " + _next.ToString(CultureInfo.InvariantCulture)
                                                        + " exceeds the build-order space");
                }
                newMins[i] = rebasedMin;
                if (rebasedMax > newMax) newMax = rebasedMax;
            }

            for (int i = 0; i < n; i++)
            {
                uint min = (uint)newMins[i];
                uint width = maxs[i] - mins[i];
                BuildOrderRange r = ranges[i];
                if (r.IsInverted)
                {
                    r.End = min;
                    r.Start = min + width;
                }
                else
                {
                    r.Start = min;
                    r.End = min + width;
                }
                ranges[i] = r;
            }
            Raise((uint)newMax);
        }

        public override string ToString() => "BuildOrderAllocator(next " + _next.ToString(CultureInfo.InvariantCulture) + (_saturated ? ", saturated)" : ")");

        private void Raise(uint max)
        {
            if (max == uint.MaxValue)
            {
                _next = uint.MaxValue;
                _saturated = true;
                return;
            }
            if (max + 1 > _next) _next = max + 1;
        }
    }
}
