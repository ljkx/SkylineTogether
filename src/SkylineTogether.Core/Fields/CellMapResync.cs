using System;
using System.Collections.Generic;

namespace SkylineTogether.Core.Fields
{
    /// <summary>The valid range of one 16-bit little-endian field of a cell (<see cref="CellMapMessage.ClampFields"/>).</summary>
    public struct CellFieldClamp
    {
        public int Offset;
        public bool Signed;
        public int Min;
        public int Max;

        public CellFieldClamp(int offset, bool signed, int min, int max)
        {
            Offset = offset;
            Signed = signed;
            Min = min;
            Max = max;
        }
    }

    /// <summary>
    /// Paces cell-map resync requests (<see cref="CellMapMessage.TypeResync"/>). Host side: one served request
    /// per peer and map per <see cref="HostInterval"/>. Client side: after a request, the next one for the same map waits
    /// an interval that doubles while the map keeps differing (up to <see cref="MaxClientInterval"/>) and starts over
    /// once it matches. Not thread-safe.
    /// </summary>
    public sealed class CellMapResyncGate
    {
        public const double HostInterval = 20.0;
        public const double MinClientInterval = 30.0;
        public const double MaxClientInterval = 600.0;

        private readonly Dictionary<ulong, double> _hostNext = new Dictionary<ulong, double>();
        private readonly List<ulong> _dropScratch = new List<ulong>();
        private readonly Dictionary<uint, double> _clientInterval = new Dictionary<uint, double>();
        private readonly Dictionary<uint, double> _clientNext = new Dictionary<uint, double>();

        /// <summary>Host: whether a resync request of <paramref name="peer"/> for <paramref name="mapId"/> is served now.</summary>
        public bool TryAcceptHost(byte peer, uint mapId, double now)
        {
            ulong key = ((ulong)peer << 32) | mapId;
            if (_hostNext.TryGetValue(key, out double next) && now < next) return false;
            _hostNext[key] = now + HostInterval;
            return true;
        }

        /// <summary>Host: forgets a peer that left.</summary>
        public void ForgetPeer(byte peer)
        {
            _dropScratch.Clear();
            foreach (ulong key in _hostNext.Keys)
            {
                if ((byte)(key >> 32) == peer) _dropScratch.Add(key);
            }
            foreach (ulong key in _dropScratch) _hostNext.Remove(key);
        }

        /// <summary>Client: the map's digest differed; true when a request may go out now (the next one then waits longer).</summary>
        public bool TryRequestClient(uint mapId, double now)
        {
            if (_clientNext.TryGetValue(mapId, out double next) && now < next) return false;
            double interval = _clientInterval.TryGetValue(mapId, out double known) ? Math.Min(MaxClientInterval, known * 2) : MinClientInterval;
            _clientInterval[mapId] = interval;
            _clientNext[mapId] = now + interval;
            return true;
        }

        /// <summary>Client: the map's digest matched; the back-off starts over.</summary>
        public void ClientMatched(uint mapId) => _clientInterval.Remove(mapId);

        /// <summary>Client: the current back-off of <paramref name="mapId"/> (0 when the map matched last).</summary>
        public double ClientInterval(uint mapId) => _clientInterval.TryGetValue(mapId, out double v) ? v : 0;

        public void Clear()
        {
            _hostNext.Clear();
            _clientInterval.Clear();
            _clientNext.Clear();
        }
    }
}
