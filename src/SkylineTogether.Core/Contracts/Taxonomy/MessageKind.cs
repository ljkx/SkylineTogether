namespace SkylineTogether.Core
{
    /// <summary>
    /// Envelope message kinds (ARCH §4.6.1, Appendix C). The values are the first byte of every message and must
    /// never change; 0 is not a valid kind.
    /// </summary>
    public enum MessageKind : byte
    {
        Hello = 1,
        Welcome,
        Refuse,
        Control,
        Epoch,
        Ack,
        Proposal,
        Motion,
        MotionControl,
        Clock,
        View,
        Presence,
        HashDetail,
        RepairRequest,
        BlobManifest,
        BlobChunk,
        BlobHave,
        Ping,
        Chat,

        // feat/terrain (merge note: renumber here if another branch also appended kinds after Chat)
        /// <summary>Terrain sync (ARCH §4.10.9): brush stamps, heightmap tiles, tile-hash manifests and tile requests
        /// (<see cref="Sync.TerrainMessage"/>), host ↔ client.</summary>
        Terrain = 20,
        /// <summary>Water correction tiles (ARCH §4.10.10, <see cref="Sync.WaterMessage"/>), host → client.</summary>
        Water = 21,

        // w2/ids (merge coordination: kinds 60-64 are reserved for this branch)
        /// <summary>Client → host: how many of the host's <c>Epoch</c> messages the client has applied
        /// (<see cref="Sync.EpochAckMessage"/>); gates the reuse of released net ids (ARCH §3.1).</summary>
        EpochAck = 60
    }
}
