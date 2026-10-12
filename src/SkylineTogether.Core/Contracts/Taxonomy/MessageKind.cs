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

        // w2/simvals (merge coordination: this branch owns 65-69)
        /// <summary>Attention-driven simulation values and time-aligned inspection samples (ARCH D5, §4.7.9, §4.8;
        /// <see cref="Sync.SimValueMessage"/>), host → client.</summary>
        SimValues = 65,
        /// <summary>Cell-map tiles host → client and resource-paint patches client → host (ARCH §4.10.8;
        /// <see cref="Fields.CellMapMessage"/>).</summary>
        CellMaps = 66,
        /// <summary>City statistics history (<see cref="Sync.StatisticsMessage"/>), host → client.</summary>
        Statistics = 67
    }
}
