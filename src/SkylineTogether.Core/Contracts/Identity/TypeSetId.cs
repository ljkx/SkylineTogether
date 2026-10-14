using System;
using System.Globalization;

namespace SkylineTogether.Core
{
    /// <summary>
    /// Dictionary code of a replicated signature (the sorted set of an entity's replicated type ranks) in the
    /// session-global, append-only type-set registry (docs/history/ARCHITECTURE-original.md §3.6). 0 is <see cref="None"/>; the registry assigns ids
    /// from 1 upwards.
    /// </summary>
    public readonly struct TypeSetId : IEquatable<TypeSetId>
    {
        /// <summary>The "no type set" value (0); equal to <c>default(TypeSetId)</c>.</summary>
        public static readonly TypeSetId None;

        /// <summary>The registry id (0 = none).</summary>
        public readonly int Value;

        public TypeSetId(int value)
        {
            Value = value;
        }

        /// <summary>True for the "no type set" value 0.</summary>
        public bool IsNone => Value == 0;

        public bool Equals(TypeSetId other) => Value == other.Value;

        public override bool Equals(object obj) => obj is TypeSetId other && Value == other.Value;

        public override int GetHashCode() => Value;

        public override string ToString() => IsNone ? "typeset:none" : "typeset:" + Value.ToString(CultureInfo.InvariantCulture);

        public static bool operator ==(TypeSetId a, TypeSetId b) => a.Value == b.Value;

        public static bool operator !=(TypeSetId a, TypeSetId b) => a.Value != b.Value;
    }
}
