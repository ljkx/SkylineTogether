using System;
using System.Globalization;

namespace SkylineTogether.Core
{
    /// <summary>
    /// Prefab identity as a rank: the position of the prefab in the ordinally sorted list of every peer's
    /// <c>PrefabID</c>s (ARCH §3.5). Equal prefab digests mean equal ranks, so no table is ever transferred.
    /// Ranks start at 0; <see cref="None"/> is -1 (the wire encodes <c>rank + 1</c>, 0 = none, ARCH §4.6.3).
    /// Note that <c>default(PrefabRank)</c> is rank 0, not <see cref="None"/>.
    /// </summary>
    public readonly struct PrefabRank : IEquatable<PrefabRank>
    {
        /// <summary>The "no prefab" value (-1).</summary>
        public static readonly PrefabRank None = new PrefabRank(-1);

        /// <summary>The rank (0-based), or -1 for <see cref="None"/>.</summary>
        public readonly int Value;

        /// <summary>Creates a rank; values below -1 throw <see cref="ArgumentOutOfRangeException"/>.</summary>
        public PrefabRank(int value)
        {
            if (value < -1)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "a prefab rank is >= 0, or -1 for none");
            }
            Value = value;
        }

        /// <summary>True for <see cref="None"/>.</summary>
        public bool IsNone => Value < 0;

        public bool Equals(PrefabRank other) => Value == other.Value;

        public override bool Equals(object obj) => obj is PrefabRank other && Value == other.Value;

        public override int GetHashCode() => Value;

        public override string ToString() => IsNone ? "prefab:none" : "prefab:" + Value.ToString(CultureInfo.InvariantCulture);

        public static bool operator ==(PrefabRank a, PrefabRank b) => a.Value == b.Value;

        public static bool operator !=(PrefabRank a, PrefabRank b) => a.Value != b.Value;
    }
}
