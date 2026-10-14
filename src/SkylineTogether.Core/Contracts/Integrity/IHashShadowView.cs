using System.Collections.Generic;

namespace SkylineTogether.Core
{
    /// <summary>Read view of the host's hash shadow (core-replication <c>HashShadow</c>) for the repair responder.</summary>
    public interface IHashShadowView
    {
        bool TryGetCanonical(NetSlot slot, TypeRank type, out uint hash);

        /// <summary>Per-slot combined member hashes of a bucket (bucket details, docs/history/ARCHITECTURE-original.md §8.5 level 1).</summary>
        void CollectBucket(ReplicationClass cls, int bucket, List<NetSlot> slots, List<uint> combinedHashes);

        ReplicationClass ClassOf(NetSlot slot);
    }
}
