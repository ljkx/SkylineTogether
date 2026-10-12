namespace SkylineTogether.Core.Motion
{
    /// <summary>
    /// IEEE-754 binary16 conversion for exact velocities (ARCH §4.9.3: followed and inspected movers carry f16
    /// velocity). <c>System.Half</c> is not part of netstandard2.0, so the conversion is done by bit manipulation:
    /// round to nearest even, subnormals supported, and values beyond the binary16 range clamp to ±65504 (a velocity
    /// never becomes infinite). Pure and deterministic.
    /// </summary>
    internal static class MotionHalf
    {
        /// <summary>The largest finite binary16 value.</summary>
        public const float MaxValue = 65504f;

        /// <summary>Encodes a finite float (callers reject NaN and infinities first).</summary>
        public static ushort FromSingle(float value)
        {
            if (value > MaxValue) value = MaxValue;
            else if (value < -MaxValue) value = -MaxValue;
            uint bits = MotionMath.SingleToBits(value);
            uint sign = (bits >> 16) & 0x8000u;
            int exponent = (int)((bits >> 23) & 0xFF);
            uint mantissa = bits & 0x7FFFFFu;

            if (exponent == 0xFF)
            {
                // NaN or infinity cannot reach here after the clamp unless NaN; encode NaN as zero (callers validate)
                return (ushort)sign;
            }
            int halfExponent = exponent - 127 + 15;
            if (halfExponent >= 0x1F)
            {
                return (ushort)(sign | 0x7BFFu);   // unreachable after the clamp; keep finite
            }
            if (halfExponent <= 0)
            {
                // subnormal or zero: shift the mantissa with the implicit leading 1 into place
                if (halfExponent < -10) return (ushort)sign;
                uint full = mantissa | 0x800000u;
                int shift = 14 - halfExponent;              // 24 bits of significand down to the subnormal position
                uint halfMantissa = full >> shift;
                uint remainder = full & ((1u << shift) - 1u);
                uint halfway = 1u << (shift - 1);
                if (remainder > halfway || (remainder == halfway && (halfMantissa & 1u) != 0)) halfMantissa++;
                return (ushort)(sign | halfMantissa);       // a carry into the exponent field is the correct result
            }
            uint result = sign | ((uint)halfExponent << 10) | (mantissa >> 13);
            uint rest = mantissa & 0x1FFFu;
            if (rest > 0x1000u || (rest == 0x1000u && (result & 1u) != 0)) result++;   // may carry into the exponent: correct
            if ((result & 0x7C00u) == 0x7C00u) result = sign | 0x7BFFu;               // rounding overflow: clamp
            return (ushort)result;
        }

        /// <summary>Decodes a binary16 value; false for the infinity and NaN encodings (never produced by <see cref="FromSingle"/>).</summary>
        public static bool TryToSingle(ushort half, out float value)
        {
            uint sign = (uint)(half & 0x8000) << 16;
            int exponent = (half >> 10) & 0x1F;
            uint mantissa = (uint)(half & 0x3FF);
            if (exponent == 0x1F)
            {
                value = 0f;
                return false;
            }
            if (exponent == 0)
            {
                if (mantissa == 0)
                {
                    value = MotionMath.BitsToSingle(sign);
                    return true;
                }
                // subnormal: normalise
                int e = -1;
                do
                {
                    e++;
                    mantissa <<= 1;
                }
                while ((mantissa & 0x400u) == 0);
                mantissa &= 0x3FFu;
                uint bits = sign | ((uint)(127 - 15 - e) << 23) | (mantissa << 13);
                value = MotionMath.BitsToSingle(bits);
                return true;
            }
            uint normal = sign | ((uint)(exponent - 15 + 127) << 23) | (mantissa << 13);
            value = MotionMath.BitsToSingle(normal);
            return true;
        }
    }
}
