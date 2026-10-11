using System.Collections.Generic;

namespace SkylineTogether.Core
{
    /// <summary>
    /// Produces HASH beacons for a peer's message (ARCH §8.4): only for enabled classes and only for buckets in which
    /// the peer has nothing dirty. Implemented by core-integrity's <c>BeaconPlanner</c>.
    /// </summary>
    public interface IBeaconSource
    {
        /// <summary>Appends at most <paramref name="maxBeacons"/> beacons; returns the number appended.</summary>
        int AppendBeacons(PeerId peer, IPeerDirtyView dirty, List<HashBeacon> output, int maxBeacons);

        /// <summary>Enables a class for a peer after its first full scan cycle past the join (high rate during verification).</summary>
        void EnableClass(PeerId peer, ReplicationClass cls, bool highRate);
    }
}
