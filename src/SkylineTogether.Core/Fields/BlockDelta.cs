using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Hashing;

namespace SkylineTogether.Core.Fields
{
    /// <summary>
    /// Block-hash delta for the warm join (ARCH §7.10, milestone M9): a client that holds a host-authored copy of the
    /// same city lineage (a resume point or a saved copy) receives only what differs instead of the whole snapshot.
    /// <list type="number">
    /// <item>The client hashes its copy: <see cref="HashBlocks"/> (xxHash128 per block, about 3 KB per 200 MB at 1 MiB
    /// blocks) and sends the list (<see cref="WriteBlockList"/>).</item>
    /// <item>The host reads the list with a bound (<see cref="ReadBlockList"/>, <see cref="MaxBlocksFor"/>) and builds the
    /// delta of its snapshot against it: <see cref="BuildDelta(Stream, IReadOnlyList{Digest128}, int, Stream)"/>.</item>
    /// <item>The client rebuilds the snapshot: <see cref="ApplyDelta(Stream, Stream, Stream, int, int)"/> with the block
    /// size and count of the list it sent, which verifies length and xxHash128 of the result.</item>
    /// </list>
    /// Blocks are content-defined (<see cref="ContentDefinedChunker"/>: average <c>blockSize</c>, between a quarter and
    /// four times of it), because saves are sequences of separately ZSTD-compressed buffers (R01 §4) whose positions
    /// shift whenever an earlier buffer changes size; with fixed offsets every block after the first change would
    /// differ.
    /// <para>
    /// Delta format (deterministic):
    /// <code>
    /// "STBD" | u8 version (1) | varuint blockSize | varuint receiverBlockCount
    /// ops: u8 1 Copy, varuint firstBlock, varuint blockCount   (consecutive receiver blocks = one basis range)
    ///      u8 2 Literal, varuint length, bytes                  (one source block the receiver lacks)
    ///      u8 0 End
    /// u64 sourceLength | u64 digestLo | u64 digestHi            (xxHash128 of the source)
    /// </code>
    /// Every stream is processed sequentially with constant memory (one chunk buffer of 8 × blockSize). Thread-safe
    /// (no shared state).
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class BlockDelta
    {
        /// <summary>The design's block size: 1 MiB.</summary>
        public const int DefaultBlockSize = 1 << 20;

        /// <summary>Smallest accepted (average) block size.</summary>
        public const int MinBlockSize = 256;

        /// <summary>Largest accepted (average) block size.</summary>
        public const int MaxBlockSize = 1 << 22;

        /// <summary>
        /// Largest output <see cref="ApplyDelta(Stream, Stream, Stream)"/> produces: blob sizes are capped at 2 GB
        /// (ARCH §14.5), read here as 2 GiB so no snapshot the blob layer accepts is ever refused.
        /// </summary>
        public const long MaxOutputBytes = 2L << 30;

        /// <summary>Format version of the delta stream.</summary>
        public const byte FormatVersion = 1;

        /// <summary>Format version of the block list written by <see cref="WriteBlockList"/>.</summary>
        public const byte BlockListVersion = 1;

        private const int DigestBytes = 16;
        private const byte OpEnd = 0;
        private const byte OpCopy = 1;
        private const byte OpLiteral = 2;
        private const int CopyBufferSize = 64 * 1024;
        private const uint MagicValue = 0x44425453;   // "STBD" read as a little-endian u32

        /// <summary>
        /// xxHash128 of every content-defined block of <paramref name="stream"/>, from its current position to its end.
        /// </summary>
        public static IReadOnlyList<Digest128> HashBlocks(Stream stream, int blockSize = 1 << 20)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (!stream.CanRead) throw new ArgumentException("the stream is not readable", nameof(stream));
            CheckBlockSize(blockSize);
            var digests = new List<Digest128>();
            var chunker = new ContentDefinedChunker(blockSize);
            chunker.Start(stream);
            try
            {
                while (chunker.Next(out byte[] buffer, out int offset, out int length))
                {
                    digests.Add(Digest128.Compute(buffer, offset, length));
                }
            }
            finally
            {
                chunker.Finish();
            }
            return digests.AsReadOnly();
        }

        /// <summary>
        /// Writes to <paramref name="delta"/> how to rebuild <paramref name="source"/> (from its current position to its
        /// end) from a file whose <see cref="HashBlocks"/> list with the same <paramref name="blockSize"/> is
        /// <paramref name="receiverBlocks"/>.
        /// </summary>
        public static void BuildDelta(Stream source, IReadOnlyList<Digest128> receiverBlocks, int blockSize, Stream delta)
        {
            BuildDelta(source, receiverBlocks, blockSize, delta, out _);
        }

        /// <summary><see cref="BuildDelta(Stream, IReadOnlyList{Digest128}, int, Stream)"/> that also reports what it produced.</summary>
        public static void BuildDelta(Stream source, IReadOnlyList<Digest128> receiverBlocks, int blockSize, Stream delta, out BlockDeltaStats stats)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (receiverBlocks == null) throw new ArgumentNullException(nameof(receiverBlocks));
            if (delta == null) throw new ArgumentNullException(nameof(delta));
            if (!source.CanRead) throw new ArgumentException("the source is not readable", nameof(source));
            if (!delta.CanWrite) throw new ArgumentException("the delta stream is not writable", nameof(delta));
            CheckBlockSize(blockSize);

            // first occurrence wins, so equal blocks always resolve to the same receiver index
            var index = new Dictionary<Digest128, int>(receiverBlocks.Count);
            for (int i = 0; i < receiverBlocks.Count; i++)
            {
                if (!index.ContainsKey(receiverBlocks[i])) index.Add(receiverBlocks[i], i);
            }

            stats = default(BlockDeltaStats);
            var writer = new ByteWriter(64);
            writer.WriteUInt32(MagicValue);
            writer.WriteByte(FormatVersion);
            writer.WriteVarUInt32((uint)blockSize);
            writer.WriteVarUInt32((uint)receiverBlocks.Count);
            Flush(writer, delta, ref stats);

            var hasher = new XxHash128Stream();
            var chunker = new ContentDefinedChunker(blockSize);
            int runFirst = 0, runCount = 0;
            chunker.Start(source);
            try
            {
                while (chunker.Next(out byte[] buffer, out int offset, out int length))
                {
                    hasher.Append(buffer, offset, length);
                    stats.SourceBytes += length;
                    stats.SourceBlocks++;
                    if (index.TryGetValue(Digest128.Compute(buffer, offset, length), out int block))
                    {
                        stats.MatchedBlocks++;
                        stats.CopiedBytes += length;
                        if (runCount > 0 && block == runFirst + runCount)
                        {
                            runCount++;
                            continue;
                        }
                        if (runCount > 0) WriteCopy(writer, delta, runFirst, runCount, ref stats);
                        runFirst = block;
                        runCount = 1;
                        continue;
                    }
                    if (runCount > 0)
                    {
                        WriteCopy(writer, delta, runFirst, runCount, ref stats);
                        runCount = 0;
                    }
                    writer.WriteByte(OpLiteral);
                    writer.WriteVarUInt32((uint)length);
                    Flush(writer, delta, ref stats);
                    delta.Write(buffer, offset, length);
                    stats.DeltaBytes += length;
                    stats.LiteralBytes += length;
                }
            }
            finally
            {
                chunker.Finish();
            }
            if (runCount > 0) WriteCopy(writer, delta, runFirst, runCount, ref stats);
            Digest128 digest = hasher.Finish();
            writer.WriteByte(OpEnd);
            writer.WriteUInt64((ulong)stats.SourceBytes);
            writer.WriteUInt64(digest.Lo);
            writer.WriteUInt64(digest.Hi);
            Flush(writer, delta, ref stats);
        }

        /// <summary>
        /// Rebuilds the source into <paramref name="output"/> from <paramref name="basis"/> (the receiver's file, from
        /// its current position; it must be seekable) and a delta built against the basis's block list. Throws
        /// <see cref="DecodeException"/> when the delta is malformed, when the basis is not the file the block list was
        /// computed from, or when the result does not match the source's length and xxHash128; the output is then
        /// incomplete and must be discarded. I/O errors of the streams propagate unchanged.
        /// </summary>
        public static void ApplyDelta(Stream basis, Stream delta, Stream output)
        {
            ApplyDelta(basis, delta, output, -1, -1);
        }

        /// <summary>
        /// <see cref="ApplyDelta(Stream, Stream, Stream)"/> for the receiver that sent the block list: the delta's header
        /// must name <paramref name="expectedBlockSize"/> and <paramref name="expectedBlockCount"/> (the values of the
        /// list it sent with <see cref="WriteBlockList"/>), otherwise it is rejected with a
        /// <see cref="DecodeException"/> before the basis is read at all. Without this check a delta built against
        /// another list (or a hostile header with a tiny block size) is detected only after the whole basis was
        /// re-chunked, and the chunking of a 2 GB basis into 256-byte blocks holds tens of millions of offsets.
        /// -1 skips the respective check.
        /// </summary>
        public static void ApplyDelta(Stream basis, Stream delta, Stream output, int expectedBlockSize, int expectedBlockCount)
        {
            if (expectedBlockSize != -1) CheckBlockSize(expectedBlockSize);
            if (expectedBlockCount < -1) throw new ArgumentOutOfRangeException(nameof(expectedBlockCount), expectedBlockCount, "expected -1 (any) or a block count");
            if (basis == null) throw new ArgumentNullException(nameof(basis));
            if (delta == null) throw new ArgumentNullException(nameof(delta));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (!basis.CanRead || !basis.CanSeek) throw new ArgumentException("the basis must be readable and seekable", nameof(basis));
            if (!delta.CanRead) throw new ArgumentException("the delta stream is not readable", nameof(delta));
            if (!output.CanWrite) throw new ArgumentException("the output is not writable", nameof(output));

            byte[] scratch = BufferPool.Rent(CopyBufferSize);
            try
            {
                ReadExactly(delta, scratch, 0, 5);
                uint magic = scratch[0] | ((uint)scratch[1] << 8) | ((uint)scratch[2] << 16) | ((uint)scratch[3] << 24);
                if (magic != MagicValue) throw new DecodeException("block delta: not a block delta stream");
                if (scratch[4] != FormatVersion) throw new DecodeException("block delta: unknown format version " + scratch[4].ToString(CultureInfo.InvariantCulture));
                uint blockSize = ReadVarUInt32(delta);
                if (blockSize < MinBlockSize || blockSize > MaxBlockSize) throw new DecodeException("block delta: invalid block size " + blockSize);
                if (expectedBlockSize != -1 && blockSize != (uint)expectedBlockSize)
                {
                    throw new DecodeException("block delta: built for block size " + blockSize + ", the block list used " + expectedBlockSize.ToString(CultureInfo.InvariantCulture));
                }
                uint blockCount = ReadVarUInt32(delta);
                if (blockCount > int.MaxValue - 1) throw new DecodeException("block delta: invalid block count " + blockCount);
                if (expectedBlockCount != -1 && blockCount != (uint)expectedBlockCount)
                {
                    throw new DecodeException("block delta: built against " + blockCount + " blocks, the block list had " + expectedBlockCount.ToString(CultureInfo.InvariantCulture));
                }

                long basisStart = basis.Position;
                List<long> offsets = BlockOffsets(basis, (int)blockSize, (int)blockCount);
                int maxLiteral = 4 * (int)blockSize;
                var hasher = new XxHash128Stream();
                long produced = 0;
                while (true)
                {
                    int op = delta.ReadByte();
                    if (op < 0) throw Truncated();
                    if (op == OpEnd) break;
                    long length;
                    if (op == OpCopy)
                    {
                        uint first = ReadVarUInt32(delta);
                        uint count = ReadVarUInt32(delta);
                        if (count == 0 || first >= blockCount || count > blockCount - first)
                        {
                            throw new DecodeException("block delta: copy of blocks " + first + "+" + count + " outside the " + blockCount + " basis blocks");
                        }
                        long from = offsets[(int)first];
                        length = offsets[(int)(first + count)] - from;
                        CheckOutput(produced, length);
                        basis.Position = basisStart + from;
                        Copy(basis, output, length, hasher, scratch, "the basis");
                    }
                    else if (op == OpLiteral)
                    {
                        length = ReadVarUInt32(delta);
                        if (length == 0 || length > maxLiteral) throw new DecodeException("block delta: invalid literal length " + length);
                        CheckOutput(produced, length);
                        Copy(delta, output, length, hasher, scratch, "the delta");
                    }
                    else
                    {
                        throw new DecodeException("block delta: unknown operation " + op.ToString(CultureInfo.InvariantCulture));
                    }
                    produced += length;
                }

                ReadExactly(delta, scratch, 0, 24);
                ulong sourceLength = ReadUInt64(scratch, 0);
                var expected = new Digest128(ReadUInt64(scratch, 8), ReadUInt64(scratch, 16));
                if (sourceLength != (ulong)produced)
                {
                    throw new DecodeException("block delta: rebuilt " + produced + " bytes, the source has " + sourceLength);
                }
                Digest128 actual = hasher.Finish();
                if (actual != expected)
                {
                    throw new DecodeException("block delta: digest of the rebuilt file " + actual + " differs from the source's " + expected);
                }
            }
            finally
            {
                BufferPool.Return(scratch);
            }
        }

        /// <summary>
        /// Appends the receiver's block list (from <see cref="HashBlocks"/>) for the warm-join request, so the host can
        /// build the delta against it:
        /// <code>u8 version (1) | varuint blockSize | varuint count | count × (u64 lo | u64 hi)</code>
        /// </summary>
        public static void WriteBlockList(IReadOnlyList<Digest128> blocks, int blockSize, ByteWriter output)
        {
            if (blocks == null) throw new ArgumentNullException(nameof(blocks));
            if (output == null) throw new ArgumentNullException(nameof(output));
            CheckBlockSize(blockSize);
            output.WriteByte(BlockListVersion);
            output.WriteVarUInt32((uint)blockSize);
            output.WriteVarUInt32((uint)blocks.Count);
            for (int i = 0; i < blocks.Count; i++)
            {
                output.WriteUInt64(blocks[i].Lo);
                output.WriteUInt64(blocks[i].Hi);
            }
        }

        /// <summary>
        /// Reads a block list written by <see cref="WriteBlockList"/> (network input from the joining client). The count
        /// is checked against <paramref name="maxBlocks"/> and the remaining input before anything is allocated, so a
        /// peer can never make the reader allocate more than 16 × <paramref name="maxBlocks"/> bytes; the block size must
        /// lie in [<see cref="MinBlockSize"/>, <see cref="MaxBlockSize"/>]. Throws <see cref="DecodeException"/>.
        /// </summary>
        public static IReadOnlyList<Digest128> ReadBlockList(ByteReader input, int maxBlocks, out int blockSize)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (maxBlocks < 0) throw new ArgumentOutOfRangeException(nameof(maxBlocks), maxBlocks, "the block limit must not be negative");
            byte version = input.ReadByte();
            if (version != BlockListVersion) throw new DecodeException("block list: unknown format version " + version.ToString(CultureInfo.InvariantCulture));
            uint size = input.ReadVarUInt32();
            if (size < MinBlockSize || size > MaxBlockSize) throw new DecodeException("block list: invalid block size " + size);
            uint count = input.ReadVarUInt32();
            if (count > (uint)maxBlocks) throw new DecodeException("block list: " + count + " blocks exceed the limit of " + maxBlocks.ToString(CultureInfo.InvariantCulture));
            if ((long)count * DigestBytes > input.Remaining)
            {
                throw new DecodeException("block list: " + count + " blocks overrun the input (" + input.Remaining.ToString(CultureInfo.InvariantCulture) + " bytes left)");
            }
            var blocks = new Digest128[count];
            for (int i = 0; i < blocks.Length; i++)
            {
                ulong lo = input.ReadUInt64();
                blocks[i] = new Digest128(lo, input.ReadUInt64());
            }
            blockSize = (int)size;
            return Array.AsReadOnly(blocks);
        }

        /// <summary>
        /// The most blocks <see cref="HashBlocks"/> can produce for a file of <paramref name="fileBytes"/> bytes (every
        /// block but the last is longer than a quarter of <paramref name="blockSize"/>): the limit for
        /// <see cref="ReadBlockList"/> given the largest file the receiver may hold.
        /// </summary>
        public static int MaxBlocksFor(long fileBytes, int blockSize)
        {
            if (fileBytes < 0) throw new ArgumentOutOfRangeException(nameof(fileBytes), fileBytes, "the file size must not be negative");
            CheckBlockSize(blockSize);
            if (fileBytes == 0) return 0;
            long blocks = (fileBytes - 1) / (blockSize / 4 + 1) + 1;
            return blocks > int.MaxValue ? int.MaxValue : (int)blocks;
        }

        /// <summary>Block boundaries of the basis; the block count must match the list the delta was built against.</summary>
        private static List<long> BlockOffsets(Stream basis, int blockSize, int expectedCount)
        {
            var offsets = new List<long> { 0 };
            long position = 0;
            var chunker = new ContentDefinedChunker(blockSize);
            chunker.Start(basis);
            try
            {
                while (chunker.Next(out byte[] _, out int _, out int length))
                {
                    position += length;
                    offsets.Add(position);
                    if (offsets.Count - 1 > expectedCount) break;
                }
            }
            finally
            {
                chunker.Finish();
            }
            if (offsets.Count - 1 != expectedCount)
            {
                throw new DecodeException("block delta: the basis does not match the block list of the delta ("
                                          + (offsets.Count - 1 > expectedCount ? "more than " + expectedCount : (offsets.Count - 1).ToString(CultureInfo.InvariantCulture))
                                          + " blocks, expected " + expectedCount + ")");
            }
            return offsets;
        }

        private static void CheckBlockSize(int blockSize)
        {
            if (blockSize < MinBlockSize || blockSize > MaxBlockSize)
            {
                throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize, "the block size must lie in [" + MinBlockSize + ", " + MaxBlockSize + "]");
            }
        }

        private static void CheckOutput(long produced, long length)
        {
            if (produced + length > MaxOutputBytes) throw new DecodeException("block delta: the output exceeds " + MaxOutputBytes + " bytes");
        }

        private static void WriteCopy(ByteWriter writer, Stream delta, int first, int count, ref BlockDeltaStats stats)
        {
            writer.WriteByte(OpCopy);
            writer.WriteVarUInt32((uint)first);
            writer.WriteVarUInt32((uint)count);
            Flush(writer, delta, ref stats);
        }

        private static void Flush(ByteWriter writer, Stream stream, ref BlockDeltaStats stats)
        {
            stream.Write(writer.Buffer, 0, writer.Length);
            stats.DeltaBytes += writer.Length;
            writer.Reset();
        }

        private static void Copy(Stream from, Stream to, long length, XxHash128Stream hasher, byte[] buffer, string what)
        {
            while (length > 0)
            {
                int want = (int)Math.Min(buffer.Length, length);
                int read = from.Read(buffer, 0, want);
                if (read <= 0) throw new DecodeException("block delta: " + what + " ended early (" + length + " bytes missing)");
                to.Write(buffer, 0, read);
                hasher.Append(buffer, 0, read);
                length -= read;
            }
        }

        private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int read = stream.Read(buffer, offset, count);
                if (read <= 0) throw Truncated();
                offset += read;
                count -= read;
            }
        }

        /// <summary>LEB128 with the canonical rules of <c>ByteReader.ReadVarUInt32</c>.</summary>
        private static uint ReadVarUInt32(Stream stream)
        {
            uint result = 0;
            int shift = 0;
            for (int i = 0; i < 5; i++)
            {
                int b = stream.ReadByte();
                if (b < 0) throw Truncated();
                if (i == 4 && b > 0x0F) throw new DecodeException("block delta: varint exceeds 32 bits");
                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    if (b == 0 && i > 0) throw new DecodeException("block delta: overlong varint");
                    return result;
                }
                shift += 7;
            }
            throw new DecodeException("block delta: varint exceeds 32 bits");
        }

        private static ulong ReadUInt64(byte[] b, int p)
        {
            ulong lo = b[p] | ((uint)b[p + 1] << 8) | ((uint)b[p + 2] << 16) | ((uint)b[p + 3] << 24);
            ulong hi = b[p + 4] | ((uint)b[p + 5] << 8) | ((uint)b[p + 6] << 16) | ((uint)b[p + 7] << 24);
            return lo | (hi << 32);
        }

        private static DecodeException Truncated() => new DecodeException("block delta: the delta stream is truncated");
    }
}
