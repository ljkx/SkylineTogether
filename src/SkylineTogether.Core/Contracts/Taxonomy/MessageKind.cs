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
        /// (<see cref="Sync.ApplyAckMessage"/>); gates the reuse of released net ids (ARCH §3.1).</summary>
        ApplyAck = 60,

        // w2/simvals (merge coordination: this branch owns 65-69)
        /// <summary>Attention-driven simulation values and time-aligned inspection samples (ARCH D5, §4.7.9, §4.8;
        /// <see cref="Sync.SimValueMessage"/>), host → client.</summary>
        SimValues = 65,
        /// <summary>Cell-map tiles host → client and resource-paint patches client → host (ARCH §4.10.8;
        /// <see cref="Fields.CellMapMessage"/>).</summary>
        CellMaps = 66,
        /// <summary>City statistics history (<see cref="Sync.StatisticsMessage"/>), host → client.</summary>
        Statistics = 67,

        // w3/agents (merge coordination: this branch owns 90-94)
        /// <summary>Values of citizens, households, pets and companies, streamed by attention, plus the reverse buffers of
        /// their owners after a membership change (<see cref="Sync.SimValueMessage"/> format), host → client.</summary>
        AgentValues = 90,
        /// <summary>Client → host: agents whose values referenced something not yet known here; the host re-sends them
        /// (<see cref="Sync.AgentRefreshMessage"/>).</summary>
        AgentRefresh = 91,

        // w4/flow (merge coordination: this branch owns 100-104)
        /// <summary>Flow status of the electricity and water graphs by flow key, to peers with the Electricity or Water
        /// infoview topic active (ARCH §4.11.5; <see cref="Sync.FlowStatusMessage"/>), host → client.</summary>
        FlowStatus = 100,

        // w8/mods (merge coordination: this branch owns 110-114)
        /// <summary>Opaque bytes of another mod on its own channel (<see cref="Protocol.ModChannelMessage"/>; docs/MOD-INTEGRATION.md),
        /// client → host and host → client; the host relays between clients.</summary>
        ModChannel = 110
    }
}
