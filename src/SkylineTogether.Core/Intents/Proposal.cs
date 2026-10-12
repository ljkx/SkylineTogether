using System;
using System.Collections.Generic;
using System.Globalization;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;

namespace SkylineTogether.Core.Intents
{
    /// <summary>
    /// A client proposal (ARCH §5.1-5.3): the effects of one tool apply, local edit, terrain stroke or batch of UI actions,
    /// sent to the host, which validates it atomically (<c>ProposalValidator</c>) and commits or rejects it as a
    /// whole. <see cref="Seq"/> numbers the proposals of one client from 1 (<c>ProposalTracker.NextSeq</c>); on the
    /// wire it is always qualified by the origin peer (RESULT records, ARCH §4.6.3). <see cref="BaseEpoch"/> is the last
    /// epoch the client had applied when it captured the effects (diagnostics; conflicts are decided by revisions).
    /// <para>
    /// Codec (<see cref="ProposalFormat"/>): <c>Write</c> and <c>Read</c> handle a proposal that fits into one
    /// part; <c>WriteParts</c> splits any proposal into parts of at most <see cref="Limits.MaxProposalBytes"/>,
    /// which the host joins with <c>ProposalAssembler</c>. A single-part proposal is byte-identical in both forms.
    /// Lists may be null when written (treated as empty); decoders always return non-null lists.
    /// </para>
    /// </summary>
    public sealed class Proposal
    {
        public uint Seq;

        public ProposalKind Kind;

        public EpochId BaseEpoch;

        /// <summary>The producing tool (change sets, local edits of tools, terrain strokes); null for UI actions.</summary>
        public ToolHeader Tool;

        public List<ProposalCreate> Creates = new List<ProposalCreate>();

        public List<ProposalUpdate> Updates = new List<ProposalUpdate>();

        public List<ProposalDelete> Deletes = new List<ProposalDelete>();

        public List<TouchRecord> Touches = new List<TouchRecord>();

        public List<ZoneEdit> Zones = new List<ZoneEdit>();

        public List<TerrainStamp> Stamps = new List<TerrainStamp>();

        public List<SemanticIntent> Intents = new List<SemanticIntent>();

        /// <summary>
        /// Entities the proposal addresses: creates + updates + deletes + zone edits (the measure of
        /// <see cref="Limits.MaxProposalEntities"/>, ARCH §5.4.1 rule 1).
        /// </summary>
        public int EntityCount => Count(Creates) + Count(Updates) + Count(Deletes) + Count(Zones);

        /// <summary>
        /// Set by the decoders when a well-formed proposal exceeded a per-proposal cap (<see cref="ProposalFormat"/>): the
        /// proposal is then an empty shell (header only) and <c>ProposalValidator</c> rejects it with
        /// <see cref="RejectReason.Invalid"/>. Null for every proposal within the limits and for proposals built in memory.
        /// </summary>
        public string LimitViolation { get; internal set; }

        /// <summary>
        /// True when the proposal fits the per-proposal caps the host enforces (entities, touches, stamps, intents,
        /// components, zone cells); otherwise <paramref name="violation"/> names the first cap exceeded. The byte total
        /// (<see cref="Limits.MaxProposalTotalBytes"/>) is checked by <c>WriteParts</c>, which measures it.
        /// A sender checks this before tracking a proposal, so an oversized action is rolled back locally.
        /// </summary>
        public bool IsWithinLimits(out string violation)
        {
            violation = ProposalCodec.CheckProposalCaps(this, ProposalLimits.Default);
            return violation == null;
        }

        /// <summary>
        /// Appends the proposal as one part (a complete message body). Throws <see cref="ArgumentException"/> when a value
        /// cannot be encoded, a per-proposal cap is exceeded, or the encoding exceeds <see cref="Limits.MaxProposalBytes"/>
        /// (use <c>WriteParts</c>); the writer is then left as it was.
        /// </summary>
        public void Write(ByteWriter w) => Write(w, ProposalLimits.Default);

        internal void Write(ByteWriter w, ProposalLimits limits)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            ProposalCodec.CheckEncodable(this, limits);
            int start = w.Length;
            try
            {
                ProposalCodec.WritePartHeader(w, this, 0);
                for (byte tag = ProposalCodec.TagCreate; tag <= ProposalCodec.TagIntent; tag++)
                {
                    int count = ProposalCodec.ItemCount(this, tag);
                    for (int i = 0; i < count; i++)
                    {
                        ProposalCodec.WriteItem(w, this, tag, i);
                        if (w.Length - start > limits.MaxPartBytes)
                        {
                            throw new ArgumentException("proposal " + ProposalCodec.Format(Seq) + " needs more than one part of "
                                                        + ProposalCodec.Format(limits.MaxPartBytes) + " bytes; use WriteParts");
                        }
                    }
                }
            }
            catch
            {
                w.Truncate(start);
                throw;
            }
        }

        /// <summary>
        /// Reads one complete single-part proposal: everything from the reader's position to the end of its window (the
        /// message body). Throws <see cref="DecodeException"/> for malformed input, a body above
        /// <see cref="Limits.MaxProposalBytes"/>, or a part of a multi-part proposal (those go through
        /// <c>ProposalAssembler</c>). A well-formed proposal above a per-proposal cap is returned as a shell with
        /// <see cref="LimitViolation"/> set.
        /// </summary>
        public static Proposal Read(ByteReader r) => Read(r, ProposalLimits.Default);

        /// <summary>Reads a whole message body with <c>Read</c>.</summary>
        public static Proposal Decode(byte[] buffer, int offset, int count) => Read(new ByteReader(buffer, offset, count), ProposalLimits.Default);

        internal static Proposal Read(ByteReader r, ProposalLimits limits)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            int size = r.Remaining;
            if (size > limits.MaxPartBytes)
            {
                throw new DecodeException("proposal part of " + ProposalCodec.Format(size) + " bytes exceeds " + ProposalCodec.Format(limits.MaxPartBytes));
            }
            ProposalCodec.ReadPartHeader(r, out byte flags, out uint seq, out int partIndex);
            if (partIndex != 0 || (flags & ProposalCodec.FlagMoreParts) != 0)
            {
                throw new DecodeException("proposal " + ProposalCodec.Format(seq) + ": part " + ProposalCodec.Format(partIndex)
                                          + " of a multi-part proposal (use ProposalAssembler)");
            }
            var state = new ProposalDecodeState(limits, new Proposal { Seq = seq });
            ProposalCodec.ReadProposalHeader(r, state.Target);
            state.Bytes = size;
            ProposalCodec.ReadItems(r, state);
            return state.Violation == null ? state.Target : CreateShell(state.Target, state.Violation);
        }

        /// <summary>
        /// Encodes the proposal into <paramref name="scratch"/> (reset first) as parts of at most
        /// <see cref="Limits.MaxProposalBytes"/>, split at item boundaries, and hands each part to
        /// <paramref name="emitPart"/> in order (the array is valid only during the call: the outbox copies it). A proposal
        /// that fits into one part is emitted as exactly the bytes of <c>Write</c>. Nothing is emitted when the
        /// proposal cannot be encoded: <see cref="ArgumentException"/> for unencodable values, an exceeded per-proposal cap
        /// (<see cref="IsWithinLimits"/>), a total above <see cref="Limits.MaxProposalTotalBytes"/>, or a single item larger
        /// than one part.
        /// </summary>
        public void WriteParts(ByteWriter scratch, Action<byte[], int, int> emitPart) => WriteParts(scratch, emitPart, ProposalLimits.Default);

        internal void WriteParts(ByteWriter scratch, Action<byte[], int, int> emitPart, ProposalLimits limits)
        {
            if (scratch == null) throw new ArgumentNullException(nameof(scratch));
            if (emitPart == null) throw new ArgumentNullException(nameof(emitPart));
            ProposalCodec.CheckEncodable(this, limits);
            scratch.Reset();
            var starts = new List<int>();
            var lengths = new List<int>();
            int partIndex = 0;
            int partStart = ProposalCodec.WritePartHeader(scratch, this, 0);
            int bodyStart = scratch.Length;
            for (byte tag = ProposalCodec.TagCreate; tag <= ProposalCodec.TagIntent; tag++)
            {
                int count = ProposalCodec.ItemCount(this, tag);
                for (int i = 0; i < count; i++)
                {
                    int itemStart = scratch.Length;
                    ProposalCodec.WriteItem(scratch, this, tag, i);
                    if (scratch.Length - partStart > limits.MaxPartBytes)
                    {
                        if (itemStart == bodyStart) throw ItemTooLarge(tag, i, scratch.Length - itemStart, limits);
                        // close the current part before this item and repeat the item at the start of the next part
                        scratch.Truncate(itemStart);
                        starts.Add(partStart);
                        lengths.Add(itemStart - partStart);
                        scratch.Buffer[partStart + 1] = ProposalCodec.FlagMoreParts;
                        partIndex++;
                        if (partIndex >= ProposalFormat.MaxPartsPerProposal)
                        {
                            throw new ArgumentException("proposal " + ProposalCodec.Format(Seq) + " needs more than " + ProposalCodec.Format(ProposalFormat.MaxPartsPerProposal) + " parts");
                        }
                        partStart = ProposalCodec.WritePartHeader(scratch, this, partIndex);
                        bodyStart = scratch.Length;
                        itemStart = scratch.Length;
                        ProposalCodec.WriteItem(scratch, this, tag, i);
                        if (scratch.Length - partStart > limits.MaxPartBytes) throw ItemTooLarge(tag, i, scratch.Length - itemStart, limits);
                    }
                    if (scratch.Length > limits.MaxTotalBytes)
                    {
                        throw new ArgumentException("proposal " + ProposalCodec.Format(Seq) + " needs more than " + ProposalCodec.Format(limits.MaxTotalBytes) + " bytes");
                    }
                }
            }
            starts.Add(partStart);
            lengths.Add(scratch.Length - partStart);
            byte[] buffer = scratch.Buffer;
            for (int i = 0; i < starts.Count; i++) emitPart(buffer, starts[i], lengths[i]);
        }

        /// <summary>
        /// Appends the <c>(slot, type)</c> pairs of every update component: the pairs whose incoming host values the
        /// client holds while the proposal is pending (ARCH §5.5.4). The game layer adds pairs that semantic intents
        /// predict (for example the target's policy buffer). Returns the number of pairs appended.
        /// </summary>
        public int CollectCoveredPairs(List<(NetSlot slot, TypeRank type)> output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            int added = 0;
            for (int i = 0; i < Count(Updates); i++)
            {
                ProposalUpdate update = Updates[i];
                if (update == null || update.Components == null) continue;
                for (int c = 0; c < update.Components.Count; c++)
                {
                    output.Add((update.Slot, update.Components[c].Type));
                    added++;
                }
            }
            return added;
        }

        /// <summary>An upper estimate of the encoded size, without encoding (the tracker's transfer allowance).</summary>
        internal long EstimateEncodedBytes()
        {
            long bytes = 32;
            if (Tool != null) bytes += 24 + (Tool.ToolTypeName == null ? 0 : Tool.ToolTypeName.Length * 3L);
            for (int i = 0; i < Count(Creates); i++)
            {
                bytes += 24;
                if (Creates[i] != null) bytes += EstimateComponents(Creates[i].Components);
            }
            for (int i = 0; i < Count(Updates); i++)
            {
                bytes += 12;
                if (Updates[i] != null) bytes += EstimateComponents(Updates[i].Components);
            }
            bytes += 10L * Count(Deletes) + 6L * Count(Touches) + 96L * Count(Stamps);
            for (int i = 0; i < Count(Zones); i++)
            {
                ushort[] ranks = Zones[i] == null ? null : Zones[i].ZoneRanks;
                bytes += 64 + (ranks == null ? 0 : 6L * ranks.Length);
            }
            for (int i = 0; i < Count(Intents); i++)
            {
                SemanticIntent s = Intents[i];
                if (s == null) continue;
                bytes += 32 + (s.Text == null ? 0 : s.Text.Length * 3L) + (s.IntArray == null ? 0 : 5L * s.IntArray.Length) + (s.Blob == null ? 0 : s.Blob.Length);
            }
            return bytes;
        }

        public override string ToString()
        {
            var ci = CultureInfo.InvariantCulture;
            string text = "proposal " + Seq.ToString(ci) + " " + Kind + " (" + Count(Creates).ToString(ci) + " creates, " + Count(Updates).ToString(ci)
                          + " updates, " + Count(Deletes).ToString(ci) + " deletes, " + Count(Touches).ToString(ci) + " touches, "
                          + Count(Zones).ToString(ci) + " zones, " + Count(Stamps).ToString(ci) + " stamps, " + Count(Intents).ToString(ci) + " intents)";
            return LimitViolation == null ? text : text + " [over limits: " + LimitViolation + "]";
        }

        /// <summary>The header of <paramref name="source"/> without items, flagged with <paramref name="violation"/>.</summary>
        internal static Proposal CreateShell(Proposal source, string violation)
        {
            return new Proposal
            {
                Seq = source.Seq,
                Kind = source.Kind,
                BaseEpoch = source.BaseEpoch,
                Tool = source.Tool,
                LimitViolation = violation
            };
        }

        private static int Count<T>(List<T> list) => list == null ? 0 : list.Count;

        private static long EstimateComponents(List<ComponentPayload> components)
        {
            if (components == null) return 0;
            long bytes = 0;
            for (int i = 0; i < components.Count; i++) bytes += 8 + components[i].Length;
            return bytes;
        }

        private ArgumentException ItemTooLarge(byte tag, int index, int itemBytes, ProposalLimits limits)
        {
            return new ArgumentException("proposal " + ProposalCodec.Format(Seq) + ": item " + ProposalCodec.Format(index) + " of tag "
                                         + ProposalCodec.Format(tag) + " needs " + ProposalCodec.Format(itemBytes) + " bytes and does not fit into a part of "
                                         + ProposalCodec.Format(limits.MaxPartBytes) + " bytes");
        }
    }
}
