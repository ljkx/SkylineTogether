using System;
using System.Globalization;

namespace SkylineTogether.Core
{
    /// <summary>
    /// Number of a host frame with replication output (ARCHITECTURE.md#deferred-values-and-link-pacing): monotonic, the unit of ordering, catch-up and
    /// acknowledgement. 0 means "no epoch" (nothing applied yet); the host's first epoch is 1. At 60 epochs per second
    /// a 32-bit counter lasts more than two years, so comparisons are plain unsigned comparisons (no wrap-around).
    /// </summary>
    public readonly struct EpochId : IEquatable<EpochId>, IComparable<EpochId>
    {
        /// <summary>The "no epoch" value (0); equal to <c>default(EpochId)</c>.</summary>
        public static readonly EpochId None;

        /// <summary>The epoch number (0 = none).</summary>
        public readonly uint Value;

        public EpochId(uint value)
        {
            Value = value;
        }

        /// <summary>True for the "no epoch" value 0.</summary>
        public bool IsNone => Value == 0;

        /// <summary>The following epoch (<c>Value + 1</c>).</summary>
        public EpochId Next() => new EpochId(unchecked(Value + 1));

        public bool Equals(EpochId other) => Value == other.Value;

        public override bool Equals(object obj) => obj is EpochId other && Value == other.Value;

        public override int GetHashCode() => (int)Value;

        public int CompareTo(EpochId other) => Value.CompareTo(other.Value);

        public override string ToString() => "epoch:" + Value.ToString(CultureInfo.InvariantCulture);

        public static bool operator ==(EpochId a, EpochId b) => a.Value == b.Value;

        public static bool operator !=(EpochId a, EpochId b) => a.Value != b.Value;

        public static bool operator <(EpochId a, EpochId b) => a.Value < b.Value;

        public static bool operator >(EpochId a, EpochId b) => a.Value > b.Value;

        public static bool operator <=(EpochId a, EpochId b) => a.Value <= b.Value;

        public static bool operator >=(EpochId a, EpochId b) => a.Value >= b.Value;
    }
}
