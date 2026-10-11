using System;
using System.Globalization;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// Allocation-free primitives shared by the protocol codecs: strict LEB128 reads over a byte window (the rules of
    /// <see cref="Buffers.ByteReader"/>: no overlong encodings, nothing beyond the type width), varint sizes, and the
    /// float rules (writers map non-finite values to 0, readers reject them).
    /// </summary>
    internal static class WireFormat
    {
        /// <summary>Encoded size of an unsigned LEB128 value (1-5 bytes).</summary>
        internal static int VarUInt32Size(uint value)
        {
            int size = 1;
            while (value >= 0x80)
            {
                value >>= 7;
                size++;
            }
            return size;
        }

        /// <summary>Encoded size of an unsigned LEB128 value (1-10 bytes).</summary>
        internal static int VarUInt64Size(ulong value)
        {
            int size = 1;
            while (value >= 0x80)
            {
                value >>= 7;
                size++;
            }
            return size;
        }

        /// <summary>
        /// Reads a strict LEB128 <c>uint</c> from <c>buffer[position .. limit-1]</c> and advances
        /// <paramref name="position"/>; <paramref name="what"/> names the field in error messages.
        /// </summary>
        internal static uint ReadVarUInt32(byte[] buffer, ref int position, int limit, string what)
        {
            uint result = 0;
            int shift = 0;
            for (int i = 0; i < 5; i++)
            {
                if (position >= limit) throw new DecodeException(what + ": truncated varint");
                byte b = buffer[position++];
                if (i == 4 && b > 0x0F) throw new DecodeException(what + ": varint exceeds 32 bits");
                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    if (b == 0 && i > 0) throw new DecodeException(what + ": overlong varint");
                    return result;
                }
                shift += 7;
            }
            throw new DecodeException(what + ": varint exceeds 32 bits");
        }

        /// <summary>Replaces NaN and infinities by 0 (writers of float fields; readers reject them).</summary>
        internal static float Finite(float value) => float.IsNaN(value) || float.IsInfinity(value) ? 0f : value;

        /// <summary>Replaces NaN and infinities by 0.</summary>
        internal static double Finite(double value) => double.IsNaN(value) || double.IsInfinity(value) ? 0d : value;

        internal static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        internal static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        internal static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);

        internal static string Format(ulong value) => value.ToString(CultureInfo.InvariantCulture);

        /// <summary>Validates <c>(offset, count)</c> as a window of <paramref name="buffer"/> (argument errors: caller bugs).</summary>
        internal static void CheckRange(byte[] buffer, int offset, int count, string bufferName)
        {
            if (buffer == null) throw new ArgumentNullException(bufferName);
            if (offset < 0 || count < 0 || offset > buffer.Length - count)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "the range lies outside " + bufferName);
            }
        }

        /// <summary>Like <see cref="CheckRange"/>, but a null buffer is accepted when <paramref name="count"/> is 0.</summary>
        internal static void CheckOptionalRange(byte[] buffer, int offset, int count, string bufferName)
        {
            if (buffer == null)
            {
                if (count != 0 || offset != 0) throw new ArgumentNullException(bufferName);
                return;
            }
            CheckRange(buffer, offset, count, bufferName);
        }
    }
}
