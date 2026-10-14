namespace SkylineTogether.Core
{
    /// <summary>
    /// Envelope message kinds (ARCHITECTURE.md#envelope-and-message-kinds). The values are the first byte of every message and must
    /// never change; 0 is not a valid kind.
    /// <para>
    /// Reserved values (ARCHITECTURE.md#reserved-wire-values): <see cref="Clock"/>, <see cref="HashDetail"/>,
    /// <see cref="RepairRequest"/> and <see cref="BlobManifest"/> belong to the original design and have no sender. Their
    /// numbers stay taken; receivers drop them (<see cref="Protocol.Limits.IsReservedKind"/>) like unknown kinds, before
    /// any listener sees them.
    /// </para>
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
        /// <summary>Reserved (10): never sent; dropped on receipt. The host's frame travels as the batch's <c>Clock</c> record.</summary>
        Clock,
        View,
        Presence,
        /// <summary>Reserved (13): never sent; dropped on receipt (integrity details travel as batch records).</summary>
        HashDetail,
        /// <summary>Reserved (14): never sent; dropped on receipt (repairs are requested with batch records).</summary>
        RepairRequest,
        /// <summary>Reserved (15): never sent; dropped on receipt (the snapshot's manifest is <c>Control SnapshotManifest</c>).</summary>
        BlobManifest,
        BlobChunk,
        BlobHave,
        Ping,
        Chat,

        /// <summary>Terrain sync (ARCHITECTURE.md#zone-cells-cell-maps-terrain-and-water): brush stamps, heightmap tiles, tile-hash manifests and tile requests
        /// (<see cref="Sync.TerrainMessage"/>), host ↔ client.</summary>
        Terrain = 20,
        /// <summary>Water correction tiles (ARCHITECTURE.md#zone-cells-cell-maps-terrain-and-water, <see cref="Sync.WaterMessage"/>), host → client.</summary>
        Water = 21,

        // 60-64: network id reuse
        /// <summary>Client → host: how many of the host's <c>Epoch</c> messages the client has applied
        /// (<see cref="Sync.ApplyAckMessage"/>); gates the reuse of released net ids (ARCHITECTURE.md#net-ids).</summary>
        ApplyAck = 60,

        // 65-69: simulation values, cell maps, statistics
        /// <summary>Attention-driven simulation values and time-aligned inspection samples (ARCHITECTURE.md#simulation-values, #deferred-values-and-link-pacing;
        /// <see cref="Sync.SimValueMessage"/>), host → client.</summary>
        SimValues = 65,
        /// <summary>Cell-map tiles host → client and resource-paint patches client → host (ARCHITECTURE.md#zone-cells-cell-maps-terrain-and-water;
        /// <see cref="Fields.CellMapMessage"/>).</summary>
        CellMaps = 66,
        /// <summary>City statistics history (<see cref="Sync.StatisticsMessage"/>), host → client.</summary>
        Statistics = 67,

        // 90-94: agents
        /// <summary>Values of citizens, households, pets and companies, streamed by attention, plus the reverse buffers of
        /// their owners after a membership change (<see cref="Sync.SimValueMessage"/> format), host → client.</summary>
        AgentValues = 90,
        /// <summary>Client → host: agents whose values referenced something not yet known here; the host re-sends them
        /// (<see cref="Sync.AgentRefreshMessage"/>).</summary>
        AgentRefresh = 91,

        // 100-104: utility flow status
        /// <summary>Flow status of the electricity and water graphs by flow key, to peers with the Electricity or Water
        /// infoview topic active (ARCHITECTURE.md#statistics-icons-and-flow-status; <see cref="Sync.FlowStatusMessage"/>), host → client.</summary>
        FlowStatus = 100,

        // 110-114: mod integration
        /// <summary>Opaque bytes of another mod on its own channel (<see cref="Protocol.ModChannelMessage"/>; docs/MOD-INTEGRATION.md),
        /// client → host and host → client; the host relays between clients.</summary>
        ModChannel = 110
    }
}
