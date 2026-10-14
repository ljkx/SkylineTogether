namespace SkylineTogether.Core
{
    /// <summary>A finished host snapshot: its manifest and the temporary file that holds the blob.</summary>
    public sealed class SnapshotReady
    {
        public SnapshotManifest Manifest;
        /// <summary>Locally generated path in the mod's temp folder (never a path from the network, ARCH §14.5).</summary>
        public string FilePath;

        /// <summary>
        /// True when <see cref="DigestLo"/>/<see cref="DigestHi"/> hold the file's XXH3-128 digest and
        /// <see cref="Size"/> its length (the provider hashed the bytes while writing them, or the host hashed the file
        /// once for its first receiver). Every transfer of this snapshot then starts without reading the file again.
        /// </summary>
        public bool DigestKnown;

        /// <summary>The digest's low and high halves (<c>Hashing.Digest128</c>), valid when <see cref="DigestKnown"/>.</summary>
        public ulong DigestLo, DigestHi;

        /// <summary>The file's length in bytes, valid when <see cref="DigestKnown"/>.</summary>
        public long Size;

        /// <summary>Seconds the provider took to write the file (the game's save), 0 when unknown.</summary>
        public double SaveSeconds;
    }
}
