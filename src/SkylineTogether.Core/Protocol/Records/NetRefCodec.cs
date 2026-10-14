using System;
using System.Globalization;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// The wire form of a <see cref="NetRef"/> in records (ICON owners and targets, NAME targets; docs/history/ARCHITECTURE-original.md §4.6.3
    /// "NetRef = §4.3.3") and of a <see cref="DerivedKey"/> (KEYED, KEYED_DEL, ZONE). It is byte-identical to the
    /// canonical-bytes encoding of core-codec, because the same references are hashed (docs/history/ARCHITECTURE-original.md §4.3.3):
    /// <code>
    /// NetRef = varint head [extension]          head = (payload &lt;&lt; 2) | tag, LEB128 (up to 64 bits)
    ///   tag 0   payload 0 = Null, 1 = Local       (one byte: 00 or 04)
    ///   tag 1   payload = slot, 1 ... 0xFFFFFF
    ///   tag 2   payload = prefab rank, 0 ... int.MaxValue
    ///   tag 3   payload 0, then u8 subkind:
    ///           0     provisional LocalId: varint id (≥ 1)
    ///           1-7   derived key of that DerivedKeyKind: the key fields below
    /// DerivedKey = u8 kind (1-7) | varint owner slot (≤ 0xFFFFFF) | varint (prefab rank + 1; 0 = none)
    ///            | zig-zag varint index | zig-zag varint qx, qy, qz | varint a, b, c   (every field, declaration order)
    /// </code>
    /// The decoder accepts exactly one byte sequence per value (strict varints, payload ranges, subkinds); everything
    /// else is a <see cref="DecodeException"/>.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class NetRefCodec
    {
        private const int TagNullOrLocal = 0;
        private const int TagSlot = 1;
        private const int TagPrefab = 2;
        private const int TagExtended = 3;
        private const byte SubkindProvisional = 0;

        /// <summary>Appends <paramref name="value"/>.</summary>
        public static void Write(ByteWriter w, in NetRef value)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            switch (value.Kind)
            {
                case NetRefKind.Null:
                    w.WriteByte(0);                                              // (0 << 2) | 0
                    return;
                case NetRefKind.Local:
                    w.WriteByte(4);                                              // (1 << 2) | 0
                    return;
                case NetRefKind.Slot:
                    w.WriteVarUInt64(((ulong)value.Value << 2) | TagSlot);
                    return;
                case NetRefKind.Prefab:
                    w.WriteVarUInt64(((ulong)value.Value << 2) | TagPrefab);
                    return;
                case NetRefKind.Provisional:
                    w.WriteByte(TagExtended);                                    // (0 << 2) | 3
                    w.WriteByte(SubkindProvisional);
                    w.WriteVarUInt32(value.Value);
                    return;
                case NetRefKind.Derived:
                    w.WriteByte(TagExtended);
                    WriteKey(w, value.Key);
                    return;
                default:
                    throw new ArgumentException("cannot encode a NetRef of unknown kind " + ((int)value.Kind).ToString(CultureInfo.InvariantCulture), nameof(value));
            }
        }

        /// <summary>Reads one reference. Throws <see cref="DecodeException"/> for truncated or non-canonical input.</summary>
        public static NetRef Read(ByteReader r)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            ulong head = r.ReadVarUInt64();
            ulong payload = head >> 2;
            switch ((int)(head & 3))
            {
                case TagNullOrLocal:
                    if (payload == 0) return NetRef.Null;
                    if (payload == 1) return NetRef.Local;
                    throw new DecodeException("entity reference: null/local tag with payload " + WireFormat.Format(payload));
                case TagSlot:
                    if (payload == 0 || payload > NetSlot.MaxValue)
                    {
                        throw new DecodeException("entity reference: slot " + WireFormat.Format(payload) + " outside 1 ... 0xFFFFFF");
                    }
                    return NetRef.ForSlot(new NetSlot((uint)payload));
                case TagPrefab:
                    if (payload > int.MaxValue) throw new DecodeException("entity reference: prefab rank " + WireFormat.Format(payload) + " exceeds int.MaxValue");
                    return NetRef.ForPrefab(new PrefabRank((int)payload));
                default:
                    if (payload != 0) throw new DecodeException("entity reference: extended tag with payload " + WireFormat.Format(payload) + " (must be 0)");
                    byte subkind = r.PeekByte();
                    if (subkind == SubkindProvisional)
                    {
                        r.ReadByte();
                        uint id = r.ReadVarUInt32();
                        if (id == 0) throw new DecodeException("entity reference: provisional LocalId 0");
                        return NetRef.ForProvisional(new LocalId(id));
                    }
                    DerivedKey key = ReadKey(r);
                    return NetRef.ForDerived(in key);
            }
        }

        /// <summary>Appends a derived key: its kind byte followed by every field.</summary>
        public static void WriteKey(ByteWriter w, in DerivedKey key)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            if (!DerivedKey.IsValidKind((byte)key.Kind)) throw new ArgumentException("the derived key has no valid kind (default DerivedKey?)", nameof(key));
            w.WriteByte((byte)key.Kind);
            w.WriteVarUInt32(key.Owner.Value);
            w.WriteVarUInt32(unchecked((uint)(key.Prefab.Value + 1)));           // PrefabRank >= -1, so 0 ... 2^31
            w.WriteVarInt32(key.Index);
            w.WriteVarInt32(key.QX);
            w.WriteVarInt32(key.QY);
            w.WriteVarInt32(key.QZ);
            w.WriteVarUInt64(key.A);
            w.WriteVarUInt64(key.B);
            w.WriteVarUInt64(key.C);
        }

        /// <summary>Reads a derived key written by <see cref="WriteKey"/>. Throws <see cref="DecodeException"/>.</summary>
        public static DerivedKey ReadKey(ByteReader r)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            byte kind = r.ReadByte();
            if (!DerivedKey.IsValidKind(kind)) throw new DecodeException("derived key: unknown kind " + kind.ToString(CultureInfo.InvariantCulture));
            uint owner = r.ReadVarUInt32();
            if (owner > NetSlot.MaxValue) throw new DecodeException("derived key: owner slot " + WireFormat.Format(owner) + " exceeds 0xFFFFFF");
            uint prefabPlusOne = r.ReadVarUInt32();
            if (prefabPlusOne > 0x80000000u) throw new DecodeException("derived key: prefab rank " + WireFormat.Format(prefabPlusOne) + " - 1 exceeds int.MaxValue");
            int index = r.ReadVarInt32();
            int qx = r.ReadVarInt32();
            int qy = r.ReadVarInt32();
            int qz = r.ReadVarInt32();
            ulong a = r.ReadVarUInt64();
            ulong b = r.ReadVarUInt64();
            ulong c = r.ReadVarUInt64();
            PrefabRank prefab = prefabPlusOne == 0 ? PrefabRank.None : new PrefabRank((int)(prefabPlusOne - 1));
            return new DerivedKey((DerivedKeyKind)kind, new NetSlot(owner), prefab, index, qx, qy, qz, a, b, c);
        }
    }
}
