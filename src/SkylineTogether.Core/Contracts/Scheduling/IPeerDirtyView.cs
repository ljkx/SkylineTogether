namespace SkylineTogether.Core
{
    /// <summary>
    /// Read view of one peer's dirty set (docs/history/ARCHITECTURE-original.md §4.5.3): per slot a mask of type-set ordinals whose latest value the peer
    /// has not received. Implemented by core-replication's <c>PeerDirtySet</c>; read by the scheduler and beacons.
    /// </summary>
    public interface IPeerDirtyView
    {
        bool AnyDirty { get; }

        /// <summary>The first dirty slot at or after <paramref name="fromSlotInclusive"/>; -1 when none.</summary>
        int NextDirtySlot(int fromSlotInclusive);

        /// <summary>Dirty type-set ordinals of the slot (bit i = ordinal i).</summary>
        ulong GetMask(NetSlot slot);

        /// <summary>Seconds since the slot's page became dirty (staleness promotion, docs/history/ARCHITECTURE-original.md §4.8.4).</summary>
        double DirtyAgeSeconds(NetSlot slot);

        /// <summary>The bucket (1024 slots) has dirty bits; beacons skip such buckets (docs/history/ARCHITECTURE-original.md §8.4).</summary>
        bool BucketHasDirty(int bucket);
    }
}
