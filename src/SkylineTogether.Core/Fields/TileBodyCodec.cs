using System;
using System.Globalization;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Fields
{
    /// <summary>
    /// The body framing shared by <see cref="HeightTileCodec"/> and <see cref="WaterTileCodec"/>: filtered planes →
    /// zero-run coding → optional block compression (original design, docs/history/ARCHITECTURE-original.md §4.10.9: "zero-run-length, ZSTD"; ZSTD is not used).
    /// <code>
    /// u8 flags (bit 0: compressed; other bits must be 0)
    /// [u8 CompressionKind]                      when compressed
    /// varuint zeroRunLength                     length of the zero-run stream
    /// [varuint compressedLength]                when compressed
    /// zero-run stream, or its compressed form
    /// </code>
    /// The compressed form is used only when it is smaller. A compressor that throws or reports an impossible size is
    /// never fatal: the body is written uncompressed (still a valid encoding) and the failure is logged, the same
    /// degradation as the message envelope's. All scratch memory comes from <see cref="BufferPool"/>.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    internal static class TileBodyCodec
    {
        internal const byte FlagCompressed = 1;

        /// <summary>Zero-run streams below this size are never compressed (nothing to gain).</summary>
        internal const int MinCompressBytes = 64;

        /// <summary>
        /// Writes <c>raw[0 .. rawLength)</c> in the body format; <paramref name="what"/> names the tile kind in the
        /// warning that <paramref name="log"/> (may be null) receives when the compressor fails.
        /// </summary>
        internal static void Write(byte[] raw, int rawLength, ICompressor compressor, ByteWriter output, ILog log, string what)
        {
            byte[] runs = BufferPool.Rent(Math.Max(ZeroRunCodec.MaxEncodedLength(rawLength), 1));
            byte[] packed = null;
            try
            {
                int runLength = ZeroRunCodec.Encode(raw, 0, null, 0, rawLength, runs, 0);
                int packedLength = 0;
                if (compressor != null && compressor.Kind != CompressionKind.None && runLength >= MinCompressBytes)
                {
                    packedLength = TryCompress(compressor, runs, runLength, log, what, out packed);
                }
                if (packedLength > 0 && packedLength < runLength)
                {
                    output.WriteByte(FlagCompressed);
                    output.WriteByte((byte)compressor.Kind);
                    output.WriteVarUInt32((uint)runLength);
                    output.WriteVarUInt32((uint)packedLength);
                    output.WriteBytes(packed, 0, packedLength);
                }
                else
                {
                    output.WriteByte(0);
                    output.WriteVarUInt32((uint)runLength);
                    output.WriteBytes(runs, 0, runLength);
                }
            }
            finally
            {
                BufferPool.Return(runs);
                if (packed != null) BufferPool.Return(packed);
            }
        }

        /// <summary>
        /// Compresses the zero-run stream into a pooled buffer (returned in <paramref name="packed"/>, also on failure, so
        /// the caller returns it); the compressed length, or 0 when the compressor failed or reported an impossible size.
        /// </summary>
        private static int TryCompress(ICompressor compressor, byte[] runs, int runLength, ILog log, string what, out byte[] packed)
        {
            packed = null;
            int bound;
            try
            {
                bound = compressor.MaxCompressedSize(runLength);
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                log.Warn(LogCategory.Fields, compressor.Kind + " compressor rejected a " + Num(runLength) + "-byte " + what + " body; sending it uncompressed", e);
                return 0;
            }
            // a real block compressor needs a few bytes per KiB of slack; anything far beyond is a broken bound that
            // would only make the pool allocate a huge array
            if (bound <= 0 || bound > 2L * runLength + 65536)
            {
                log.Warn(LogCategory.Fields, compressor.Kind + " compressor reported a bound of " + Num(bound) + " bytes for " + Num(runLength) + " input bytes; sending the " + what + " uncompressed");
                return 0;
            }
            packed = BufferPool.Rent(bound);
            int packedLength;
            try
            {
                packedLength = compressor.Compress(runs, 0, runLength, packed, 0);
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                log.Warn(LogCategory.Fields, compressor.Kind + " compression of a " + Num(runLength) + "-byte " + what + " body failed; sending it uncompressed", e);
                return 0;
            }
            if (packedLength < 0 || packedLength > bound)
            {
                log.Warn(LogCategory.Fields, compressor.Kind + " compressor returned " + Num(packedLength) + " bytes (bound " + Num(bound) + "); sending the " + what + " uncompressed");
                return 0;
            }
            return packedLength;
        }

        /// <summary>
        /// Reads a body and decodes exactly <paramref name="rawLength"/> bytes into <paramref name="raw"/>. Every
        /// malformed input, including a failure of the compressor, becomes a <see cref="DecodeException"/>.
        /// </summary>
        internal static void Read(ByteReader input, int rawLength, ICompressor compressor, byte[] raw, string what)
        {
            byte flags = input.ReadByte();
            if ((flags & ~FlagCompressed) != 0)
            {
                throw new DecodeException(what + ": unknown body flags 0x" + flags.ToString("x2", CultureInfo.InvariantCulture));
            }
            bool compressed = (flags & FlagCompressed) != 0;
            CompressionKind kind = CompressionKind.None;
            if (compressed)
            {
                byte k = input.ReadByte();
                if (k == (byte)CompressionKind.None || k > (byte)CompressionKind.Lz4)
                {
                    throw new DecodeException(what + ": invalid compression kind " + k.ToString(CultureInfo.InvariantCulture));
                }
                kind = (CompressionKind)k;
            }
            uint runLength = input.ReadVarUInt32();
            int maxRunLength = ZeroRunCodec.MaxEncodedLength(rawLength);
            if (runLength > (uint)maxRunLength)
            {
                throw new DecodeException(what + ": body of " + runLength + " bytes exceeds the limit of " + maxRunLength + " for " + rawLength + " raw bytes");
            }
            if (!compressed)
            {
                ArraySegment<byte> runs = input.ReadSegment((int)runLength);
                int consumed = ZeroRunCodec.Decode(runs.Array, runs.Offset, runs.Offset + runs.Count, raw, 0, rawLength, ZeroRunCodec.Mode.Overwrite);
                if (consumed != runs.Count) throw new DecodeException(what + ": " + (runs.Count - consumed) + " trailing body bytes");
                return;
            }
            if (compressor == null) throw new DecodeException(what + ": the body is compressed with " + kind + " but no compressor is available");
            if (!SameFormat(compressor.Kind, kind)) throw new DecodeException(what + ": the body is compressed with " + kind + ", the local compressor is " + compressor.Kind);
            uint packedLength = input.ReadVarUInt32();
            if (packedLength == 0 || packedLength > (uint)input.Remaining)
            {
                throw new DecodeException(what + ": compressed length " + packedLength + " is invalid (" + input.Remaining + " bytes left)");
            }
            ArraySegment<byte> packed = input.ReadSegment((int)packedLength);
            byte[] runBuffer = BufferPool.Rent(Math.Max((int)runLength, 1));
            try
            {
                int produced;
                try
                {
                    produced = compressor.Decompress(packed.Array, packed.Offset, packed.Count, runBuffer, 0, (int)runLength);
                }
                catch (DecodeException)
                {
                    throw;
                }
                catch (Exception e) when (!(e is OutOfMemoryException))
                {
                    // the ICompressor contract allows only DecodeException; a native failure must not escape as anything else
                    throw new DecodeException(what + ": decompression failed (" + e.GetType().Name + ": " + e.Message + ")", e);
                }
                if (produced != (int)runLength)
                {
                    throw new DecodeException(what + ": the decompressor produced " + Num(produced) + " bytes, expected " + Num((int)runLength));
                }
                int consumed = ZeroRunCodec.Decode(runBuffer, 0, (int)runLength, raw, 0, rawLength, ZeroRunCodec.Mode.Overwrite);
                if (consumed != (int)runLength) throw new DecodeException(what + ": " + ((int)runLength - consumed) + " trailing body bytes");
            }
            finally
            {
                BufferPool.Return(runBuffer);
            }
        }

        private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

        /// <summary>Native LZ4 and Core's managed LZ4 write the same LZ4 block format (the message envelope relies on it too).</summary>
        private static bool SameFormat(CompressionKind local, CompressionKind body) => local == body || (IsLz4(local) && IsLz4(body));

        private static bool IsLz4(CompressionKind kind) => kind == CompressionKind.Lz4 || kind == CompressionKind.ManagedLz4;
    }
}
