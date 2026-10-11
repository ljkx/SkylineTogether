namespace SkylineTogether.Core
{
    /// <summary>A finished host snapshot: its manifest and the temporary file that holds the blob.</summary>
    public sealed class SnapshotReady
    {
        public SnapshotManifest Manifest;
        /// <summary>Locally generated path in the mod's temp folder (never a path from the network, ARCH §14.5).</summary>
        public string FilePath;
    }
}
