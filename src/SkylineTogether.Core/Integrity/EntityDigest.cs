using System;
using System.Collections.Generic;
using SkylineTogether.Core.Hashing;
using SkylineTogether.Core.Sync;

namespace SkylineTogether.Core.Integrity
{
    /// <summary>How an entity of a bucket compares between the host and a client.</summary>
    public enum EntityDifference : byte
    {
        /// <summary>Both have it with the same hash.</summary>
        Match = 0,
        /// <summary>Both have it, the hashes differ.</summary>
        Differs = 1,
        /// <summary>The host has it bound, the client does not.</summary>
        MissingOnClient = 2,
        /// <summary>The client has it bound, the host does not.</summary>
        ExtraOnClient = 3,
    }

    /// <summary>
    /// The rolling live verification's digest arithmetic (playable subset of ARCH §8.2-8.4): net ids are grouped into
    /// buckets of <see cref="BucketSize"/>; a bucket's digest is the count of its bound entities and the wrapping sum of
    /// <c>Mix64(Combine(id, hash))</c>, so it does not depend on iteration order and a single differing, missing or
    /// extra entity changes it. Pure and thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class EntityDigest
    {
        /// <summary>log2 of <see cref="BucketSize"/>.</summary>
        public const int BucketShift = 7;

        /// <summary>Net ids per bucket (128).</summary>
        public const int BucketSize = 1 << BucketShift;

        private const ulong MemberDomain = 0x6A09E667F3BCC909UL;

        public static uint BucketOf(uint netId) => netId >> BucketShift;

        /// <summary>The first id of a bucket that can be bound (bucket 0 starts at id 1).</summary>
        public static uint FirstId(uint bucket) => bucket == 0 ? 1u : bucket << BucketShift;

        /// <summary>The last id of a bucket.</summary>
        public static uint LastId(uint bucket) => bucket >= (uint.MaxValue >> BucketShift) ? uint.MaxValue : ((bucket + 1) << BucketShift) - 1;

        /// <summary>One entity's contribution to its bucket's sum.</summary>
        public static ulong Member(uint netId, ulong hash) => HashMix.Mix64(HashMix.Combine(MemberDomain ^ netId, hash));

        /// <summary>Running digest of one bucket.</summary>
        public struct Accumulator
        {
            public ulong Sum;
            public uint Count;

            public void Add(uint netId, ulong hash)
            {
                unchecked
                {
                    Sum += Member(netId, hash);
                }
                Count++;
            }

            public bool Matches(uint count, ulong sum) => Count == count && Sum == sum;

            public void Reset()
            {
                Sum = 0;
                Count = 0;
            }
        }

        /// <summary>The digest of a list of entity hashes.</summary>
        public static Accumulator Of(IReadOnlyList<EntityHash> hashes)
        {
            var a = new Accumulator();
            for (int i = 0; i < hashes.Count; i++) a.Add(hashes[i].NetId, hashes[i].Hash);
            return a;
        }

        /// <summary>
        /// Every id of either list that does not match, with its kind, ordered by id. A list holding an id twice uses the
        /// first entry (lists are built from id tables, so this does not happen in practice).
        /// </summary>
        public static List<(uint NetId, EntityDifference Kind)> Compare(IReadOnlyList<EntityHash> host, IReadOnlyList<EntityHash> client)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            if (client == null) throw new ArgumentNullException(nameof(client));
            var hostById = new Dictionary<uint, ulong>(host.Count);
            for (int i = 0; i < host.Count; i++)
            {
                if (!hostById.ContainsKey(host[i].NetId)) hostById.Add(host[i].NetId, host[i].Hash);
            }
            var result = new List<(uint, EntityDifference)>();
            var seen = new HashSet<uint>();
            for (int i = 0; i < client.Count; i++)
            {
                uint id = client[i].NetId;
                if (!seen.Add(id)) continue;
                if (!hostById.TryGetValue(id, out ulong h)) result.Add((id, EntityDifference.ExtraOnClient));
                else if (h != client[i].Hash) result.Add((id, EntityDifference.Differs));
            }
            foreach (KeyValuePair<uint, ulong> pair in hostById)
            {
                if (!seen.Contains(pair.Key)) result.Add((pair.Key, EntityDifference.MissingOnClient));
            }
            result.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            return result;
        }
    }
}
