using SkylineTogether.Core.Protocol;

namespace SkylineTogether.Core.Intents
{
    /// <summary>
    /// The wire format of a proposal (the body of a <see cref="MessageKind.Proposal"/> message on the reliable lane,
    /// ARCH §5.3, Appendix C) and its limits (ARCH §5.4.1 rule 1, §5.6, §14.3). A proposal travels as one part or, when
    /// it is larger than <see cref="Limits.MaxProposalBytes"/>, as several parts split at item boundaries, like epoch
    /// parts (ARCH §4.6.2); the host assembles them with <c>ProposalAssembler</c> and validates only the complete
    /// proposal.
    /// <code>
    /// part       = u8 version (1) | u8 flags {bit 0: more parts follow} | varint seq (≥ 1) | varint partIndex
    ///              | [partIndex = 0: header] | item*                      items run to the end of the message body
    /// header     = u8 kind (ProposalKind) | varint baseEpoch | u8 hasTool (0/1)
    ///              | [varint toolKind (≤ 65535) | nstring toolTypeName (≤ 256 bytes) | varint prefab+1 | u8 mode
    ///                 | zig-zag varint64 costSum]
    /// item       = u8 tag | body          tags never decrease over the whole proposal (all creates, then updates, ...)
    ///   1 CREATE   varint localId (≥ 1) | u8 family | varint prefab+1 | varint typeSetId | varint64 enabledBits | components
    ///   2 UPDATE   varint slot (≥ 1) | varint baseRevision (≤ 65535) | components
    ///   3 DELETE   varint slot (≥ 1) | varint baseRevision
    ///   4 TOUCH    varint slot (≥ 1) | u8 TouchFlags
    ///   5 ZONE     derived key (NetRefCodec.WriteKey) | varint baseRevision | cells (ZoneOverlayCodec, ≤ 4096 cells)
    ///   6 STAMP    TerrainStamp body
    ///   7 INTENT   varint kind (IntentKind) | varint target slot (0 = none) | varint baseRevision | varint prefab+1
    ///              | zig-zag varint intValue | f32 floatValue | u8 bool | nstring text (≤ 64 characters) | nints | nbytes
    /// components = varint count (≤ 64) | count × (varint typeRank | u8 flags {bit 0 enabled bit present, bit 1 enabled,
    ///              bit 2 removed} | [not removed: varint length | canonical bytes])
    /// nstring    = varint (utf8Length + 1), 0 = null | utf8      nints = varint (count + 1), 0 = null | zig-zag varints
    /// nbytes     = varint (length + 1), 0 = null | bytes         prefab+1 = 0 for PrefabRank.None
    /// </code>
    /// Floats are raw little-endian IEEE-754 bits (non-finite values are a validation failure, not a decode failure).
    /// The encoding is canonical: every proposal has exactly one byte sequence per split, and the decoders reject every
    /// other one (strict varints, unknown tags, flags and enum values, decreasing tags, values out of range) with
    /// <see cref="DecodeException"/>. Text is the exception: decoders strip control characters (ARCH §14.3).
    /// <para>
    /// Limits: a malformed part, a part above <see cref="Limits.MaxProposalBytes"/>, and violations of the per-item caps
    /// below that the format defines (components per item, cells per zone edit, text, arrays) are decode errors (the host
    /// disconnects the peer, ARCH §14.3). A well-formed proposal that exceeds a per-proposal cap
    /// (<see cref="Limits.MaxProposalTotalBytes"/>, <see cref="Limits.MaxProposalEntities"/>, <see cref="MaxTouches"/>,
    /// <see cref="MaxStamps"/>, <see cref="MaxIntents"/>, <see cref="MaxComponentsTotal"/>, <see cref="MaxZoneCellsTotal"/>)
    /// is decoded as an empty shell with <see cref="Proposal.LimitViolation"/> set: the host rejects it (RESULT
    /// <see cref="RejectReason.Invalid"/>) and counts the violation (repeated violations kick, ARCH §5.6).
    /// </para>
    /// </summary>
    public static class ProposalFormat
    {
        /// <summary>The format version byte of every part.</summary>
        public const byte Version = 1;

        /// <summary>Parts of one proposal (bytes and items bound it long before: 16 full parts carry 64 MB).</summary>
        public const int MaxPartsPerProposal = 1024;

        /// <summary>Touch records of one proposal (each addresses an entity, like the entity cap).</summary>
        public const int MaxTouches = Limits.MaxProposalEntities;

        /// <summary>Terrain stamps of one proposal (a stroke proposal carries the stamps of one apply frame).</summary>
        public const int MaxStamps = 4096;

        /// <summary>Semantic intents of one proposal (stage A batches the UI actions of one frame).</summary>
        public const int MaxIntents = 1024;

        /// <summary>Components of one create or update (type-set ordinals are below 64, ARCH §3.6).</summary>
        public const int MaxComponentsPerItem = Limits.MaxComponentsPerRecord;

        /// <summary>Components of all creates and updates of one proposal (200k entities with ~20 valued types each).</summary>
        public const int MaxComponentsTotal = 4 * 1024 * 1024;

        /// <summary>Cells of one zone edit (vanilla blocks have at most 10 × 6 cells; room for mods).</summary>
        public const int MaxZoneCellsPerEdit = 4096;

        /// <summary>Cells of all zone edits of one proposal (8 MB decoded).</summary>
        public const int MaxZoneCellsTotal = 4 * 1024 * 1024;

        /// <summary>UTF-8 bytes of <see cref="ToolHeader.ToolTypeName"/> (an identifier, never truncated).</summary>
        public const int MaxToolTypeNameBytes = 256;

        /// <summary>Characters (UTF-16 code units) of <see cref="SemanticIntent.Text"/>: names (ARCH §14.3).</summary>
        public const int MaxTextChars = Limits.MaxNameChars;

        /// <summary>Elements of <see cref="SemanticIntent.IntArray"/>.</summary>
        public const int MaxIntentArrayLength = 4096;

        /// <summary>Bytes of <see cref="SemanticIntent.Blob"/> (whole buffers of mesh colours, vehicle models, palettes).</summary>
        public const int MaxIntentBlobBytes = 64 * 1024;
    }
}
