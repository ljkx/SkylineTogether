using System.Globalization;
using SkylineTogether.Core.Hashing;

namespace SkylineTogether.Core.Transport
{
    /// <summary>
    /// What a blob transfer moves (ARCH §7.5, §9.4): an id, the size, the chunk size, the XXH3-128 digest of the whole
    /// blob and a purpose label (for example <c>"snapshot"</c>, <c>"rebaseline"</c>, <c>"resume"</c>, <c>"tiles"</c>;
    /// an identifier, compared exactly). Written by <see cref="BlobCodec.WriteManifest"/> into a
    /// <see cref="MessageKind.BlobManifest"/> message on lane R; the receiver builds its <see cref="BlobReceiver"/> from
    /// it (or from the snapshot manifest's equivalent fields).
    /// </summary>
    public sealed class BlobManifestInfo
    {
        public uint BlobId;

        /// <summary>Blob size in bytes, 0 ... <see cref="BlobCodec.MaxBlobBytes"/>.</summary>
        public long Size;

        /// <summary>Payload bytes per chunk (the last chunk may be shorter), <see cref="BlobCodec.MinChunkBytes"/> ... 256 KiB.</summary>
        public int ChunkSize;

        /// <summary>XXH3-128 of the whole blob (<see cref="Digest128.Compute(System.IO.Stream)"/>).</summary>
        public Digest128 Digest;

        public string Purpose;

        /// <summary>Number of chunks (0 for an empty blob or an invalid chunk size).</summary>
        public int ChunkCount => ChunkSize > 0 && Size >= 0 ? BlobCodec.ChunkCount(Size, ChunkSize) : 0;

        public BlobManifestInfo Clone()
        {
            return (BlobManifestInfo)MemberwiseClone();
        }

        public override string ToString()
        {
            var ci = CultureInfo.InvariantCulture;
            return "blob " + BlobId.ToString(ci) + " \"" + (Purpose ?? string.Empty) + "\": " + Size.ToString(ci) + " bytes in "
                   + ChunkCount.ToString(ci) + " chunk(s) of " + ChunkSize.ToString(ci) + ", digest " + Digest;
        }
    }
}
