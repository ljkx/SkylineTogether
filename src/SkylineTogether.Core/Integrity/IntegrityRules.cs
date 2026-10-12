using System.Globalization;
using SkylineTogether.Core.Protocol;

namespace SkylineTogether.Core.Integrity
{
    /// <summary>
    /// Shared rules of the integrity module: which classes are audited with bucket sums, which of them are scheduled
    /// per peer (their beacons skip dirty buckets, ARCH §8.4), bucket ranges, and the bucket designator convention of
    /// bucket-level repair requests.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    internal static class IntegrityRules
    {
        /// <summary>Number of <see cref="ReplicationClass"/> values (0 ... OnDemand).</summary>
        internal const int ClassCount = (int)ReplicationClass.OnDemand + 1;

        /// <summary>Number of buckets a class can span: <c>NetSlot.MaxValue &gt;&gt; 10</c> + 1 = 16384.</summary>
        internal const int MaxBuckets = Limits.MaxBucket + 1;

        /// <summary>Slots per bucket (1024, ARCH §8.2).</summary>
        internal const int SlotsPerBucket = 1 << NetSlot.BucketShift;

        /// <summary>True for a defined class value (guards against casts of wire bytes).</summary>
        internal static bool IsValidClass(ReplicationClass cls) => (uint)cls < ClassCount;

        /// <summary>
        /// Classes with exact bucket audits (ARCH §8.1, §8.4): S, G, V, A, A-low, M and K. Excluded, derived, channel
        /// and on-demand data are never hashed into bucket sums (channels have their own hashes, §8.6).
        /// </summary>
        internal static bool IsAudited(ReplicationClass cls)
        {
            switch (cls)
            {
                case ReplicationClass.S:
                case ReplicationClass.G:
                case ReplicationClass.V:
                case ReplicationClass.A:
                case ReplicationClass.ALow:
                case ReplicationClass.M:
                case ReplicationClass.K:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Classes delivered per peer (V, A, A-low, M, K): their hash shadow is updated at capture, so a beacon or a
        /// bucket detail describes what the peer has received only while the peer has no dirty bits in the bucket
        /// (ARCH §8.4). S and G are shared P0 classes whose shadow is updated when the record is emitted (§4.4.5).
        /// </summary>
        internal static bool IsPerPeer(ReplicationClass cls)
        {
            switch (cls)
            {
                case ReplicationClass.V:
                case ReplicationClass.A:
                case ReplicationClass.ALow:
                case ReplicationClass.M:
                case ReplicationClass.K:
                    return true;
                default:
                    return false;
            }
        }

        internal static bool IsValidBucket(int bucket) => bucket >= 0 && bucket < MaxBuckets;

        /// <summary>
        /// A bucket-level repair request (<see cref="RepairLevel.Bucket"/>, a request for a bucket detail) names each
        /// bucket by one slot inside it: the bucket's first slot, or slot 1 for bucket 0 (slot 0 is not a valid slot on
        /// the wire). The host reads the bucket back with <see cref="NetSlot.Bucket"/>.
        /// </summary>
        internal static NetSlot BucketDesignator(int bucket)
        {
            return new NetSlot(bucket == 0 ? 1u : (uint)bucket << NetSlot.BucketShift);
        }

        /// <summary>First slot of a bucket that can be bound (bucket 0 starts at slot 1).</summary>
        internal static uint FirstSlotOf(int bucket) => bucket == 0 ? 1u : (uint)bucket << NetSlot.BucketShift;

        /// <summary>Last slot of a bucket, capped at <see cref="NetSlot.MaxValue"/>.</summary>
        internal static uint LastSlotOf(int bucket)
        {
            ulong last = ((ulong)(uint)bucket << NetSlot.BucketShift) + (SlotsPerBucket - 1);
            return last > NetSlot.MaxValue ? NetSlot.MaxValue : (uint)last;
        }

        /// <summary>Log text of a class and bucket, e.g. <c>"S/12"</c>.</summary>
        internal static string Describe(ReplicationClass cls, int bucket)
        {
            return cls.ToString() + "/" + bucket.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Key of a (class, bucket) pair in dictionaries.</summary>
        internal static long BucketKey(ReplicationClass cls, int bucket) => ((long)(byte)cls << 32) | (uint)bucket;

        /// <summary>Key of a (slot, type) pair in dictionaries: <c>slot &lt;&lt; 16 | type</c>.</summary>
        internal static ulong PairKey(NetSlot slot, TypeRank type) => ((ulong)slot.Value << 16) | type.Value;

        internal static NetSlot SlotOfPairKey(ulong key) => new NetSlot((uint)(key >> 16) & NetSlot.MaxValue);

        internal static TypeRank TypeOfPairKey(ulong key) => new TypeRank((ushort)key);

        internal static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);

        internal static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);

        internal static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
