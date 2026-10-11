namespace SkylineTogether.Core
{
    /// <summary>Message body compressors (ARCH §4.6.5); the codec is negotiated in the handshake.</summary>
    public enum CompressionKind : byte
    {
        None,
        /// <summary>Core's pure C# LZ4 block format (tests and last-resort fallback).</summary>
        ManagedLz4,
        /// <summary>ZSTD level 1 through the game's native compression (baseline).</summary>
        Zstd,
        /// <summary>Native LZ4, only if a startup probe proves it works.</summary>
        Lz4
    }
}
