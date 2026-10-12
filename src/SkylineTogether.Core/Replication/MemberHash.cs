using System;
using SkylineTogether.Core.Hashing;

namespace SkylineTogether.Core.Replication
{
    /// <summary>
    /// Member hashes of the convergence proof (ARCH §4.3.4, §8.1-8.2). The host's <see cref="HashShadow"/> and the
    /// client's ledger bookkeeping in <see cref="ClientReplicationEngine"/> both use these functions, so the bucket sums of
    /// the two sides agree by construction; the game layer uses them for the client's initial ledger pass and rolling
    /// verification. The definitions are part of the wire contract (beacons compare sums) and must never change:
    /// <list type="bullet">
    /// <item><b>Value hash</b> of canonical bytes: <c>Fold32(xxHash64(bytes, seed = typeRank))</c> (<see cref="HashCanonical"/>),
    /// or for <c>hashAsSet</c> types an order-insensitive multiset hash over the elements (<see cref="HashEntityRefMultiset"/>).</item>
    /// <item><b>Pair hash</b>: the value hash with the type's enabled bit folded in (<see cref="PairHash"/>): unchanged when
    /// enabled or not enableable, XOR <see cref="DisabledMarker"/> when disabled (§8.1 "enabled bits included").</item>
    /// <item><b>Member</b> of a (slot, type) pair: <c>Combine((slot &lt;&lt; 16) | typeRank, pairHash)</c>, never 0
    /// (<see cref="Member"/>); 0 means "absent" for <see cref="IBucketSink"/>.</item>
    /// <item><b>Existence member</b> of a slot: <c>Combine(Combine(ExistenceSeed | generation, (slot &lt;&lt; 8) | family), typeSetId)</c>,
    /// never 0 (<see cref="ExistenceMember"/>).</item>
    /// <item><b>Keyed member</b> of K state: <c>Combine(Combine(KeyHash(key), typeRank), valueHash)</c>, never 0, bucketed by the
    /// key's owner slot (§8.6).</item>
    /// <item><b>Slot contribution</b> to a bucket (bucket details, §8.5 level 1): <c>Fold32(Σ Mix64(member))</c> over the
    /// slot's members of one class (<see cref="FoldContribution"/>).</item>
    /// </list>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class MemberHash
    {
        /// <summary>XORed into the pair hash of an enableable type whose enabled bit is off.</summary>
        public const uint DisabledMarker = 0x5BD1E995u;

        private const ulong ExistenceSeed = 0x5354455849535400UL;   // "STEXIST\0"
        private const ulong MultisetSeed = 0x4D554C5449534554UL;    // "MULTISET"
        private const ulong KeySeed = 0x5354444B45590000UL;         // "STDKEY\0\0"

        /// <summary><c>Fold32(xxHash64(bytes, seed = typeRank))</c>: the ordered hash of canonical bytes.</summary>
        public static uint HashCanonical(TypeRank type, byte[] canonical, int offset, int count)
        {
            if (count == 0) return HashMix.Fold32(XxHash64.Hash(Array.Empty<byte>(), 0, 0, type.Value));
            return HashMix.Fold32(XxHash64.Hash(canonical, offset, count, type.Value));
        }

        /// <summary>
        /// The value hash of a <c>hashAsSet</c> type (data-replicated reverse buffers, ARCH §4.2.4): independent of the
        /// order of the elements, sensitive to their multiplicity. The bytes are an element count followed by
        /// <c>count × entityRefsPerElement</c> canonical <see cref="NetRef"/>s; the count is read as the little-endian
        /// <c>int</c> the canonical writer uses for buffers and, if that does not describe the bytes exactly, as a varint.
        /// Bytes that match neither layout fall back to <see cref="HashCanonical"/> (both peers apply the same rule to the
        /// same bytes, so they still agree).
        /// </summary>
        public static uint HashEntityRefMultiset(TypeRank type, int entityRefsPerElement, byte[] canonical, int offset, int count)
        {
            if (canonical == null || count <= 0) return HashCanonical(type, canonical, offset, count);
            int k = entityRefsPerElement < 1 ? 1 : entityRefsPerElement;
            int end = offset + count;
            if (count >= 4)
            {
                int elements = canonical[offset] | (canonical[offset + 1] << 8) | (canonical[offset + 2] << 16) | (canonical[offset + 3] << 24);
                if (TryMultiset(type, k, canonical, offset + 4, end, elements, out uint hash)) return hash;
            }
            int position = offset;
            if (TrySkipVarint(canonical, ref position, end, out ulong varCount) && varCount <= int.MaxValue
                && TryMultiset(type, k, canonical, position, end, (int)varCount, out uint varHash))
            {
                return varHash;
            }
            return HashCanonical(type, canonical, offset, count);
        }

        /// <summary>The value hash of a type: <see cref="HashEntityRefMultiset"/> for <c>hashAsSet</c> types, else <see cref="HashCanonical"/>.</summary>
        public static uint HashValue(TypeRank type, bool hashAsSet, int entityRefsPerElement, byte[] canonical, int offset, int count)
        {
            return hashAsSet
                ? HashEntityRefMultiset(type, entityRefsPerElement, canonical, offset, count)
                : HashCanonical(type, canonical, offset, count);
        }

        /// <summary>Folds an enabled bit into a value hash; <paramref name="hasEnabledBit"/> false (not enableable) leaves it unchanged.</summary>
        public static uint PairHash(uint valueHash, bool hasEnabledBit, bool enabled)
        {
            return hasEnabledBit && !enabled ? valueHash ^ DisabledMarker : valueHash;
        }

        /// <summary>The bucket-sum member of a (slot, type) pair; never 0.</summary>
        public static ulong Member(NetSlot slot, TypeRank type, uint pairHash)
        {
            ulong member = HashMix.Combine(((ulong)slot.Value << 16) | type.Value, pairHash);
            return member != 0 ? member : 1;
        }

        /// <summary>The existence member of a slot (ARCH §4.3.4): slot, generation, type set and family; never 0.</summary>
        public static ulong ExistenceMember(NetSlot slot, byte generation, TypeSetId typeSet, Family family)
        {
            ulong member = HashMix.Combine(HashMix.Combine(ExistenceSeed | generation, ((ulong)slot.Value << 8) | (byte)family), (uint)typeSet.Value);
            return member != 0 ? member : 1;
        }

        /// <summary>A 64-bit hash of every field of a derived key.</summary>
        public static ulong KeyHash(in DerivedKey key)
        {
            ulong h = HashMix.Combine(KeySeed | (byte)key.Kind, key.Owner.Value);
            h = HashMix.Combine(h, (uint)key.Prefab.Value);
            h = HashMix.Combine(h, (uint)key.Index);
            h = HashMix.Combine(h, ((ulong)(uint)key.QX << 32) | (uint)key.QY);
            h = HashMix.Combine(h, (uint)key.QZ);
            h = HashMix.Combine(h, key.A);
            h = HashMix.Combine(h, key.B);
            return HashMix.Combine(h, key.C);
        }

        /// <summary>The bucket-sum member of K state (state of type <paramref name="type"/> on the derived entity <paramref name="key"/>); never 0.</summary>
        public static ulong KeyedMember(in DerivedKey key, TypeRank type, uint valueHash)
        {
            ulong member = HashMix.Combine(HashMix.Combine(KeyHash(key), type.Value), valueHash);
            return member != 0 ? member : 1;
        }

        /// <summary>A slot's contribution to a bucket, folded to 32 bits: <c>Fold32(Σ Mix64(member))</c>.</summary>
        public static uint FoldContribution(ulong sumOfMixedMembers) => HashMix.Fold32(sumOfMixedMembers);

        private static bool TryMultiset(TypeRank type, int k, byte[] b, int start, int end, int elements, out uint hash)
        {
            hash = 0;
            if (elements < 0) return false;
            // every NetRef takes at least one byte
            if ((long)elements * k > end - start) return false;
            ulong sum = 0;
            int position = start;
            for (int e = 0; e < elements; e++)
            {
                int elementStart = position;
                for (int r = 0; r < k; r++)
                {
                    if (!TrySkipNetRef(b, ref position, end)) return false;
                }
                sum += HashMix.Mix64(XxHash64.Hash(b, elementStart, position - elementStart, type.Value));
            }
            if (position != end) return false;
            hash = HashMix.Fold32(HashMix.Combine(HashMix.Combine(sum, (ulong)elements), MultisetSeed ^ type.Value));
            return true;
        }

        /// <summary>Skips one canonical NetRef (ARCH §4.3.3) without validating payload ranges; false when truncated.</summary>
        private static bool TrySkipNetRef(byte[] b, ref int position, int end)
        {
            if (!TrySkipVarint(b, ref position, end, out ulong head)) return false;
            if ((head & 3) != 3) return true;
            if (position >= end) return false;
            byte subkind = b[position++];
            if (subkind == 0) return TrySkipVarint(b, ref position, end, out _);
            if (subkind > 7) return false;
            // owner, prefab + 1, index, qx, qy, qz, a, b, c
            for (int i = 0; i < 9; i++)
            {
                if (!TrySkipVarint(b, ref position, end, out _)) return false;
            }
            return true;
        }

        private static bool TrySkipVarint(byte[] b, ref int position, int end, out ulong value)
        {
            value = 0;
            int shift = 0;
            for (int i = 0; i < 10; i++)
            {
                if (position >= end) return false;
                byte x = b[position++];
                value |= (ulong)(x & 0x7F) << shift;
                if ((x & 0x80) == 0) return true;
                shift += 7;
            }
            return false;
        }
    }
}
