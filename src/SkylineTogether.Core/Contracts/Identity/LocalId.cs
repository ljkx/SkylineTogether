using System;
using System.Globalization;

namespace SkylineTogether.Core
{
    /// <summary>
    /// Provisional id of an entity a client created by prediction, used only inside proposals (ARCHITECTURE.md#net-ids, #host-validation-and-commit).
    /// Every client counts from 1, so on the wire a LocalId is always qualified by its origin peer (BIND records).
    /// 0 means "none".
    /// </summary>
    public readonly struct LocalId : IEquatable<LocalId>
    {
        /// <summary>The "no id" value (0); equal to <c>default(LocalId)</c>.</summary>
        public static readonly LocalId None;

        /// <summary>The counter value (0 = none).</summary>
        public readonly uint Value;

        public LocalId(uint value)
        {
            Value = value;
        }

        /// <summary>True for the "no id" value 0.</summary>
        public bool IsNone => Value == 0;

        public bool Equals(LocalId other) => Value == other.Value;

        public override bool Equals(object obj) => obj is LocalId other && Value == other.Value;

        public override int GetHashCode() => (int)Value;

        public override string ToString() => "local:" + Value.ToString(CultureInfo.InvariantCulture);

        public static bool operator ==(LocalId a, LocalId b) => a.Value == b.Value;

        public static bool operator !=(LocalId a, LocalId b) => a.Value != b.Value;
    }
}
