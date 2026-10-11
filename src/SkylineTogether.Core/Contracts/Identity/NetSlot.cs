using System;
using System.Globalization;

namespace SkylineTogether.Core
{
    /// <summary>
    /// Dense 24-bit network identity of a replicated entity (ARCH §3.1): 1 ... 16 777 215, 0 = none. References carry
    /// slots on the wire (a varint of at most 4 bytes); peers keep their side tables as flat arrays indexed by slot.
    /// </summary>
    public readonly struct NetSlot : IEquatable<NetSlot>, IComparable<NetSlot>
    {
        /// <summary>The largest valid slot value (24 bits).</summary>
        public const uint MaxValue = 0x00FFFFFF;

        /// <summary>Slots per hash bucket: bucket = slot >> 10 (ARCH §8.2).</summary>
        public const int BucketShift = 10;

        /// <summary>The "no slot" value (0); equal to <c>default(NetSlot)</c>.</summary>
        public static readonly NetSlot None;

        /// <summary>The slot number (0 = none).</summary>
        public readonly uint Value;

        /// <summary>Creates a slot; throws <see cref="ArgumentOutOfRangeException"/> above <see cref="MaxValue"/>.</summary>
        public NetSlot(uint value)
        {
            if (value > MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "a NetSlot has 24 bits (at most 0x00FFFFFF)");
            }
            Value = value;
        }

        /// <summary>True for the "no slot" value 0.</summary>
        public bool IsNone => Value == 0;

        /// <summary>The hash bucket of this slot: <c>Value >> 10</c> (1024 slots per bucket, ARCH §8.2).</summary>
        public int Bucket => (int)(Value >> BucketShift);

        /// <summary>True when <paramref name="value"/> fits into a slot (decoders validate wire values with it).</summary>
        public static bool IsValidValue(uint value) => value <= MaxValue;

        /// <summary>Creates a slot when <paramref name="value"/> fits, without throwing (for decoders).</summary>
        public static bool TryCreate(uint value, out NetSlot slot)
        {
            if (value > MaxValue)
            {
                slot = None;
                return false;
            }
            slot = new NetSlot(value);
            return true;
        }

        public bool Equals(NetSlot other) => Value == other.Value;

        public override bool Equals(object obj) => obj is NetSlot other && Value == other.Value;

        public override int GetHashCode() => (int)Value;

        public int CompareTo(NetSlot other) => Value.CompareTo(other.Value);

        public override string ToString() => IsNone ? "slot:none" : "slot:" + Value.ToString(CultureInfo.InvariantCulture);

        public static bool operator ==(NetSlot a, NetSlot b) => a.Value == b.Value;

        public static bool operator !=(NetSlot a, NetSlot b) => a.Value != b.Value;

        public static bool operator <(NetSlot a, NetSlot b) => a.Value < b.Value;

        public static bool operator >(NetSlot a, NetSlot b) => a.Value > b.Value;

        public static bool operator <=(NetSlot a, NetSlot b) => a.Value <= b.Value;

        public static bool operator >=(NetSlot a, NetSlot b) => a.Value >= b.Value;
    }
}
