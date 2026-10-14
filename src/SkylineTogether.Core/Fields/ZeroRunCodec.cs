using System;
using System.Globalization;

namespace SkylineTogether.Core.Fields
{
    /// <summary>
    /// The zero-run-length coding shared by the field codecs (ARCHITECTURE.md#batch-encoding, #zone-cells-cell-maps-terrain-and-water). It removes the long zero
    /// runs of XOR deltas and filtered tile planes before the general-purpose compressor sees them.
    /// <para>
    /// Format: pairs <c>varuint zeroRun, varuint literalRun, literalRun bytes</c> until exactly <c>count</c> bytes are
    /// described. The encoder is deterministic: each pair starts with the maximal zero run at its position, and its
    /// literal run extends up to the next run of at least <see cref="MinZeroRun"/> zero bytes (shorter zero runs are
    /// cheaper as literals than a new pair header). A pair that describes no byte is rejected, so a decoder always
    /// terminates. Optionally the coded bytes are the XOR of two arrays (the cell-map delta, ARCHITECTURE.md#zone-cells-cell-maps-terrain-and-water).
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    internal static class ZeroRunCodec
    {
        /// <summary>Zero runs shorter than this stay inside a literal run.</summary>
        internal const int MinZeroRun = 3;

        internal enum Mode : byte
        {
            /// <summary>Parse and check the stream without writing anything.</summary>
            Validate,
            /// <summary>Write zero runs as zeros and literals as they are (the target is fully overwritten).</summary>
            Overwrite,
            /// <summary>Leave zero runs untouched and XOR literal bytes into the target (applies an XOR delta).</summary>
            Xor
        }

        /// <summary>
        /// Upper bound of the encoded size of <paramref name="count"/> bytes. The first pair adds at most 6 header bytes
        /// to the bytes it covers (a zero run of 0 plus a 5-byte literal length). Every later pair starts with at least
        /// <see cref="MinZeroRun"/> zero bytes, which pay for its two varints unless its literal run has 16 KiB or more
        /// (a length of 3 or more bytes); such a pair adds at most 3 bytes per 16 KiB of literals. <c>count / 64 + 16</c>
        /// covers both with a wide margin.
        /// </summary>
        internal static int MaxEncodedLength(int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            return checked(count + (count >> 6) + 16);
        }

        /// <summary>
        /// Encodes <c>a[aOffset + i] ^ b[bOffset + i]</c> for i in [0, count) (or <c>a[aOffset + i]</c> when
        /// <paramref name="b"/> is null) into <paramref name="dst"/>, which needs <see cref="MaxEncodedLength"/> bytes
        /// from <paramref name="dstOffset"/>. Returns the number of bytes written.
        /// </summary>
        internal static int Encode(byte[] a, int aOffset, byte[] b, int bOffset, int count, byte[] dst, int dstOffset)
        {
            if (a == null) throw new ArgumentNullException(nameof(a));
            if (dst == null) throw new ArgumentNullException(nameof(dst));
            if (count < 0 || aOffset < 0 || aOffset > a.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            if (b != null && (bOffset < 0 || bOffset > b.Length - count)) throw new ArgumentOutOfRangeException(nameof(bOffset));
            if (dstOffset < 0 || dstOffset > dst.Length - MaxEncodedLength(count)) throw new ArgumentOutOfRangeException(nameof(dstOffset), "the destination is smaller than MaxEncodedLength");

            int p = dstOffset;
            int i = 0;
            while (i < count)
            {
                int zeroStart = i;
                while (i < count && At(a, aOffset, b, bOffset, i) == 0) i++;
                int zeros = i - zeroStart;
                int literalStart = i;
                while (i < count)
                {
                    if (At(a, aOffset, b, bOffset, i) != 0)
                    {
                        i++;
                        continue;
                    }
                    int j = i + 1;
                    while (j < count && j - i < MinZeroRun && At(a, aOffset, b, bOffset, j) == 0) j++;
                    if (j - i >= MinZeroRun) break;   // a long zero run starts the next pair
                    i = j;                             // a short one stays in the literal
                }
                int literals = i - literalStart;
                p = WriteVarUInt32(dst, p, (uint)zeros);
                p = WriteVarUInt32(dst, p, (uint)literals);
                if (b == null)
                {
                    Buffer.BlockCopy(a, aOffset + literalStart, dst, p, literals);
                }
                else
                {
                    int ai = aOffset + literalStart, bi = bOffset + literalStart;
                    for (int k = 0; k < literals; k++) dst[p + k] = (byte)(a[ai + k] ^ b[bi + k]);
                }
                p += literals;
            }
            return p - dstOffset;
        }

        /// <summary>
        /// Decodes a stream that describes exactly <paramref name="count"/> bytes from <c>src[srcOffset ..
        /// srcEnd)</c> into <c>target[targetOffset ..]</c> according to <paramref name="mode"/>; returns the number of
        /// source bytes consumed (the stream may be followed by other data). Throws <see cref="DecodeException"/> on any
        /// malformed input; in <see cref="Mode.Validate"/> the target is not touched (and may be null), which lets
        /// callers check a stream completely before they modify state.
        /// </summary>
        internal static int Decode(byte[] src, int srcOffset, int srcEnd, byte[] target, int targetOffset, int count, Mode mode)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (srcOffset < 0 || srcEnd < srcOffset || srcEnd > src.Length) throw new ArgumentOutOfRangeException(nameof(srcEnd));
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (mode != Mode.Validate)
            {
                if (target == null) throw new ArgumentNullException(nameof(target));
                if (targetOffset < 0 || targetOffset > target.Length - count) throw new ArgumentOutOfRangeException(nameof(targetOffset));
            }

            int p = srcOffset;
            int produced = 0;
            while (produced < count)
            {
                uint zeros = ReadVarUInt32(src, ref p, srcEnd, srcOffset);
                uint literals = ReadVarUInt32(src, ref p, srcEnd, srcOffset);
                int remaining = count - produced;
                if (zeros == 0 && literals == 0) throw Invalid("a zero-run pair describes no byte", p, srcOffset);
                if (zeros > (uint)remaining) throw Invalid("a zero run of " + zeros + " bytes overruns the " + remaining + " bytes left", p, srcOffset);
                if (mode == Mode.Overwrite && zeros > 0) Array.Clear(target, targetOffset + produced, (int)zeros);
                produced += (int)zeros;
                remaining -= (int)zeros;
                if (literals > (uint)remaining) throw Invalid("a literal run of " + literals + " bytes overruns the " + remaining + " bytes left", p, srcOffset);
                int n = (int)literals;
                if (n > srcEnd - p) throw Invalid("a literal run of " + n + " bytes overruns the input (" + (srcEnd - p) + " left)", p, srcOffset);
                if (mode == Mode.Overwrite)
                {
                    Buffer.BlockCopy(src, p, target, targetOffset + produced, n);
                }
                else if (mode == Mode.Xor)
                {
                    int t = targetOffset + produced;
                    for (int k = 0; k < n; k++) target[t + k] ^= src[p + k];
                }
                p += n;
                produced += n;
            }
            return p - srcOffset;
        }

        private static byte At(byte[] a, int aOffset, byte[] b, int bOffset, int i)
        {
            return b == null ? a[aOffset + i] : (byte)(a[aOffset + i] ^ b[bOffset + i]);
        }

        private static int WriteVarUInt32(byte[] dst, int p, uint v)
        {
            while (v >= 0x80)
            {
                dst[p++] = (byte)(v | 0x80);
                v >>= 7;
            }
            dst[p++] = (byte)v;
            return p;
        }

        /// <summary>
        /// LEB128 with the canonical rules of <c>ByteReader.ReadVarUInt32</c> (at most 5 bytes, no overlong form);
        /// <paramref name="origin"/> is the stream start used in error messages.
        /// </summary>
        internal static uint ReadVarUInt32(byte[] src, ref int p, int end, int origin)
        {
            uint result = 0;
            int shift = 0;
            int first = p;
            for (int i = 0; i < 5; i++)
            {
                if (p >= end) throw Invalid("truncated varint", first, origin);
                byte b = src[p++];
                if (i == 4 && b > 0x0F) throw Invalid("varint exceeds 32 bits", first, origin);
                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    if (b == 0 && i > 0) throw Invalid("overlong varint", first, origin);
                    return result;
                }
                shift += 7;
            }
            throw Invalid("varint exceeds 32 bits", first, origin);
        }

        private static DecodeException Invalid(string what, int position, int origin)
        {
            return new DecodeException("zero-run stream: " + what + " (at byte " + (position - origin).ToString(CultureInfo.InvariantCulture) + ")");
        }
    }
}
