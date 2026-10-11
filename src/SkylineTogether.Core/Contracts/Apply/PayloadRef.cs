using System;
using System.Globalization;

namespace SkylineTogether.Core
{
    /// <summary>A byte range inside a <see cref="PayloadArena"/> (canonical bytes of one component or channel record).</summary>
    public struct PayloadRef : IEquatable<PayloadRef>
    {
        public int Offset;
        public int Length;

        public PayloadRef(int offset, int length)
        {
            Offset = offset;
            Length = length;
        }

        /// <summary>True for a zero-length range (presence-only values).</summary>
        public bool IsEmpty => Length == 0;

        public bool Equals(PayloadRef other) => Offset == other.Offset && Length == other.Length;

        public override bool Equals(object obj) => obj is PayloadRef other && Equals(other);

        public override int GetHashCode() => (Offset * 397) ^ Length;

        public override string ToString()
        {
            return "[" + Offset.ToString(CultureInfo.InvariantCulture) + "+" + Length.ToString(CultureInfo.InvariantCulture) + "]";
        }
    }
}
