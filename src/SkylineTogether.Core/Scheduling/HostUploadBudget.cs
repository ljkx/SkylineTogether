using System;
using System.Globalization;

namespace SkylineTogether.Core.Scheduling
{
    /// <summary>
    /// The host's total upload cap (session setting "upload budget", default 1.5 MB/s, ARCH §4.8.2) split across the
    /// peers. The split is max-min fair over the peers' demands (water filling): every peer gets an equal share, a peer
    /// that wants less gets its demand, and what it leaves is divided among the others; capacity that remains after every
    /// demand is met is spread evenly on top, so the shares always add up to the total (work-conserving: an unused share
    /// flows to the others). The order of peers with equal demand is by peer id, so the result is deterministic.
    /// <para>
    /// <see cref="SetDemand"/> records demands; <see cref="Recompute"/> (once per frame, <c>PeerScheduler.BeginFrame</c>)
    /// turns them into shares; <see cref="ShareOf"/> reads them. A peer added since the last recompute is estimated at an
    /// equal share until the next one. A total of 0 or less means "no host cap" (every share is <see cref="int.MaxValue"/>).
    /// Thread-safe: demands are set from the per-peer scheduler workers.
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class HostUploadBudget
    {
        private readonly object _lock = new object();
        private readonly int[] _demand = new int[256];
        private readonly int[] _share = new int[256];
        private readonly bool[] _present = new bool[256];
        private readonly bool[] _computed = new bool[256];
        private readonly byte[] _order = new byte[256];
        private int _count;
        private int _total;

        /// <param name="totalBytesPerSecond">The host's total upload cap; 0 or less = unlimited.</param>
        public HostUploadBudget(int totalBytesPerSecond)
        {
            _total = totalBytesPerSecond;
        }

        /// <summary>The total cap in bytes per second (0 or less = unlimited); applied at the next <see cref="Recompute"/>.</summary>
        public int TotalBytesPerSecond
        {
            get { lock (_lock) return _total; }
            set { lock (_lock) _total = value; }
        }

        /// <summary>Peers that have a demand registered.</summary>
        public int PeerCount
        {
            get { lock (_lock) return _count; }
        }

        /// <summary>Registers (or updates) a peer's wanted rate; negative values count as 0.</summary>
        public void SetDemand(PeerId peer, int wantedBytesPerSecond)
        {
            int i = peer.Value;
            lock (_lock)
            {
                if (!_present[i])
                {
                    _present[i] = true;
                    _computed[i] = false;
                    _count++;
                }
                _demand[i] = wantedBytesPerSecond > 0 ? wantedBytesPerSecond : 0;
            }
        }

        /// <summary>Forgets a peer; its share flows to the others at the next <see cref="Recompute"/>.</summary>
        public void RemovePeer(PeerId peer)
        {
            int i = peer.Value;
            lock (_lock)
            {
                if (!_present[i]) return;
                _present[i] = false;
                _computed[i] = false;
                _demand[i] = 0;
                _share[i] = 0;
                _count--;
            }
        }

        /// <summary>The last registered demand of a peer (0 if unknown).</summary>
        public int DemandOf(PeerId peer)
        {
            lock (_lock) return _present[peer.Value] ? _demand[peer.Value] : 0;
        }

        /// <summary>
        /// The peer's share from the last <see cref="Recompute"/>. For a peer registered since then, or not registered at
        /// all, an equal share of the total (as if it joined the current peers); <see cref="int.MaxValue"/> without a cap.
        /// </summary>
        public int ShareOf(PeerId peer)
        {
            int i = peer.Value;
            lock (_lock)
            {
                if (_total <= 0) return int.MaxValue;
                if (_present[i] && _computed[i]) return _share[i];
                int n = _present[i] ? Math.Max(1, _count) : _count + 1;
                return _total / n;
            }
        }

        /// <summary>Recomputes every share from the registered demands (max-min fair, surplus spread evenly).</summary>
        public void Recompute()
        {
            lock (_lock)
            {
                int n = 0;
                for (int i = 0; i < 256; i++)
                {
                    if (_present[i]) _order[n++] = (byte)i;
                }
                if (n == 0) return;
                if (_total <= 0)
                {
                    for (int k = 0; k < n; k++)
                    {
                        _share[_order[k]] = int.MaxValue;
                        _computed[_order[k]] = true;
                    }
                    return;
                }

                // insertion sort by (demand, peer id); n ≤ 256 and nearly sorted from frame to frame
                for (int k = 1; k < n; k++)
                {
                    byte item = _order[k];
                    int j = k - 1;
                    while (j >= 0 && Less(item, _order[j]))
                    {
                        _order[j + 1] = _order[j];
                        j--;
                    }
                    _order[j + 1] = item;
                }

                long remaining = _total;
                for (int k = 0; k < n; k++)
                {
                    int p = _order[k];
                    long fair = remaining / (n - k);
                    long give = Math.Min(_demand[p], fair);
                    _share[p] = (int)give;
                    remaining -= give;
                }
                if (remaining > 0)
                {
                    // every demand is met: spread the surplus evenly (by peer id for the remainder bytes)
                    long extra = remaining / n;
                    long rest = remaining % n;
                    int given = 0;
                    for (int i = 0; i < 256; i++)
                    {
                        if (!_present[i]) continue;
                        long share = _share[i] + extra + (given < rest ? 1 : 0);
                        _share[i] = share >= int.MaxValue ? int.MaxValue : (int)share;
                        given++;
                    }
                }
                for (int k = 0; k < n; k++) _computed[_order[k]] = true;
            }
        }

        public override string ToString()
        {
            lock (_lock)
            {
                return "host upload " + (_total > 0 ? _total.ToString(CultureInfo.InvariantCulture) + " B/s" : "unlimited")
                       + " over " + _count.ToString(CultureInfo.InvariantCulture) + " peer(s)";
            }
        }

        private bool Less(byte a, byte b)
        {
            int da = _demand[a], db = _demand[b];
            return da < db || (da == db && a < b);
        }
    }
}
