using System;
using System.Globalization;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// A pure C# implementation of the LZ4 <b>block</b> format (no frame, no native dependency): the unit tests' codec
    /// and the last-resort fallback when neither native ZSTD nor native LZ4 works (ARCH §4.6.5; probes <c>P-ZSTD</c>,
    /// <c>P-LZ4</c>, Appendix B). The output is a standard LZ4 block, so the reference decoder (and the game's native LZ4,
    /// when present) decodes it; this decoder accepts every block a conforming LZ4 compressor produces and applies the
    /// end-of-block checks of the reference safe decoder (<c>LZ4_decompress_safe</c>) to everything else.
    /// <para>
    /// Block format: a sequence is a token (high nibble literal length, low nibble match length − 4; 15 = more length
    /// bytes follow, each adding up to 255), the literals, a 2-byte little-endian offset (1-65535) and the match length
    /// bytes. The last sequence holds literals only. End-of-block rules (kept by the compressor and enforced by the
    /// decoder): the last 5 bytes are literals, the last match starts at least 12 bytes before the end, and a literal
    /// run that ends within 12 output or 8 input bytes of the end must be the last sequence.
    /// </para>
    /// <para>
    /// Stateless and thread-safe: the 16 KiB hash table of the compressor lives on the stack. The compressor is the
    /// greedy single-pass algorithm of the reference compressor (4-byte hash into a 4096-entry table, acceleration 1),
    /// deterministic: equal input gives equal output. Its blocks can differ from those of a particular reference build,
    /// which picks the hash table layout by platform and input size, but every LZ4 decoder accepts them.
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class ManagedLz4Compressor : ICompressor
    {
        /// <summary>Largest input the format supports (<c>LZ4_MAX_INPUT_SIZE</c>).</summary>
        public const int MaxInputSize = 0x7E000000;

        private const int MinMatch = 4;
        private const int LastLiterals = 5;
        private const int MfLimit = 12;
        private const int MinCompressLength = MfLimit + 1;
        private const int HashLog = 12;
        private const int HashSize = 1 << HashLog;
        private const int MaxDistance = 65535;
        private const int SkipTrigger = 6;
        private const int RunMask = 15;

        public CompressionKind Kind => CompressionKind.ManagedLz4;

        /// <summary>Worst case of the format: <c>n + n / 255 + 16</c> (<c>LZ4_compressBound</c>).</summary>
        public int MaxCompressedSize(int length)
        {
            if (length < 0 || length > MaxInputSize) throw new ArgumentOutOfRangeException(nameof(length));
            return length + length / 255 + 16;
        }

        /// <summary>
        /// Compresses <c>src[srcOffset .. srcOffset+srcCount-1]</c> into <paramref name="dst"/> at
        /// <paramref name="dstOffset"/>, which must have room for <see cref="MaxCompressedSize"/> bytes; returns the
        /// block length. An empty input gives the one-byte block <c>00</c>.
        /// </summary>
        public unsafe int Compress(byte[] src, int srcOffset, int srcCount, byte[] dst, int dstOffset)
        {
            WireFormat.CheckRange(src, srcOffset, srcCount, nameof(src));
            if (dst == null) throw new ArgumentNullException(nameof(dst));
            if (srcCount > MaxInputSize) throw new ArgumentOutOfRangeException(nameof(srcCount));
            if (dstOffset < 0 || dstOffset > dst.Length || dst.Length - dstOffset < MaxCompressedSize(srcCount))
            {
                throw new ArgumentException("the destination needs room for MaxCompressedSize(" + srcCount.ToString(CultureInfo.InvariantCulture) + ") bytes", nameof(dst));
            }

            int op = dstOffset;
            int iend = srcOffset + srcCount;
            int anchor = srcOffset;
            if (srcCount >= MinCompressLength)
            {
                int* table = stackalloc int[HashSize];      // positions relative to srcOffset; 0 is a real position
                for (int i = 0; i < HashSize; i++) table[i] = 0;

                int ip0 = srcOffset;
                int mflimitPlusOne = iend - MfLimit + 1;    // a match starts before this position (≥ 12 bytes before the end)
                int matchlimit = iend - LastLiterals;       // no match may extend beyond this position
                int ip = srcOffset;

                table[Hash(Read32(src, ip))] = ip - ip0;
                ip++;
                int forwardH = Hash(Read32(src, ip));

                for (;;)
                {
                    // find a match; the step grows with failed attempts so incompressible data is skipped quickly
                    int match;
                    int forwardIp = ip;
                    int step = 1;
                    int searchMatchNb = 1 << SkipTrigger;
                    do
                    {
                        int h = forwardH;
                        ip = forwardIp;
                        forwardIp += step;
                        step = searchMatchNb++ >> SkipTrigger;
                        if (forwardIp > mflimitPlusOne) goto LastLiteralRun;
                        match = ip0 + table[h];
                        forwardH = Hash(Read32(src, forwardIp));
                        table[h] = ip - ip0;
                    }
                    while (match + MaxDistance < ip || Read32(src, match) != Read32(src, ip));

                    // extend the match backwards over equal bytes that are still literals
                    while (ip > anchor && match > ip0 && src[ip - 1] == src[match - 1])
                    {
                        ip--;
                        match--;
                    }

                    int token = op++;
                    int literalLength = ip - anchor;
                    op = WriteLength(dst, op, token, literalLength, true);
                    Buffer.BlockCopy(src, anchor, dst, op, literalLength);
                    op += literalLength;

                    for (;;)
                    {
                        int offset = ip - match;
                        dst[op++] = (byte)offset;
                        dst[op++] = (byte)(offset >> 8);

                        int matchLength = CountEqual(src, ip + MinMatch, match + MinMatch, matchlimit);
                        ip += MinMatch + matchLength;
                        op = WriteLength(dst, op, token, matchLength, false);
                        anchor = ip;

                        if (ip >= mflimitPlusOne) goto LastLiteralRun;

                        table[Hash(Read32(src, ip - 2))] = ip - 2 - ip0;

                        // an immediate match at the next position needs no literals
                        int h2 = Hash(Read32(src, ip));
                        match = ip0 + table[h2];
                        table[h2] = ip - ip0;
                        if (match + MaxDistance >= ip && Read32(src, match) == Read32(src, ip))
                        {
                            token = op++;
                            dst[token] = 0;
                            continue;
                        }
                        break;
                    }

                    ip++;
                    forwardH = Hash(Read32(src, ip));
                }
            }

        LastLiteralRun:
            {
                int token = op++;
                int lastRun = iend - anchor;
                op = WriteLength(dst, op, token, lastRun, true);
                Buffer.BlockCopy(src, anchor, dst, op, lastRun);
                op += lastRun;
            }
            return op - dstOffset;
        }

        /// <summary>
        /// Decodes one block into exactly <paramref name="expectedLength"/> bytes at <paramref name="dstOffset"/>.
        /// Throws <see cref="DecodeException"/> for every malformed block (truncation, a zero offset or one before the
        /// start of the output, a run beyond the output, a violated end-of-block rule, a length other than
        /// <paramref name="expectedLength"/>, trailing input) and when <paramref name="dst"/> is too small. Never reads
        /// or writes outside the given ranges.
        /// </summary>
        public int Decompress(byte[] src, int srcOffset, int srcCount, byte[] dst, int dstOffset, int expectedLength)
        {
            WireFormat.CheckRange(src, srcOffset, srcCount, nameof(src));
            if (dst == null) throw new ArgumentNullException(nameof(dst));
            if (expectedLength < 0) throw new DecodeException("LZ4: negative expected length");
            if (dstOffset < 0 || dstOffset > dst.Length || dst.Length - dstOffset < expectedLength)
            {
                throw new DecodeException("LZ4: the destination has no room for " + expectedLength.ToString(CultureInfo.InvariantCulture) + " bytes");
            }
            if (srcCount == 0) throw new DecodeException("LZ4: empty block");

            int ip = srcOffset;
            int iend = srcOffset + srcCount;
            int op = dstOffset;
            int oend = dstOffset + expectedLength;

            for (;;)
            {
                if (ip >= iend) throw new DecodeException("LZ4: block ends inside a sequence");
                int token = src[ip++];

                // literals
                int literalLength = token >> 4;
                if (literalLength == RunMask)
                {
                    // a literal run of 15 or more needs at least one length byte and 15 literals
                    if (ip >= iend - RunMask) throw new DecodeException("LZ4: literal length beyond the block");
                    literalLength += ReadExtraLength(src, ref ip, iend, oend - op, "literal");
                }
                bool nearEnd = literalLength > oend - MfLimit - op || literalLength > iend - (2 + 1 + LastLiterals) - ip;
                if (nearEnd)
                {
                    // within the end margins only the last sequence may end: it consumes the input exactly
                    if (literalLength != iend - ip || literalLength > oend - op)
                    {
                        throw new DecodeException("LZ4: invalid last literal run (" + literalLength.ToString(CultureInfo.InvariantCulture) + " bytes)");
                    }
                    Buffer.BlockCopy(src, ip, dst, op, literalLength);
                    ip += literalLength;
                    op += literalLength;
                    break;
                }
                Buffer.BlockCopy(src, ip, dst, op, literalLength);
                ip += literalLength;
                op += literalLength;

                // match (at least 8 input bytes remain, see nearEnd)
                int offset = src[ip] | (src[ip + 1] << 8);
                ip += 2;
                if (offset == 0) throw new DecodeException("LZ4: zero match offset");
                if (offset > op - dstOffset) throw new DecodeException("LZ4: match offset before the start of the output");
                int matchLength = token & RunMask;
                if (matchLength == RunMask)
                {
                    matchLength += ReadExtraLength(src, ref ip, iend, oend - op, "match");
                    if (ip >= iend - (LastLiterals - 1)) throw new DecodeException("LZ4: match length runs into the last literals");
                }
                matchLength += MinMatch;
                if (matchLength > oend - LastLiterals - op)
                {
                    throw new DecodeException("LZ4: match reaches into the last " + LastLiterals.ToString(CultureInfo.InvariantCulture) + " bytes of the output");
                }
                int from = op - offset;
                if (offset >= matchLength)
                {
                    Buffer.BlockCopy(dst, from, dst, op, matchLength);
                }
                else
                {
                    // overlapping copy repeats the last `offset` bytes; it must run forwards byte by byte
                    for (int i = 0; i < matchLength; i++) dst[op + i] = dst[from + i];
                }
                op += matchLength;
            }

            if (op != oend)
            {
                throw new DecodeException("LZ4: block decodes to " + (op - dstOffset).ToString(CultureInfo.InvariantCulture)
                                          + " bytes, expected " + expectedLength.ToString(CultureInfo.InvariantCulture));
            }
            return expectedLength;
        }

        /// <summary>Writes a literal (high nibble) or match (low nibble) length into the token and any extra length bytes.</summary>
        private static int WriteLength(byte[] dst, int op, int token, int length, bool literal)
        {
            if (length >= RunMask)
            {
                if (literal) dst[token] = (byte)(RunMask << 4);
                else dst[token] |= RunMask;
                int rest = length - RunMask;
                while (rest >= 255)
                {
                    dst[op++] = 255;
                    rest -= 255;
                }
                dst[op++] = (byte)rest;
            }
            else if (literal)
            {
                dst[token] = (byte)(length << 4);
            }
            else
            {
                dst[token] |= (byte)length;
            }
            return op;
        }

        /// <summary>Sums length bytes until one is below 255; fails as soon as the sum exceeds <paramref name="maxUseful"/>.</summary>
        private static int ReadExtraLength(byte[] src, ref int ip, int iend, int maxUseful, string what)
        {
            int length = 0;
            for (;;)
            {
                if (ip >= iend) throw new DecodeException("LZ4: truncated " + what + " length");
                int b = src[ip++];
                length += b;
                if (length > maxUseful) throw new DecodeException("LZ4: " + what + " length beyond the output");
                if (b != 255) return length;
            }
        }

        private static int CountEqual(byte[] src, int a, int b, int limit)
        {
            int start = a;
            while (a < limit && src[a] == src[b])
            {
                a++;
                b++;
            }
            return a - start;
        }

        private static uint Read32(byte[] src, int p)
        {
            return src[p] | ((uint)src[p + 1] << 8) | ((uint)src[p + 2] << 16) | ((uint)src[p + 3] << 24);
        }

        private static int Hash(uint sequence) => (int)((sequence * 2654435761u) >> (32 - HashLog));
    }
}
