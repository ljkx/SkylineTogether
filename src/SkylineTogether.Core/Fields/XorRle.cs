using System;
using System.Globalization;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Fields
{
    /// <summary>
    /// XOR delta between two versions of a byte array, zero-run-length coded (ARCH §4.6.4, §4.10.8: cell maps receive
    /// <c>XOR(current, last acknowledged version)</c>). Unchanged bytes XOR to zero and collapse into runs, so a map
    /// that changed in a few cells costs a few bytes before compression.
    /// <code>varuint count | zero-run pairs over previous[i] ^ current[i] (see ZeroRunCodec)</code>
    /// A null <c>previous</c> means all zeros, which turns the delta into a keyframe that
    /// <see cref="ApplyDelta"/> reproduces on a zeroed target.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class XorRle
    {
        /// <summary>Appends the delta that turns <c>previous[0 .. count)</c> into <c>current[0 .. count)</c>.</summary>
        public static void EncodeDelta(byte[] previous, byte[] current, int count, ByteWriter output)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (count < 0 || count > current.Length) throw new ArgumentOutOfRangeException(nameof(count), count, "count must lie in [0, current.Length]");
            if (previous != null && count > previous.Length) throw new ArgumentOutOfRangeException(nameof(count), count, "previous is shorter than count");
            output.WriteVarUInt32((uint)count);
            int position = output.Reserve(ZeroRunCodec.MaxEncodedLength(count));
            int written = ZeroRunCodec.Encode(current, 0, previous, 0, count, output.Buffer, position);
            output.Truncate(position + written);
        }

        /// <summary>
        /// XORs a delta written by <see cref="EncodeDelta"/> into <c>target[0 .. count)</c>, turning the previous
        /// version into the current one. The delta must cover exactly <paramref name="count"/> bytes. The whole delta is
        /// validated before <paramref name="target"/> is touched, so on <see cref="DecodeException"/> the target is
        /// unchanged; the reader then stands somewhere inside the delta.
        /// </summary>
        public static void ApplyDelta(ByteReader input, byte[] target, int count)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (count < 0 || count > target.Length) throw new ArgumentOutOfRangeException(nameof(count), count, "count must lie in [0, target.Length]");
            uint declared = input.ReadVarUInt32();
            if (declared != (uint)count)
            {
                throw new DecodeException("XOR delta: covers " + declared + " bytes, the target has " + count.ToString(CultureInfo.InvariantCulture));
            }
            // a zero-length segment exposes the reader's array and position without consuming anything
            ArraySegment<byte> rest = input.ReadSegment(0);
            int end = rest.Offset + input.Remaining;
            int consumed = ZeroRunCodec.Decode(rest.Array, rest.Offset, end, null, 0, count, ZeroRunCodec.Mode.Validate);
            ZeroRunCodec.Decode(rest.Array, rest.Offset, end, target, 0, count, ZeroRunCodec.Mode.Xor);
            input.Skip(consumed);
        }

        /// <summary>
        /// Checks that <c>src[offset .. offset + length)</c> holds exactly one well-formed delta of
        /// <paramref name="count"/> bytes; returns the position of its zero-run pairs. Throws <see cref="DecodeException"/>.
        /// </summary>
        internal static int ValidateExact(byte[] src, int offset, int length, int count)
        {
            int p = offset;
            int end = offset + length;
            uint declared = ZeroRunCodec.ReadVarUInt32(src, ref p, end, offset);
            if (declared != (uint)count)
            {
                throw new DecodeException("XOR delta: covers " + declared + " bytes, the map has " + count.ToString(CultureInfo.InvariantCulture));
            }
            int consumed = ZeroRunCodec.Decode(src, p, end, null, 0, count, ZeroRunCodec.Mode.Validate);
            if (p + consumed != end) throw new DecodeException("XOR delta: " + (end - p - consumed) + " trailing bytes");
            return p;
        }

        /// <summary>
        /// Core of <see cref="ApplyDelta"/> over a byte range that holds exactly one delta (used by
        /// <see cref="CellMapVersionRing"/>): validates the complete delta first, then applies it, either XORed into
        /// <paramref name="target"/> or, with <paramref name="overwrite"/>, onto an implicit zero base (keyframes).
        /// </summary>
        internal static void ApplyExact(byte[] src, int offset, int length, byte[] target, int count, bool overwrite)
        {
            int pairs = ValidateExact(src, offset, length, count);
            ZeroRunCodec.Decode(src, pairs, offset + length, target, 0, count, overwrite ? ZeroRunCodec.Mode.Overwrite : ZeroRunCodec.Mode.Xor);
        }
    }
}
