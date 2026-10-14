using System.Collections.Generic;

namespace SkylineTogether.Core.Hashing
{
    /// <summary>
    /// An order-insensitive hash of a multiset of ids (ARCH §4.2.4: reverse buffers such as
    /// <c>HouseholdCitizen</c> or <c>Renter</c> are hashed as multisets, because a loaded save rebuilds them in chunk order
    /// while the host keeps insertion order). Each id is mixed alone and the mixes are summed, then combined with the
    /// count, so duplicates count and order does not. Pure.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class SetHash
    {
        private const ulong Domain = 0xBB67AE8584CAA73BUL;

        public static ulong Of(IEnumerable<uint> ids)
        {
            var acc = new Accumulator();
            if (ids != null)
            {
                foreach (uint id in ids) acc.Add(id);
            }
            return acc.Value;
        }

        /// <summary>Incremental form of <see cref="Of"/> (no allocation).</summary>
        public struct Accumulator
        {
            private ulong _sum;
            private uint _count;

            public void Add(uint id)
            {
                unchecked
                {
                    _sum += HashMix.Mix64(Domain ^ id);
                    _count++;
                }
            }

            public uint Count => _count;

            public ulong Value => HashMix.Combine(HashMix.Combine(Domain, _sum), _count);
        }
    }
}
