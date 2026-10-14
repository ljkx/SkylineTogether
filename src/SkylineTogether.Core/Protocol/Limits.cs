namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// Size limits of the wire format (ARCHITECTURE.md#batch-encoding, #fragmentation-and-compression, #security-model). Encoders refuse to produce
    /// anything above a limit (<see cref="System.ArgumentException"/>: the caller must split), decoders reject it with
    /// <see cref="DecodeException"/> before allocating or decompressing. A well-formed message that exceeds a semantic
    /// limit of the replica (an epoch above <see cref="MaxEpochTotalBytes"/>) is a resync condition, not a protocol
    /// error (ARCHITECTURE.md#feature-health-and-guards).
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class Limits
    {
        /// <summary>Largest message Steam Networking Sockets sends (<c>k_cbMaxSteamNetworkingSocketsMessageSizeSend</c>,
        /// <c>Constants.cs:253</c>, R06 §4.2).</summary>
        public const int MaxMessageBytes = 512 * 1024;

        /// <summary>Encoded messages above this size are fragmented by <see cref="Fragmenter"/> (ARCHITECTURE.md#fragmentation-and-compression); every
        /// fragment stays at or below it.</summary>
        public const int FragmentThreshold = 480 * 1024;

        /// <summary>Largest uncompressed message body: one message, one epoch part (ARCHITECTURE.md#fragmentation-and-compression).</summary>
        public const int MaxUncompressedBytes = 8 * 1024 * 1024;

        /// <summary>All parts of one epoch together (ARCHITECTURE.md#batch-encoding); above it the client resyncs.</summary>
        public const long MaxEpochTotalBytes = 256L * 1024 * 1024;

        /// <summary>Payload of one blob chunk on the bulk lane (ARCHITECTURE.md#blob-transfer).</summary>
        public const int MaxBulkChunkBytes = 256 * 1024;

        /// <summary>Motion datagrams on the unreliable lane (ARCHITECTURE.md#fragmentation-and-compression).</summary>
        public const int MaxDatagramBytes = 1200;

        /// <summary>Characters (UTF-16 code units) of one chat line (ARCHITECTURE.md#security-model).</summary>
        public const int MaxChatChars = 500;

        /// <summary>Characters (UTF-16 code units) of a display name or an entity name (ARCHITECTURE.md#security-model).</summary>
        public const int MaxNameChars = 64;

        /// <summary>One proposal part (ARCHITECTURE.md#host-validation-and-commit).</summary>
        public const int MaxProposalBytes = 4 * 1024 * 1024;

        /// <summary>All parts of one proposal.</summary>
        public const long MaxProposalTotalBytes = 64L * 1024 * 1024;

        /// <summary>Entities of one proposal.</summary>
        public const int MaxProposalEntities = 200_000;

        /// <summary>Slots of one repair request (ARCHITECTURE.md#security-model).</summary>
        public const int MaxRepairSlots = 512;

        /// <summary>Message bodies of this size or more are compressed (ARCHITECTURE.md#fragmentation-and-compression).</summary>
        public const int CompressThreshold = 256;

        /// <summary>Partially received fragmented messages, and partially received multi-part epochs, per peer (ARCHITECTURE.md#security-model).</summary>
        public const int MaxPartialMessagesPerPeer = 4;

        // ---- limits derived from the formats (not part of the plan's table) ----

        /// <summary>Parts of one epoch (the header's <c>partCount</c>); with 8 MiB parts 32 suffice for the largest epoch.</summary>
        public const int MaxEpochParts = 1024;

        /// <summary>Sections of one epoch message (the header's <c>u8 sectionCount</c>), also of a reassembled multi-part epoch.</summary>
        public const int MaxSectionsPerEpoch = 255;

        /// <summary>Fragments of one message; 18 cover the largest message at <see cref="FragmentThreshold"/>.</summary>
        public const int MaxFragmentsPerMessage = 64;

        /// <summary>Bytes an envelope adds to a body at most (kind, flags, size varint, codec byte), rounded up.</summary>
        public const int EnvelopeOverheadBytes = 16;

        /// <summary>Components in one SPAWN or STRUCT record, and types in one type set: ordinals are below 64 (ARCHITECTURE.md#batch-encoding).</summary>
        public const int MaxComponentsPerRecord = 64;

        /// <summary>Slots in one <see cref="HashDetailMessage"/>: one bucket holds 1024 slots (ARCHITECTURE.md#bucket-digests).</summary>
        public const int MaxHashDetailSlots = 1 << NetSlot.BucketShift;

        /// <summary>Largest valid hash bucket index (<c>NetSlot.MaxValue &gt;&gt; 10</c>).</summary>
        public const int MaxBucket = (int)(NetSlot.MaxValue >> NetSlot.BucketShift);

        /// <summary>Bytes of the blob chunk header allowed on top of <see cref="MaxBulkChunkBytes"/>.</summary>
        public const int BlobChunkHeaderSlack = 64;

        /// <summary>
        /// The largest uncompressed body a message of <paramref name="kind"/> may have. <see cref="EnvelopeCodec"/>
        /// checks the declared size against it before decompressing (decompression-bomb guard, ARCHITECTURE.md#security-model), and
        /// <see cref="Reassembler"/> before buffering fragments. Unknown kinds get a small limit; they are dropped by
        /// the router anyway.
        /// </summary>
        public static int MaxBodyBytes(MessageKind kind)
        {
            switch (kind)
            {
                case MessageKind.Hello: return 64 * 1024;
                case MessageKind.Welcome: return 4 * 1024 * 1024;
                case MessageKind.Refuse: return 4 * 1024 * 1024;
                case MessageKind.Control: return MaxUncompressedBytes;
                case MessageKind.Epoch: return MaxUncompressedBytes;
                case MessageKind.Ack: return 256;
                case MessageKind.Proposal: return MaxProposalBytes + 1024;
                case MessageKind.Motion: return 16 * 1024;
                case MessageKind.MotionControl: return MaxUncompressedBytes;
                case MessageKind.Clock: return 256;
                case MessageKind.View: return 256;
                case MessageKind.Presence: return 16 * 1024;
                case MessageKind.HashDetail: return 64 * 1024;
                case MessageKind.RepairRequest: return 16 * 1024;
                case MessageKind.BlobManifest: return 64 * 1024;
                case MessageKind.BlobChunk: return MaxBulkChunkBytes + BlobChunkHeaderSlack;
                case MessageKind.BlobHave: return 1024 * 1024;
                case MessageKind.Ping: return 256;
                case MessageKind.Chat: return 8 * 1024;
                case MessageKind.Terrain: return MaxUncompressedBytes;
                case MessageKind.Water: return MaxUncompressedBytes;
                case MessageKind.ApplyAck: return 64;
                case MessageKind.SimValues: return MaxUncompressedBytes;
                case MessageKind.CellMaps: return MaxUncompressedBytes;
                case MessageKind.Statistics: return MaxUncompressedBytes;
                case MessageKind.AgentValues: return MaxUncompressedBytes;
                case MessageKind.AgentRefresh: return 16 * 1024;
                case MessageKind.FlowStatus: return 256 * 1024;
                case MessageKind.ModChannel: return ModChannelMessage.MaxEncodedBytes;
                default: return 64 * 1024;
            }
        }

        /// <summary>True for the kinds of <see cref="MessageKind"/> (1-21, 60, 65-67, 90-91, 100, 110); the router drops every other kind (ARCHITECTURE.md#security-model).</summary>
        public static bool IsKnownKind(MessageKind kind) => (kind >= MessageKind.Hello && kind <= MessageKind.Water) || kind == MessageKind.ApplyAck || (kind >= MessageKind.SimValues && kind <= MessageKind.Statistics)
                                                            || kind == MessageKind.AgentValues || kind == MessageKind.AgentRefresh
                                                            || kind == MessageKind.FlowStatus || kind == MessageKind.ModChannel;
    }
}
