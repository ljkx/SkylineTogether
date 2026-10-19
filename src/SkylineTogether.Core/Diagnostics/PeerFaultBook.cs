using System.Collections.Generic;
using System.Globalization;

namespace SkylineTogether.Core.Diagnostics
{
    /// <summary>What <see cref="PeerFaultBook.Report"/> asks the caller to do.</summary>
    public struct PeerFaultVerdict
    {
        /// <summary>Write this failure to the log (rate-limited per peer).</summary>
        public bool Log;

        /// <summary>Failures of the peer not logged since the last logged one.</summary>
        public int Suppressed;

        /// <summary>This failure muted the peer: its input of this kind is ignored from now on.</summary>
        public bool JustMuted;
    }

    /// <summary>
    /// Host: failures caused by one peer's input to a shared feature (ARCHITECTURE.md#feature-health-and-guards). A feature
    /// that serves every player (terrain sync, for one) must not be switched off because one client keeps sending input
    /// the host cannot apply (another mod's prefab, a mismatched asset, a hostile client): each peer's failures are counted
    /// on their own, with the same rate-limited logging and the same trip rule as a feature
    /// (<see cref="FeatureHealth.TripCount"/> within <see cref="FeatureHealth.TripWindowSeconds"/>), and a peer that trips is
    /// muted until it leaves. Pure (times are passed in). Main thread.
    /// </summary>
    public sealed class PeerFaultBook
    {
        private readonly Dictionary<byte, FeatureHealth> _peers = new Dictionary<byte, FeatureHealth>();

        /// <summary>Peers muted so far in this session (diagnostics).</summary>
        public int MutedCount
        {
            get
            {
                int n = 0;
                foreach (FeatureHealth h in _peers.Values)
                {
                    if (h.Tripped) n++;
                }
                return n;
            }
        }

        /// <summary>Whether the peer's input is ignored.</summary>
        public bool IsMuted(byte peer) => _peers.TryGetValue(peer, out FeatureHealth h) && h.Tripped;

        /// <summary>Records one failure of the peer's input at <paramref name="now"/> (seconds).</summary>
        public PeerFaultVerdict Report(byte peer, double now)
        {
            if (!_peers.TryGetValue(peer, out FeatureHealth h))
            {
                string id = peer.ToString(CultureInfo.InvariantCulture);
                _peers[peer] = h = new FeatureHealth("peer." + id, "input of peer " + id, false);
            }
            FailureVerdict v = h.ReportFailure(null, now);
            return new PeerFaultVerdict { Log = v.Log, Suppressed = v.Suppressed, JustMuted = v.JustTripped };
        }

        /// <summary>The peer left: a later peer with the same number starts clean.</summary>
        public void Forget(byte peer) => _peers.Remove(peer);

        public void Clear() => _peers.Clear();
    }
}
