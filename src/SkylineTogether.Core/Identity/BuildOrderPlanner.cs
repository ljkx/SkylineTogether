using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Identity
{
    /// <summary>
    /// Which build-order ranges of a client's newly created edges the host re-bases (ARCH §5.4.3). A fresh edge got
    /// its range from the client's own <c>GenerateEdgesSystem</c> counter, which may collide with the host's; it is
    /// re-based onto <see cref="BuildOrderAllocator"/>. An edge created by splitting or replacing an existing edge got
    /// a sub-range of that edge's host range (<c>GenerateEdgesSystem.cs:905-915</c>), which keeps the original zoning
    /// priority and must stay as it is. A spawned range that lies inside the range of an existing host edge the
    /// proposal deletes or changes is therefore inherited; every other one is fresh.
    /// </summary>
    public static class BuildOrderPlanner
    {
        /// <summary>For every spawned range, whether it is fresh (to be re-based).</summary>
        public static bool[] SelectFresh(IReadOnlyList<BuildOrderRange> spawned, IReadOnlyList<BuildOrderRange> touchedHostEdges)
        {
            if (spawned == null) throw new ArgumentNullException(nameof(spawned));
            var fresh = new bool[spawned.Count];
            for (int i = 0; i < spawned.Count; i++)
            {
                BuildOrderRange s = spawned[i];
                bool inherited = false;
                if (touchedHostEdges != null)
                {
                    foreach (BuildOrderRange h in touchedHostEdges)
                    {
                        if (h.Min <= s.Min && s.Max <= h.Max)
                        {
                            inherited = true;
                            break;
                        }
                    }
                }
                fresh[i] = !inherited;
            }
            return fresh;
        }

        /// <summary>
        /// Re-bases the fresh ranges of <paramref name="spawned"/> in place (relative order and widths preserved) after
        /// raising the allocator to the host's live counter <paramref name="hostCounter"/> (the next value vanilla would
        /// assign; 0 when unknown). Returns how many ranges were re-based.
        /// </summary>
        public static int RebaseFresh(BuildOrderAllocator allocator, IList<BuildOrderRange> spawned, IReadOnlyList<BuildOrderRange> touchedHostEdges, uint hostCounter)
        {
            if (allocator == null) throw new ArgumentNullException(nameof(allocator));
            if (hostCounter > 0) allocator.Observe(hostCounter - 1, hostCounter - 1);
            var list = new List<BuildOrderRange>(spawned);
            bool[] fresh = SelectFresh(list, touchedHostEdges);
            var picked = new List<BuildOrderRange>();
            var index = new List<int>();
            for (int i = 0; i < fresh.Length; i++)
            {
                if (!fresh[i]) continue;
                picked.Add(list[i]);
                index.Add(i);
            }
            if (picked.Count == 0) return 0;
            allocator.Rebase(picked);
            for (int k = 0; k < picked.Count; k++) spawned[index[k]] = picked[k];
            return picked.Count;
        }
    }
}
