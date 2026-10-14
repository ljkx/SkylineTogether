using System.Collections.Generic;

namespace SkylineTogether.Core.Integrity
{
    /// <summary>
    /// The per-slot view of a ledger that bucket details (repair level 1, docs/history/ARCHITECTURE-original.md §8.5) need: which slots of a bucket hold
    /// members of a class, their slot digests (<see cref="MemberHasher.SlotDigest"/>), the class each pair was counted in,
    /// and the epochs of the last changes (so a detail or beacon older than a local change is not mistaken for a
    /// divergence). <see cref="HashLedger"/> implements it; <see cref="RepairCoordinator"/> uses it when its ledger offers
    /// it and degrades to whole-bucket entity re-sends otherwise.
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public interface IHashLedgerDetail : IHashLedger
    {
        /// <summary>One above the highest bucket of the class that ever held a member.</summary>
        int KnownBucketCount(ReplicationClass cls);

        /// <summary>Present members of the class (pairs and extra members).</summary>
        long TotalCount(ReplicationClass cls);

        /// <summary>Appends, in ascending order, the slots of the bucket that hold at least one member of the class; returns how many.</summary>
        int CollectBucketSlots(ReplicationClass cls, int bucket, List<NetSlot> output);

        /// <summary>The slot's digest over its members of the class; false when it has none.</summary>
        bool TryGetSlotDigest(ReplicationClass cls, NetSlot slot, out uint digest);

        /// <summary>The class a recorded pair is counted in; false when the pair is not recorded.</summary>
        bool TryGetClass(NetSlot slot, TypeRank type, out ReplicationClass cls);

        /// <summary>The epoch of the last change of the slot (<see cref="EpochId.None"/> when unknown).</summary>
        EpochId SlotModifiedEpoch(NetSlot slot);

        /// <summary>The epoch of the last change in the bucket of the class (<see cref="EpochId.None"/> when unknown).</summary>
        EpochId BucketModifiedEpoch(ReplicationClass cls, int bucket);
    }
}
