namespace SkylineTogether.Core
{
    /// <summary>
    /// Block compressor seam (ARCH §4.6.5): core-protocol's <c>ManagedLz4Compressor</c> and game-bootstrap's
    /// <c>NativeCompressor</c> (ZSTD, LZ4). Implementations are thread-safe (stateless or internally synchronised).
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public interface ICompressor
    {
        CompressionKind Kind { get; }

        /// <summary>Worst-case compressed size for <paramref name="length"/> input bytes (size of the destination buffer).</summary>
        int MaxCompressedSize(int length);

        /// <summary>Compresses <paramref name="srcCount"/> bytes into <paramref name="dst"/> at <paramref name="dstOffset"/>; returns the compressed length.</summary>
        int Compress(byte[] src, int srcOffset, int srcCount, byte[] dst, int dstOffset);

        /// <summary>
        /// Decompresses into <paramref name="dst"/>; the output must be exactly <paramref name="expectedLength"/> bytes.
        /// Returns <paramref name="expectedLength"/>. Throws <see cref="DecodeException"/> on corrupt input, on a length
        /// mismatch, or when <paramref name="dst"/> is too small; never reads or writes outside the given ranges.
        /// </summary>
        int Decompress(byte[] src, int srcOffset, int srcCount, byte[] dst, int dstOffset, int expectedLength);
    }
}
