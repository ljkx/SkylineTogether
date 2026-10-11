using System;
using System.Globalization;

namespace SkylineTogether.Core
{
    /// <summary>
    /// Component type identity as a rank: the index of the type in the catalog sorted by <c>TypeInfo.StableTypeHash</c>
    /// (ARCH §3.6). Equal catalog fingerprints mean equal ranks on every peer.
    /// </summary>
    public readonly struct TypeRank : IEquatable<TypeRank>, IComparable<TypeRank>
    {
        /// <summary>The rank (index in the type catalog).</summary>
        public readonly ushort Value;

        public TypeRank(ushort value)
        {
            Value = value;
        }

        public bool Equals(TypeRank other) => Value == other.Value;

        public override bool Equals(object obj) => obj is TypeRank other && Value == other.Value;

        public override int GetHashCode() => Value;

        public int CompareTo(TypeRank other) => Value.CompareTo(other.Value);

        public override string ToString() => "type:" + Value.ToString(CultureInfo.InvariantCulture);

        public static bool operator ==(TypeRank a, TypeRank b) => a.Value == b.Value;

        public static bool operator !=(TypeRank a, TypeRank b) => a.Value != b.Value;

        public static bool operator <(TypeRank a, TypeRank b) => a.Value < b.Value;

        public static bool operator >(TypeRank a, TypeRank b) => a.Value > b.Value;

        public static bool operator <=(TypeRank a, TypeRank b) => a.Value <= b.Value;

        public static bool operator >=(TypeRank a, TypeRank b) => a.Value >= b.Value;
    }
}
