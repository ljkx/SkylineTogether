using System;
using System.Globalization;

namespace SkylineTogether.Core
{
    /// <summary>
    /// A portable entity reference inside canonical bytes (ARCH §4.3.3): null, local (not replicated), a slot, a prefab
    /// rank, a provisional <see cref="LocalId"/> (proposals only) or a derived key. The same bytes are hashed, so
    /// references are canonical; the wire codec is core-protocol's <c>NetRefCodec</c>.
    /// Values are created only through the factories, so equal references are equal field by field.
    /// </summary>
    public readonly struct NetRef : IEquatable<NetRef>
    {
        /// <summary>The reference kind.</summary>
        public readonly NetRefKind Kind;

        /// <summary>Slot value, prefab rank or local id, depending on <see cref="Kind"/>; 0 otherwise.</summary>
        public readonly uint Value;

        /// <summary>The derived key when <see cref="Kind"/> is <see cref="NetRefKind.Derived"/>; default otherwise.</summary>
        public readonly DerivedKey Key;

        /// <summary><c>Entity.Null</c>; equal to <c>default(NetRef)</c>.</summary>
        public static readonly NetRef Null;

        /// <summary>A real but unreplicated entity; decodes to <c>Entity.Null</c> and is counted in diagnostics.</summary>
        public static readonly NetRef Local = new NetRef(NetRefKind.Local, 0, default);

        private NetRef(NetRefKind kind, uint value, in DerivedKey key)
        {
            Kind = kind;
            Value = value;
            Key = key;
        }

        /// <summary>A reference to a bound replicated entity; <see cref="NetSlot.None"/> gives <see cref="Null"/>.</summary>
        public static NetRef ForSlot(NetSlot slot) => slot.IsNone ? Null : new NetRef(NetRefKind.Slot, slot.Value, default);

        /// <summary>A reference to a prefab entity; <see cref="PrefabRank.None"/> gives <see cref="Null"/>.</summary>
        public static NetRef ForPrefab(PrefabRank rank) => rank.IsNone ? Null : new NetRef(NetRefKind.Prefab, (uint)rank.Value, default);

        /// <summary>A reference to a predicted entity inside a proposal; <see cref="LocalId.None"/> gives <see cref="Null"/>.</summary>
        public static NetRef ForProvisional(LocalId id) => id.IsNone ? Null : new NetRef(NetRefKind.Provisional, id.Value, default);

        /// <summary>A reference to a derived entity; the key must have a defined kind.</summary>
        public static NetRef ForDerived(in DerivedKey key)
        {
            if (!DerivedKey.IsValidKind((byte)key.Kind))
            {
                throw new ArgumentException("the derived key has no valid kind (default DerivedKey?)", nameof(key));
            }
            return new NetRef(NetRefKind.Derived, 0, key);
        }

        /// <summary>True for <see cref="Null"/>.</summary>
        public bool IsNull => Kind == NetRefKind.Null;

        /// <summary>The slot of a <see cref="NetRefKind.Slot"/> reference; <see cref="NetSlot.None"/> for other kinds.</summary>
        public NetSlot Slot => Kind == NetRefKind.Slot ? new NetSlot(Value) : NetSlot.None;

        /// <summary>The rank of a <see cref="NetRefKind.Prefab"/> reference; <see cref="PrefabRank.None"/> for other kinds.</summary>
        public PrefabRank Prefab => Kind == NetRefKind.Prefab ? new PrefabRank((int)Value) : PrefabRank.None;

        /// <summary>The id of a <see cref="NetRefKind.Provisional"/> reference; <see cref="LocalId.None"/> for other kinds.</summary>
        public LocalId Provisional => Kind == NetRefKind.Provisional ? new LocalId(Value) : LocalId.None;

        public bool Equals(NetRef other) => Kind == other.Kind && Value == other.Value && Key.Equals(other.Key);

        public override bool Equals(object obj) => obj is NetRef other && Equals(other);

        public override int GetHashCode() => Kind == NetRefKind.Derived ? Key.GetHashCode() : ((int)Kind << 28) ^ (int)Value;

        public override string ToString()
        {
            switch (Kind)
            {
                case NetRefKind.Null: return "ref:null";
                case NetRefKind.Local: return "ref:local";
                case NetRefKind.Slot: return "ref:" + Slot;
                case NetRefKind.Prefab: return "ref:" + Prefab;
                case NetRefKind.Provisional: return "ref:" + Provisional;
                case NetRefKind.Derived: return "ref:" + Key;
                default: return "ref:?" + ((int)Kind).ToString(CultureInfo.InvariantCulture);
            }
        }

        public static bool operator ==(NetRef a, NetRef b) => a.Equals(b);

        public static bool operator !=(NetRef a, NetRef b) => !a.Equals(b);
    }
}
