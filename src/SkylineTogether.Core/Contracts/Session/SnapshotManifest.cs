using System.Collections.Generic;

namespace SkylineTogether.Core
{
    /// <summary>
    /// Describes a join snapshot before its transfer (ARCHITECTURE.md#handshake-and-admission, #snapshot-join): size and xxHash128 digest of the blob,
    /// chunk size, snapshot epoch and frame, the host's build-order counter, the game mode and the content the client
    /// pre-checks, and per-family slot counts for the post-load check.
    /// </summary>
    public sealed class SnapshotManifest
    {
        public uint SnapshotId;
        /// <summary>Blob size in bytes.</summary>
        public long Size;
        /// <summary>The blob's 128-bit digest (<c>Hashing.Digest128</c>: low and high halves).</summary>
        public ulong DigestLo, DigestHi;
        /// <summary>Transfer chunk size (256 KiB).</summary>
        public int ChunkSize;
        /// <summary>Epoch E produced by the snapshot frame.</summary>
        public EpochId Epoch;
        /// <summary>The host frame index F0 at the snapshot.</summary>
        public uint FrameIndex;
        /// <summary>The host's build-order counter; the joiner restores <c>GenerateEdgesSystem</c>'s counter from it (ARCHITECTURE.md#snapshot-join).</summary>
        public uint HostBuildOrderNext;
        public string GameModeName;
        /// <summary>Climate, water-render and terrain-render <c>PrefabID</c>s the client must resolve.</summary>
        public string[] RenderPrefabIds;
        /// <summary>Referenced content checked with <c>ArePrerequisitesMet</c>.</summary>
        public string[] ReferencedContent;
        /// <summary>Bound slots per family (post-load coverage within ±1 %).</summary>
        public Dictionary<Family, int> FamilyCounts = new Dictionary<Family, int>();
        /// <summary>
        /// Mod compatibility: where the city's prefabs that are not base game come from (packages, local assets, runtime
        /// prefabs of mods) and how many of each the city uses; the joiner names what it lacks before it loads. Null or
        /// empty when the city uses base game prefabs only.
        /// </summary>
        public List<Modding.AssetSourceUse> AssetSources;
    }
}
