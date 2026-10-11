using System;
using System.Globalization;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Codec
{
    /// <summary>
    /// The byte layout of a <see cref="NetRef"/> inside canonical bytes (ARCH §4.3.3). The same bytes are hashed, so the
    /// encoding is canonical: exactly one byte sequence per reference, and the decoder rejects every other one (overlong
    /// varints, out-of-range payloads, unknown subkinds) with <see cref="DecodeException"/>.
    /// <code>
    /// NetRef  = varint head [extension]          head = (payload &lt;&lt; 2) | tag, LEB128 (up to 64 bits)
    ///   tag 0   payload 0 = Null, 1 = Local       (one byte: 0x00 or 0x04)
    ///   tag 1   payload = slot, 1 ... 0xFFFFFF
    ///   tag 2   payload = prefab rank, 0 ... int.MaxValue
    ///   tag 3   payload 0, then u8 subkind:
    ///           0     provisional LocalId: varint id (≥ 1)
    ///           1-7   derived key of that DerivedKeyKind:
    ///                 varint owner slot, varint (prefab rank + 1; 0 = none), zig-zag varint index,
    ///                 zig-zag varint qx, qy, qz, varint a, b, c        (every DerivedKey field, in declaration order)
    /// </code>
    /// The record-level references of core-protocol (<c>NetRefCodec</c>: ICON owners and targets, NAME targets) use the
    /// same table (ARCH §4.6.3, "NetRef = §4.3.3"). core-codec depends on core-contracts only, so the table is
    /// implemented twice; the test <c>Codec.NetRefEncodingTests.MatchesTheProtocolNetRefCodecWhenPresent</c> checks that
    /// both produce and accept identical bytes.
    /// </summary>
    internal static class CanonicalNetRef
    {
        internal const int TagNullOrLocal = 0;
        internal const int TagSlot = 1;
        internal const int TagPrefab = 2;
        internal const int TagExtended = 3;
        internal const byte SubkindProvisional = 0;

        /// <summary>Encodes <paramref name="value"/>; references are valid by construction (the NetRef factories validate).</summary>
        internal static void Write(ByteWriter writer, in NetRef value)
        {
            switch (value.Kind)
            {
                case NetRefKind.Null:
                    writer.WriteByte(0);                                   // (0 << 2) | 0
                    return;
                case NetRefKind.Local:
                    writer.WriteByte(4);                                   // (1 << 2) | 0
                    return;
                case NetRefKind.Slot:
                    writer.WriteVarUInt64(((ulong)value.Value << 2) | TagSlot);
                    return;
                case NetRefKind.Prefab:
                    writer.WriteVarUInt64(((ulong)value.Value << 2) | TagPrefab);
                    return;
                case NetRefKind.Provisional:
                    writer.WriteByte(TagExtended);                         // (0 << 2) | 3
                    writer.WriteByte(SubkindProvisional);
                    writer.WriteVarUInt32(value.Value);
                    return;
                case NetRefKind.Derived:
                {
                    DerivedKey key = value.Key;
                    writer.WriteByte(TagExtended);
                    writer.WriteByte((byte)key.Kind);
                    writer.WriteVarUInt32(key.Owner.Value);
                    writer.WriteVarUInt32(unchecked((uint)(key.Prefab.Value + 1)));   // PrefabRank >= -1, so 0 ... 2^31
                    writer.WriteVarInt32(key.Index);
                    writer.WriteVarInt32(key.QX);
                    writer.WriteVarInt32(key.QY);
                    writer.WriteVarInt32(key.QZ);
                    writer.WriteVarUInt64(key.A);
                    writer.WriteVarUInt64(key.B);
                    writer.WriteVarUInt64(key.C);
                    return;
                }
                default:
                    throw new InvalidOperationException("cannot encode a NetRef of unknown kind " + ((int)value.Kind).ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// Decodes one reference from <c>buffer[position .. limit-1]</c> and advances <paramref name="position"/>.
        /// Error positions are reported relative to <paramref name="origin"/> (the start of the record).
        /// Throws <see cref="DecodeException"/> for truncated or non-canonical input and never reads at or beyond
        /// <paramref name="limit"/>.
        /// </summary>
        internal static NetRef Read(byte[] buffer, ref int position, int limit, int origin)
        {
            int start = position;
            ulong head = ReadVarUInt64(buffer, ref position, limit, origin);
            ulong payload = head >> 2;
            switch ((int)(head & 3))
            {
                case TagNullOrLocal:
                    if (payload == 0) return NetRef.Null;
                    if (payload == 1) return NetRef.Local;
                    throw Invalid("null/local reference with payload " + Format(payload), start, origin);
                case TagSlot:
                    if (payload == 0 || payload > NetSlot.MaxValue) throw Invalid("slot reference " + Format(payload) + " outside 1 ... 0xFFFFFF", start, origin);
                    return NetRef.ForSlot(new NetSlot((uint)payload));
                case TagPrefab:
                    if (payload > int.MaxValue) throw Invalid("prefab rank " + Format(payload) + " exceeds int.MaxValue", start, origin);
                    return NetRef.ForPrefab(new PrefabRank((int)payload));
                default:
                    return ReadExtended(buffer, ref position, limit, origin, start, payload);
            }
        }

        private static NetRef ReadExtended(byte[] buffer, ref int position, int limit, int origin, int start, ulong payload)
        {
            if (payload != 0) throw Invalid("extended reference with payload " + Format(payload) + " (must be 0)", start, origin);
            if (position >= limit) throw Overrun(1, position, limit, origin);
            byte subkind = buffer[position++];
            if (subkind == SubkindProvisional)
            {
                uint id = ReadVarUInt32(buffer, ref position, limit, origin);
                if (id == 0) throw Invalid("provisional reference with LocalId 0", start, origin);
                return NetRef.ForProvisional(new LocalId(id));
            }
            if (!DerivedKey.IsValidKind(subkind))
            {
                throw Invalid("unknown extended reference subkind " + subkind.ToString(CultureInfo.InvariantCulture), start, origin);
            }
            uint owner = ReadVarUInt32(buffer, ref position, limit, origin);
            if (owner > NetSlot.MaxValue) throw Invalid("derived key owner slot " + Format(owner) + " exceeds 0xFFFFFF", start, origin);
            uint prefabPlusOne = ReadVarUInt32(buffer, ref position, limit, origin);
            if (prefabPlusOne > 0x80000000u) throw Invalid("derived key prefab rank " + Format(prefabPlusOne) + " - 1 exceeds int.MaxValue", start, origin);
            int index = ReadVarInt32(buffer, ref position, limit, origin);
            int qx = ReadVarInt32(buffer, ref position, limit, origin);
            int qy = ReadVarInt32(buffer, ref position, limit, origin);
            int qz = ReadVarInt32(buffer, ref position, limit, origin);
            ulong a = ReadVarUInt64(buffer, ref position, limit, origin);
            ulong b = ReadVarUInt64(buffer, ref position, limit, origin);
            ulong c = ReadVarUInt64(buffer, ref position, limit, origin);
            PrefabRank prefab = prefabPlusOne == 0 ? PrefabRank.None : new PrefabRank((int)(prefabPlusOne - 1));
            var key = new DerivedKey((DerivedKeyKind)subkind, new NetSlot(owner), prefab, index, qx, qy, qz, a, b, c);
            return NetRef.ForDerived(in key);
        }

        // ---- strict LEB128 (the rules of Buffers.ByteReader: no overlong encodings, nothing beyond the type width) ----

        private static uint ReadVarUInt32(byte[] buffer, ref int position, int limit, int origin)
        {
            int first = position;
            uint result = 0;
            int shift = 0;
            for (int i = 0; i < 5; i++)
            {
                if (position >= limit) throw Overrun(1, position, limit, origin);
                byte b = buffer[position++];
                if (i == 4 && b > 0x0F) throw Invalid("varint exceeds 32 bits", first, origin);
                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    if (b == 0 && i > 0) throw Invalid("overlong varint", first, origin);
                    return result;
                }
                shift += 7;
            }
            throw Invalid("varint exceeds 32 bits", first, origin);
        }

        private static ulong ReadVarUInt64(byte[] buffer, ref int position, int limit, int origin)
        {
            int first = position;
            ulong result = 0;
            int shift = 0;
            for (int i = 0; i < 10; i++)
            {
                if (position >= limit) throw Overrun(1, position, limit, origin);
                byte b = buffer[position++];
                if (i == 9 && b > 0x01) throw Invalid("varint exceeds 64 bits", first, origin);
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    if (b == 0 && i > 0) throw Invalid("overlong varint", first, origin);
                    return result;
                }
                shift += 7;
            }
            throw Invalid("varint exceeds 64 bits", first, origin);
        }

        private static int ReadVarInt32(byte[] buffer, ref int position, int limit, int origin)
        {
            uint u = ReadVarUInt32(buffer, ref position, limit, origin);
            return unchecked((int)(u >> 1) ^ -(int)(u & 1));
        }

        private static string Format(ulong value) => value.ToString(CultureInfo.InvariantCulture);

        private static DecodeException Invalid(string what, int absolutePosition, int origin)
        {
            return new DecodeException("invalid entity reference at position "
                                       + (absolutePosition - origin).ToString(CultureInfo.InvariantCulture) + ": " + what);
        }

        private static DecodeException Overrun(int count, int absolutePosition, int limit, int origin)
        {
            return new DecodeException("entity reference: read of " + count.ToString(CultureInfo.InvariantCulture)
                                       + " byte(s) at position " + (absolutePosition - origin).ToString(CultureInfo.InvariantCulture)
                                       + " overruns the bound (" + Math.Max(0, limit - absolutePosition).ToString(CultureInfo.InvariantCulture)
                                       + " remaining)");
        }
    }
}
