using System;
using System.Globalization;

namespace SkylineTogether.Core
{
    /// <summary>
    /// Slot plus generation as one 32-bit value, <c>(generation &lt;&lt; 24) | slot</c> (ARCHITECTURE.md#net-ids). Used for persistence
    /// (the <c>NetIdSystem</c> save block) and diagnostics; the generation rejects stale operations after slot reuse.
    /// </summary>
    public readonly struct NetId : IEquatable<NetId>
    {
        /// <summary>The packed value: generation in the top 8 bits, slot in the low 24 bits.</summary>
        public readonly uint Value;

        public NetId(uint value)
        {
            Value = value;
        }

        public NetId(NetSlot slot, byte generation)
        {
            Value = ((uint)generation << 24) | slot.Value;
        }

        /// <summary>The slot part (low 24 bits).</summary>
        public NetSlot Slot => new NetSlot(Value & NetSlot.MaxValue);

        /// <summary>The generation part (top 8 bits).</summary>
        public byte Generation => (byte)(Value >> 24);

        public bool Equals(NetId other) => Value == other.Value;

        public override bool Equals(object obj) => obj is NetId other && Value == other.Value;

        public override int GetHashCode() => (int)Value;

        public override string ToString()
        {
            return "netid:" + (Value & NetSlot.MaxValue).ToString(CultureInfo.InvariantCulture) + "/"
                   + Generation.ToString(CultureInfo.InvariantCulture);
        }

        public static bool operator ==(NetId a, NetId b) => a.Value == b.Value;

        public static bool operator !=(NetId a, NetId b) => a.Value != b.Value;
    }
}
