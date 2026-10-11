namespace SkylineTogether.Core.Fields
{
    /// <summary>
    /// IEEE 754 binary16 conversions (netstandard2.0 has no <c>System.Half</c>): round-to-nearest-even from single
    /// precision, exact back to single precision, with subnormals, infinities and NaN. Used for the optional water
    /// velocity channels ("velocity as 2×f16", ARCH §4.10.10). Pure bit manipulation, identical on every platform.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    internal static class HalfFloat
    {
        /// <summary>The largest finite half value (65504).</summary>
        internal const float MaxValue = 65504f;

        internal static unsafe ushort FromSingle(float value)
        {
            uint f = *(uint*)&value;
            uint sign = (f >> 16) & 0x8000u;
            int exponent = (int)((f >> 23) & 0xFF);
            uint mantissa = f & 0x7FFFFFu;
            if (exponent == 0xFF)
            {
                // infinity keeps its sign; NaN becomes a quiet NaN
                return (ushort)(sign | 0x7C00u | (mantissa != 0 ? 0x200u : 0u));
            }
            int e = exponent - 127 + 15;
            if (e >= 0x1F) return (ushort)(sign | 0x7C00u);   // overflow → infinity
            if (e <= 0)
            {
                // a half subnormal (or zero): value = m × 2^-24
                if (e < -10) return (ushort)sign;
                mantissa |= 0x800000u;
                int shift = 14 - e;
                uint half = mantissa >> shift;
                uint rest = mantissa & ((1u << shift) - 1);
                uint halfway = 1u << (shift - 1);
                if (rest > halfway || (rest == halfway && (half & 1) != 0)) half++;
                return (ushort)(sign | half);
            }
            uint h = sign | ((uint)e << 10) | (mantissa >> 13);
            uint r = mantissa & 0x1FFFu;
            // a carry out of the mantissa correctly moves to the next exponent (or to infinity)
            if (r > 0x1000u || (r == 0x1000u && (h & 1) != 0)) h++;
            return (ushort)h;
        }

        internal static unsafe float ToSingle(ushort half)
        {
            uint sign = (uint)(half & 0x8000) << 16;
            int exponent = (half >> 10) & 0x1F;
            uint mantissa = (uint)(half & 0x3FF);
            uint f;
            if (exponent == 0)
            {
                if (mantissa == 0)
                {
                    f = sign;
                }
                else
                {
                    // normalise the subnormal: shift until the implicit bit appears
                    int e = -1;
                    do
                    {
                        e++;
                        mantissa <<= 1;
                    }
                    while ((mantissa & 0x400u) == 0);
                    mantissa &= 0x3FFu;
                    f = sign | ((uint)(127 - 15 - e) << 23) | (mantissa << 13);
                }
            }
            else if (exponent == 0x1F)
            {
                f = sign | 0x7F800000u | (mantissa << 13);
            }
            else
            {
                f = sign | ((uint)(exponent - 15 + 127) << 23) | (mantissa << 13);
            }
            return *(float*)&f;
        }

        /// <summary>False for infinities and NaN.</summary>
        internal static bool IsFinite(ushort half) => (half & 0x7C00) != 0x7C00;
    }
}
