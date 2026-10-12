using System;
using SkylineTogether.Core.Hashing;

namespace SkylineTogether.Core.Integrity
{
    /// <summary>
    /// Member hashes of the convergence proof (ARCH §4.3.4, §8.1-8.2). Every function has a fixed, documented definition:
    /// the host's hash shadow, the client's ledger and every bucket beacon depend on both sides computing identical
    /// values, so these formulas are part of the wire contract and must never change within a protocol version.
    /// <list type="bullet">
    /// <item><b>Member hash</b> <c>h(slot, type)</c> = <c>Fold32(xxHash64(canonical bytes, seed = type rank))</c>
    /// (<see cref="HashCanonical"/>), or the order-insensitive <see cref="HashEntityRefMultiset"/> for <c>hashAsSet</c>
    /// types (ARCH §4.2.4). A hash is never 0: 0 means "absent" in ledgers and shadows.</item>
    /// <item><b>Member</b> (the 64-bit value a bucket sum adds): <see cref="Member"/> for a (slot, type) value,
    /// <see cref="ExistenceMember"/> for the existence of a slot, <see cref="KeyedMember"/> for K-class state on a
    /// derived key. A member is never 0 for present data; 0 means "absent" (<see cref="IBucketSink"/>).</item>
    /// <item><b>Bucket sum</b> = Σ <see cref="Contribution"/>(member) = Σ <c>Mix64(member)</c> modulo 2⁶⁴, so the
    /// iteration order never matters and every change is O(1) (<see cref="BucketSums"/>).</item>
    /// <item><b>Slot digest</b> (one entry of a bucket detail, ARCH §8.5 level 1) =
    /// <c>Fold32(Σ Contribution(member))</c> over the slot's members of one class (<see cref="SlotDigest"/>).</item>
    /// </list>
    /// All functions are pure, allocation-free and thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class MemberHasher
    {
        /// <summary>Returned instead of a folded 32-bit hash of 0 (0 means "absent").</summary>
        public const uint NonZeroHash = 0x9E3779B9u;

        /// <summary>Returned instead of a 64-bit member of 0 (0 means "absent").</summary>
        public const ulong NonZeroMember = 0x9E3779B97F4A7C15UL;

        // Domain separation constants (the SHA-512 initial values): members of different kinds can never be built from
        // the same mixer input. Part of the wire contract.
        private const ulong ValueDomain = 0x6A09E667F3BCC908UL;
        private const ulong ExistenceDomain = 0xBB67AE8584CAA73BUL;
        private const ulong KeyedDomain = 0x3C6EF372FE94F82BUL;
        private const ulong MultisetDomain = 0xA54FF53A5F1D36F1UL;
        private const ulong DisabledDomain = 0x510E527FADE682D1UL;

        /// <summary>
        /// The member hash of a value: <c>Fold32(xxHash64(canonical[offset .. offset+count), seed = type.Value))</c>, with a
        /// result of 0 replaced by <see cref="NonZeroHash"/>. Buffers are hashed as their canonical bytes, i.e. count plus
        /// elements (ARCH §4.3.4). <paramref name="canonical"/> may be null when <paramref name="count"/> is 0 (presence-only
        /// types hash their empty payload, which still differs per type).
        /// </summary>
        public static uint HashCanonical(TypeRank type, byte[] canonical, int offset, int count)
        {
            CheckRange(canonical, offset, count);
            ulong h = canonical == null
                ? XxHash64.Hash(Array.Empty<byte>(), 0, 0, type.Value)
                : XxHash64.Hash(canonical, offset, count, type.Value);
            return NonZero(HashMix.Fold32(h));
        }

        /// <summary>
        /// The order-insensitive member hash of a data-replicated reverse buffer (<c>hashAsSet</c>, ARCH §4.2.4), whose
        /// element order differs between peers after a join (the Deserialize-phase rebuild fills them in chunk order while
        /// the host keeps insertion order, R04 §5). Input layout of the <c>EntityRefList</c> codec: <c>varint n</c>, then
        /// exactly <c>n</c> canonical <c>NetRef</c>s (ARCH §4.3.3). Definition:
        /// <code>
        /// s = Σ Mix64(xxHash64(bytes of element i, seed = type.Value))   (mod 2^64)
        /// h = Fold32(Combine(s ^ MultisetDomain, n))                      (0 → NonZeroHash)
        /// </code>
        /// Duplicates count with their multiplicity. Input that is not a well-formed list (truncated, trailing bytes, an
        /// invalid reference) falls back to <see cref="HashCanonical"/> of the same bytes, which is just as deterministic
        /// on every peer; the function never throws for malformed data.
        /// </summary>
        public static uint HashEntityRefMultiset(TypeRank type, byte[] canonical, int offset, int count)
        {
            CheckRange(canonical, offset, count);
            if (canonical == null || count == 0) return HashCanonical(type, canonical, offset, count);
            int position = offset;
            int end = offset + count;
            if (!TryReadVarUInt32(canonical, ref position, end, out uint n) || n > (uint)(end - position))
            {
                return HashCanonical(type, canonical, offset, count);   // every reference takes at least one byte
            }
            ulong sum = 0;
            for (uint i = 0; i < n; i++)
            {
                int start = position;
                if (!TrySkipNetRef(canonical, ref position, end)) return HashCanonical(type, canonical, offset, count);
                unchecked
                {
                    sum += HashMix.Mix64(XxHash64.Hash(canonical, start, position - start, type.Value));
                }
            }
            if (position != end) return HashCanonical(type, canonical, offset, count);
            return NonZero(HashMix.Fold32(HashMix.Combine(sum ^ MultisetDomain, n)));
        }

        /// <summary>
        /// The member hash of an enableable type (ARCH §4.3.4 "enabled bits included"): the value hash when the
        /// component is enabled, otherwise <c>Fold32(Combine(DisabledDomain, hash))</c> (0 → <see cref="NonZeroHash"/>).
        /// A <paramref name="hash"/> of 0 (absent) stays 0.
        /// </summary>
        public static uint ApplyEnabledBit(uint hash, bool enabled)
        {
            if (hash == 0 || enabled) return hash;
            return NonZero(HashMix.Fold32(HashMix.Combine(DisabledDomain, hash)));
        }

        /// <summary>
        /// The bucket member of a (slot, type) value: <c>Combine(Combine(ValueDomain, slot &lt;&lt; 16 | type), hash)</c>
        /// (0 → <see cref="NonZeroMember"/>). A <paramref name="hash"/> of 0 or slot 0 yields 0 ("no member").
        /// </summary>
        public static ulong Member(NetSlot slot, TypeRank type, uint hash)
        {
            if (hash == 0 || slot.IsNone) return 0;
            ulong key = ((ulong)slot.Value << 16) | type.Value;
            return NonZero(HashMix.Combine(HashMix.Combine(ValueDomain, key), hash));
        }

        /// <summary>
        /// The existence member of a slot (ARCH §4.3.4): <c>Combine(Combine(ExistenceDomain, slot &lt;&lt; 8 | generation),
        /// (uint)typeSet &lt;&lt; 8 | family)</c> (0 → <see cref="NonZeroMember"/>). It changes with a respawn (generation),
        /// a signature change (type set) and a family change, so a structural difference shows in the bucket even when
        /// every value matches. Slot 0 yields 0.
        /// </summary>
        public static ulong ExistenceMember(NetSlot slot, byte generation, TypeSetId typeSet, Family family)
        {
            if (slot.IsNone) return 0;
            ulong a = ((ulong)slot.Value << 8) | generation;
            ulong b = ((ulong)(uint)typeSet.Value << 8) | (byte)family;
            return NonZero(HashMix.Combine(HashMix.Combine(ExistenceDomain, a), b));
        }

        /// <summary>
        /// The member of K-class state on a derived entity (ARCH §8.6: hashed per key, bucketed by the key's owner slot):
        /// <c>Combine(Combine(KeyedDomain, keyHash), type &lt;&lt; 32 | hash)</c> where <c>keyHash</c> chains every key field
        /// in declaration order through <see cref="HashMix.Combine"/>. A <paramref name="hash"/> of 0 yields 0. The member
        /// belongs to the bucket of <c>key.Owner</c>.
        /// </summary>
        public static ulong KeyedMember(in DerivedKey key, TypeRank type, uint hash)
        {
            if (hash == 0) return 0;
            ulong k = HashMix.Combine(KeyedDomain, (byte)key.Kind);
            k = HashMix.Combine(k, key.Owner.Value);
            k = HashMix.Combine(k, unchecked((uint)key.Prefab.Value));
            k = HashMix.Combine(k, unchecked((uint)key.Index));
            k = HashMix.Combine(k, unchecked((uint)key.QX));
            k = HashMix.Combine(k, unchecked((uint)key.QY));
            k = HashMix.Combine(k, unchecked((uint)key.QZ));
            k = HashMix.Combine(k, key.A);
            k = HashMix.Combine(k, key.B);
            k = HashMix.Combine(k, key.C);
            return NonZero(HashMix.Combine(k, ((ulong)type.Value << 32) | hash));
        }

        /// <summary>What a member adds to its bucket sum: <c>Mix64(member)</c>, and 0 for an absent member.</summary>
        public static ulong Contribution(ulong member) => member == 0 ? 0 : HashMix.Mix64(member);

        /// <summary>
        /// The per-slot entry of a bucket detail (ARCH §8.5 level 1): <c>Fold32</c> of the sum of the slot's member
        /// contributions in one class. The host's hash shadow (<see cref="IHashShadowView.CollectBucket"/>) and the
        /// client's ledger (<see cref="HashLedger.TryGetSlotDigest"/>) must both use this definition.
        /// </summary>
        public static uint SlotDigest(ulong contributionSum) => HashMix.Fold32(contributionSum);

        private static uint NonZero(uint hash) => hash == 0 ? NonZeroHash : hash;

        private static ulong NonZero(ulong member) => member == 0 ? NonZeroMember : member;

        private static void CheckRange(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
            {
                if (offset != 0 || count != 0) throw new ArgumentNullException(nameof(buffer), "a null buffer needs offset 0 and count 0");
                return;
            }
            if (offset < 0 || count < 0 || offset > buffer.Length - count)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "the range lies outside the buffer");
            }
        }

        // ---- allocation-free, non-throwing scanner of the canonical NetRef layout (ARCH §4.3.3, core-codec CanonicalNetRef) ----

        /// <summary>Advances over one canonical reference; false (position undefined) for malformed input.</summary>
        private static bool TrySkipNetRef(byte[] buffer, ref int position, int end)
        {
            if (!TryReadVarUInt64(buffer, ref position, end, out ulong head)) return false;
            if ((head & 3) != 3) return true;                       // null/local, slot or prefab: the head is the whole reference
            if ((head >> 2) != 0) return false;                     // extended references carry payload 0
            if (position >= end) return false;
            byte subkind = buffer[position++];
            if (subkind == 0) return TryReadVarUInt32(buffer, ref position, end, out _);   // provisional LocalId
            if (!DerivedKey.IsValidKind(subkind)) return false;
            for (int i = 0; i < 6; i++)                              // owner, prefab + 1, index, qx, qy, qz
            {
                if (!TryReadVarUInt32(buffer, ref position, end, out _)) return false;
            }
            for (int i = 0; i < 3; i++)                              // a, b, c
            {
                if (!TryReadVarUInt64(buffer, ref position, end, out _)) return false;
            }
            return true;
        }

        private static bool TryReadVarUInt32(byte[] buffer, ref int position, int end, out uint value)
        {
            value = 0;
            int shift = 0;
            for (int i = 0; i < 5; i++)
            {
                if (position >= end) return false;
                byte b = buffer[position++];
                if (i == 4 && b > 0x0F) return false;
                value |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return true;
                shift += 7;
            }
            return false;
        }

        private static bool TryReadVarUInt64(byte[] buffer, ref int position, int end, out ulong value)
        {
            value = 0;
            int shift = 0;
            for (int i = 0; i < 10; i++)
            {
                if (position >= end) return false;
                byte b = buffer[position++];
                if (i == 9 && b > 0x01) return false;
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return true;
                shift += 7;
            }
            return false;
        }
    }
}
