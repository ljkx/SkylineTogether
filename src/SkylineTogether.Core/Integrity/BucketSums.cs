using System.Threading;
using SkylineTogether.Core.Hashing;

namespace SkylineTogether.Core.Integrity
{
    /// <summary>
    /// Incremental bucket sums per class (ARCH §8.2): bucket = <c>slot &gt;&gt; 10</c>, sum = Σ <c>Mix64(member)</c> modulo 2⁶⁴
    /// and count = number of present members. A member change subtracts the old contribution and adds the new one: O(1),
    /// never a scan, and commutative, so chunk iteration order cannot matter. The host feeds it from the hash shadow
    /// (core-replication <c>HashShadow</c>); the client's <see cref="HashLedger"/> keeps an instance for received values.
    /// <para>
    /// Memory: a class allocates its full bucket range (16384 × 12 bytes) on first use, about 1.4 MB for the seven audited
    /// classes. Threading: mutated on the main thread (on the host only inside the capture window, ARCH §4.5.5); worker
    /// threads may read sums outside the window (beacons). Arrays are never reallocated, so a read never sees a moved
    /// array.
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public sealed class BucketSums : IBucketSink
    {
        private readonly ulong[][] _sums = new ulong[IntegrityRules.ClassCount][];
        private readonly int[][] _counts = new int[IntegrityRules.ClassCount][];
        private readonly int[] _bucketCount = new int[IntegrityRules.ClassCount];
        private readonly long[] _totals = new long[IntegrityRules.ClassCount];
        private long _rejected;
        private long _inconsistencies;

        /// <summary>
        /// Applies a member change of <paramref name="slot"/> in class <paramref name="cls"/>: the bucket sum loses
        /// <c>Mix64(oldMember)</c> and gains <c>Mix64(newMember)</c>; a member of 0 means "absent" (added: old = 0;
        /// removed: new = 0). Updates with an undefined class or slot 0 are ignored and counted in <see cref="RejectedUpdates"/>.
        /// </summary>
        public void OnMemberChanged(ReplicationClass cls, NetSlot slot, ulong oldMember, ulong newMember)
        {
            if (oldMember == newMember) return;
            if (!IntegrityRules.IsValidClass(cls) || slot.IsNone)
            {
                Interlocked.Increment(ref _rejected);
                return;
            }
            int c = (int)cls;
            int bucket = slot.Bucket;
            ulong[] sums = _sums[c];
            int[] counts = _counts[c];
            if (sums == null)
            {
                sums = new ulong[IntegrityRules.MaxBuckets];
                counts = new int[IntegrityRules.MaxBuckets];
                _counts[c] = counts;
                Volatile.Write(ref _sums[c], sums);
            }
            unchecked
            {
                if (oldMember != 0)
                {
                    sums[bucket] -= HashMix.Mix64(oldMember);
                    if (counts[bucket] > 0)
                    {
                        counts[bucket]--;
                        _totals[c]--;
                    }
                    else
                    {
                        // The caller removed a member it never added: the sum is now off by design of the caller, but the
                        // count must not go negative. The ledger and the shadow never do this; the counter makes it visible.
                        Interlocked.Increment(ref _inconsistencies);
                    }
                }
                if (newMember != 0)
                {
                    sums[bucket] += HashMix.Mix64(newMember);
                    counts[bucket]++;
                    _totals[c]++;
                }
            }
            if (bucket >= _bucketCount[c]) Volatile.Write(ref _bucketCount[c], bucket + 1);
        }

        /// <summary>Σ <c>Mix64(member)</c> of the bucket; 0 for an empty, untouched or out-of-range bucket.</summary>
        [ThreadAffinity(ThreadKind.Any)]
        public ulong Sum(ReplicationClass cls, int bucket)
        {
            if (!IntegrityRules.IsValidClass(cls) || !IntegrityRules.IsValidBucket(bucket)) return 0;
            ulong[] sums = Volatile.Read(ref _sums[(int)cls]);
            return sums == null ? 0 : sums[bucket];
        }

        /// <summary>Present members in the bucket; 0 for an empty, untouched or out-of-range bucket.</summary>
        [ThreadAffinity(ThreadKind.Any)]
        public int Count(ReplicationClass cls, int bucket)
        {
            if (!IntegrityRules.IsValidClass(cls) || !IntegrityRules.IsValidBucket(bucket)) return 0;
            if (Volatile.Read(ref _sums[(int)cls]) == null) return 0;
            int[] counts = _counts[(int)cls];
            return counts == null ? 0 : counts[bucket];
        }

        /// <summary>
        /// Number of buckets the class spans: one above the highest bucket that ever held a member since the last
        /// <see cref="Clear"/> (empty buckets inside the range are meaningful beacons: they prove the peer has nothing there).
        /// </summary>
        [ThreadAffinity(ThreadKind.Any)]
        public int BucketCount(ReplicationClass cls)
        {
            return IntegrityRules.IsValidClass(cls) ? Volatile.Read(ref _bucketCount[(int)cls]) : 0;
        }

        /// <summary>Present members of the class over all buckets.</summary>
        [ThreadAffinity(ThreadKind.Any)]
        public long TotalCount(ReplicationClass cls)
        {
            return IntegrityRules.IsValidClass(cls) ? Interlocked.Read(ref _totals[(int)cls]) : 0;
        }

        /// <summary>Updates ignored because of an undefined class or slot 0.</summary>
        [ThreadAffinity(ThreadKind.Any)]
        public long RejectedUpdates => Interlocked.Read(ref _rejected);

        /// <summary>Removals of members that were never added (a caller bug; the count was kept at 0).</summary>
        [ThreadAffinity(ThreadKind.Any)]
        public long Inconsistencies => Interlocked.Read(ref _inconsistencies);

        /// <summary>Bytes held by the bucket arrays.</summary>
        [ThreadAffinity(ThreadKind.Any)]
        public long MemoryBytes
        {
            get
            {
                long bytes = 0;
                for (int c = 0; c < IntegrityRules.ClassCount; c++)
                {
                    if (Volatile.Read(ref _sums[c]) != null) bytes += IntegrityRules.MaxBuckets * (8L + 4L);
                }
                return bytes;
            }
        }

        /// <summary>Forgets every member (session reset, snapshot resync). Keeps the arrays allocated.</summary>
        public void Clear()
        {
            for (int c = 0; c < IntegrityRules.ClassCount; c++)
            {
                ClearClass(c);
            }
            Interlocked.Exchange(ref _rejected, 0);
            Interlocked.Exchange(ref _inconsistencies, 0);
        }

        private void ClearClass(int c)
        {
            ulong[] sums = _sums[c];
            if (sums != null) System.Array.Clear(sums, 0, sums.Length);
            int[] counts = _counts[c];
            if (counts != null) System.Array.Clear(counts, 0, counts.Length);
            Volatile.Write(ref _bucketCount[c], 0);
            Interlocked.Exchange(ref _totals[c], 0);
        }
    }
}
