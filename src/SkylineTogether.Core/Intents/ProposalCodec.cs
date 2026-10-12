using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;

namespace SkylineTogether.Core.Intents
{
    /// <summary>
    /// Encoders and strict decoders of the proposal format (<see cref="ProposalFormat"/>). Encoders throw
    /// <see cref="ArgumentException"/> for values the format cannot carry (caller bugs; <see cref="CheckEncodable"/>
    /// reports them before anything is written); decoders throw <see cref="DecodeException"/> for malformed input and
    /// report per-proposal cap violations through <see cref="ProposalDecodeState.Violation"/>.
    /// </summary>
    internal static class ProposalCodec
    {
        internal const byte FlagMoreParts = 1;
        private const byte KnownPartFlags = FlagMoreParts;

        internal const byte TagCreate = 1;
        internal const byte TagUpdate = 2;
        internal const byte TagDelete = 3;
        internal const byte TagTouch = 4;
        internal const byte TagZone = 5;
        internal const byte TagStamp = 6;
        internal const byte TagIntent = 7;

        private const byte ComponentHasEnabledBit = 1;
        private const byte ComponentEnabled = 2;
        private const byte ComponentRemoved = 4;
        private const byte KnownComponentFlags = ComponentHasEnabledBit | ComponentEnabled | ComponentRemoved;

        internal const TouchFlags KnownTouchFlags = TouchFlags.Updated | TouchFlags.BatchesUpdated | TouchFlags.PathfindUpdated | TouchFlags.EffectsUpdated;

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

        // ------------------------------------------------------------------ shared helpers

        internal static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        internal static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);

        internal static string Format(ulong value) => value.ToString(CultureInfo.InvariantCulture);

        private static int Count<T>(List<T> list) => list == null ? 0 : list.Count;

        /// <summary>Number of items of <paramref name="tag"/> in <paramref name="p"/> (null lists count 0).</summary>
        internal static int ItemCount(Proposal p, byte tag)
        {
            switch (tag)
            {
                case TagCreate: return Count(p.Creates);
                case TagUpdate: return Count(p.Updates);
                case TagDelete: return Count(p.Deletes);
                case TagTouch: return Count(p.Touches);
                case TagZone: return Count(p.Zones);
                case TagStamp: return Count(p.Stamps);
                case TagIntent: return Count(p.Intents);
                default: throw new ArgumentOutOfRangeException(nameof(tag));
            }
        }

        // ------------------------------------------------------------------ encoding

        /// <summary>
        /// Throws <see cref="ArgumentException"/> (with the path of the offending item) for anything the encoder cannot
        /// write or the host would reject for size: seq 0, undefined enum values, empty slots and local ids, unencodable
        /// keys, per-item caps of the format, and the per-proposal caps of <paramref name="limits"/>.
        /// </summary>
        internal static void CheckEncodable(Proposal p, ProposalLimits limits)
        {
            if (p.Seq == 0) throw new ArgumentException("the proposal has no sequence number (seq 0)");
            if ((byte)p.Kind > (byte)ProposalKind.Semantic) throw new ArgumentException("undefined proposal kind " + Format((long)p.Kind));
            if (p.Tool != null && p.Tool.ToolTypeName != null && Utf8.GetByteCount(p.Tool.ToolTypeName) > ProposalFormat.MaxToolTypeNameBytes)
            {
                throw new ArgumentException("tool type name exceeds " + Format(ProposalFormat.MaxToolTypeNameBytes) + " UTF-8 bytes");
            }
            string violation = CheckProposalCaps(p, limits);
            if (violation != null) throw new ArgumentException(violation);

            for (int i = 0; i < Count(p.Creates); i++)
            {
                ProposalCreate c = p.Creates[i];
                string at = "creates[" + Format(i) + "]";
                if (c == null) throw new ArgumentException(at + " is null");
                if (c.Local.IsNone) throw new ArgumentException(at + " has no LocalId");
                if ((byte)c.Family > (byte)Family.Generic) throw new ArgumentException(at + " has an undefined family");
                if (c.TypeSet.Value < 0) throw new ArgumentException(at + " has a negative type-set id");
                CheckComponents(c.Components, at);
            }
            for (int i = 0; i < Count(p.Updates); i++)
            {
                ProposalUpdate u = p.Updates[i];
                string at = "updates[" + Format(i) + "]";
                if (u == null) throw new ArgumentException(at + " is null");
                if (u.Slot.IsNone) throw new ArgumentException(at + " has no slot");
                CheckComponents(u.Components, at);
            }
            for (int i = 0; i < Count(p.Deletes); i++)
            {
                ProposalDelete d = p.Deletes[i];
                if (d == null) throw new ArgumentException("deletes[" + Format(i) + "] is null");
                if (d.Slot.IsNone) throw new ArgumentException("deletes[" + Format(i) + "] has no slot");
            }
            for (int i = 0; i < Count(p.Touches); i++)
            {
                TouchRecord t = p.Touches[i];
                if (t.Slot.IsNone) throw new ArgumentException("touches[" + Format(i) + "] has no slot");
                if ((t.Flags & ~KnownTouchFlags) != 0) throw new ArgumentException("touches[" + Format(i) + "] has unknown flags");
            }
            for (int i = 0; i < Count(p.Zones); i++)
            {
                ZoneEdit z = p.Zones[i];
                string at = "zones[" + Format(i) + "]";
                if (z == null) throw new ArgumentException(at + " is null");
                if (!DerivedKey.IsValidKind((byte)z.Block.Kind)) throw new ArgumentException(at + " has no valid block key");
                if (z.ZoneRanks != null && z.ZoneRanks.Length > ProposalFormat.MaxZoneCellsPerEdit)
                {
                    throw new ArgumentException(at + " has " + Format(z.ZoneRanks.Length) + " cells, more than " + Format(ProposalFormat.MaxZoneCellsPerEdit));
                }
            }
            for (int i = 0; i < Count(p.Stamps); i++)
            {
                if (p.Stamps[i] == null) throw new ArgumentException("stamps[" + Format(i) + "] is null");
            }
            for (int i = 0; i < Count(p.Intents); i++)
            {
                SemanticIntent s = p.Intents[i];
                string at = "intents[" + Format(i) + "]";
                if (s == null) throw new ArgumentException(at + " is null");
                if (!SemanticIntent.IsDefined(s.Kind)) throw new ArgumentException(at + " has an undefined kind " + Format((long)s.Kind));
                if (s.IntArray != null && s.IntArray.Length > ProposalFormat.MaxIntentArrayLength)
                {
                    throw new ArgumentException(at + " has " + Format(s.IntArray.Length) + " array elements, more than " + Format(ProposalFormat.MaxIntentArrayLength));
                }
                if (s.Blob != null && s.Blob.Length > ProposalFormat.MaxIntentBlobBytes)
                {
                    throw new ArgumentException(at + " has a blob of " + Format(s.Blob.Length) + " bytes, more than " + Format(ProposalFormat.MaxIntentBlobBytes));
                }
            }
        }

        /// <summary>The first per-proposal cap <paramref name="p"/> exceeds, or null.</summary>
        internal static string CheckProposalCaps(Proposal p, ProposalLimits limits)
        {
            int entities = p.EntityCount;
            if (entities > limits.MaxEntities) return "the proposal addresses " + Format(entities) + " entities, more than " + Format(limits.MaxEntities);
            if (Count(p.Touches) > limits.MaxTouches) return "the proposal has " + Format(Count(p.Touches)) + " touches, more than " + Format(limits.MaxTouches);
            if (Count(p.Stamps) > limits.MaxStamps) return "the proposal has " + Format(Count(p.Stamps)) + " terrain stamps, more than " + Format(limits.MaxStamps);
            if (Count(p.Intents) > limits.MaxIntents) return "the proposal has " + Format(Count(p.Intents)) + " intents, more than " + Format(limits.MaxIntents);
            long components = 0;
            for (int i = 0; i < Count(p.Creates); i++)
            {
                if (p.Creates[i] != null) components += Count(p.Creates[i].Components);
            }
            for (int i = 0; i < Count(p.Updates); i++)
            {
                if (p.Updates[i] != null) components += Count(p.Updates[i].Components);
            }
            if (components > limits.MaxComponentsTotal) return "the proposal has " + Format(components) + " components, more than " + Format(limits.MaxComponentsTotal);
            long cells = 0;
            for (int i = 0; i < Count(p.Zones); i++)
            {
                ushort[] ranks = p.Zones[i] == null ? null : p.Zones[i].ZoneRanks;
                if (ranks != null) cells += ranks.Length;
            }
            if (cells > limits.MaxZoneCellsTotal) return "the proposal has " + Format(cells) + " zone cells, more than " + Format(limits.MaxZoneCellsTotal);
            return null;
        }

        private static void CheckComponents(List<ComponentPayload> components, string at)
        {
            int count = Count(components);
            if (count > ProposalFormat.MaxComponentsPerItem)
            {
                throw new ArgumentException(at + " has " + Format(count) + " components, more than " + Format(ProposalFormat.MaxComponentsPerItem));
            }
            for (int i = 0; i < count; i++)
            {
                ComponentPayload c = components[i];
                if (c.Removed && (c.Length != 0 || c.HasEnabledBit))
                {
                    throw new ArgumentException(at + ".components[" + Format(i) + "] (" + c.Type + ") is removed but carries bytes or an enabled bit");
                }
            }
        }

        /// <summary>Writes a part header and returns the position of its first byte (the flags byte follows it).</summary>
        internal static int WritePartHeader(ByteWriter w, Proposal p, int partIndex)
        {
            int start = w.Length;
            w.WriteByte(ProposalFormat.Version);
            w.WriteByte(0);                                        // flags: patched to FlagMoreParts when a part follows
            w.WriteVarUInt32(p.Seq);
            w.WriteVarUInt32((uint)partIndex);
            if (partIndex == 0)
            {
                w.WriteByte((byte)p.Kind);
                w.WriteVarUInt32(p.BaseEpoch.Value);
                if (p.Tool == null)
                {
                    w.WriteByte(0);
                }
                else
                {
                    w.WriteByte(1);
                    WriteTool(w, p.Tool);
                }
            }
            return start;
        }

        /// <summary>Writes the item <paramref name="index"/> of the list <paramref name="tag"/>, tag byte included.</summary>
        internal static void WriteItem(ByteWriter w, Proposal p, byte tag, int index)
        {
            w.WriteByte(tag);
            switch (tag)
            {
                case TagCreate:
                    WriteCreate(w, p.Creates[index]);
                    break;
                case TagUpdate:
                {
                    ProposalUpdate u = p.Updates[index];
                    w.WriteVarUInt32(u.Slot.Value);
                    w.WriteVarUInt32(u.BaseRevision);
                    WriteComponents(w, u.Components);
                    break;
                }
                case TagDelete:
                {
                    ProposalDelete d = p.Deletes[index];
                    w.WriteVarUInt32(d.Slot.Value);
                    w.WriteVarUInt32(d.BaseRevision);
                    break;
                }
                case TagTouch:
                {
                    TouchRecord t = p.Touches[index];
                    w.WriteVarUInt32(t.Slot.Value);
                    w.WriteByte((byte)t.Flags);
                    break;
                }
                case TagZone:
                {
                    ZoneEdit z = p.Zones[index];
                    NetRefCodec.WriteKey(w, z.Block);
                    w.WriteVarUInt32(z.BaseRevision);
                    ushort[] ranks = z.ZoneRanks ?? Array.Empty<ushort>();
                    ZoneOverlayCodec.Encode(ranks, ranks.Length, w);
                    break;
                }
                case TagStamp:
                    p.Stamps[index].Write(w);
                    break;
                case TagIntent:
                    WriteIntent(w, p.Intents[index]);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(tag));
            }
        }

        private static void WriteTool(ByteWriter w, ToolHeader tool)
        {
            w.WriteVarUInt32(tool.ToolKind);
            WriteNullableIdentifier(w, tool.ToolTypeName, ProposalFormat.MaxToolTypeNameBytes, "tool type name");
            WritePrefab(w, tool.Prefab);
            w.WriteByte(tool.Mode);
            w.WriteVarInt64(tool.CostSum);
        }

        private static void WriteCreate(ByteWriter w, ProposalCreate c)
        {
            w.WriteVarUInt32(c.Local.Value);
            w.WriteByte((byte)c.Family);
            WritePrefab(w, c.Prefab);
            w.WriteVarUInt32((uint)c.TypeSet.Value);
            w.WriteVarUInt64(c.EnabledBits);
            WriteComponents(w, c.Components);
        }

        private static void WriteComponents(ByteWriter w, List<ComponentPayload> components)
        {
            int count = Count(components);
            w.WriteVarUInt32((uint)count);
            for (int i = 0; i < count; i++)
            {
                ComponentPayload c = components[i];
                w.WriteVarUInt32(c.Type.Value);
                if (c.Removed)
                {
                    w.WriteByte(ComponentRemoved);
                    continue;
                }
                byte flags = 0;
                if (c.HasEnabledBit)
                {
                    flags = ComponentHasEnabledBit;
                    if (c.Enabled) flags |= ComponentEnabled;
                }
                w.WriteByte(flags);
                int length = c.Length;
                w.WriteVarUInt32((uint)length);
                if (length > 0) w.WriteBytes(c.Bytes, 0, length);
            }
        }

        private static void WriteIntent(ByteWriter w, SemanticIntent s)
        {
            w.WriteVarUInt32((uint)s.Kind);
            w.WriteVarUInt32(s.Target.Value);
            w.WriteVarUInt32(s.BaseRevision);
            WritePrefab(w, s.Prefab);
            w.WriteVarInt32(s.IntValue);
            w.WriteSingle(s.FloatValue);
            w.WriteByte(s.BoolValue ? (byte)1 : (byte)0);
            WriteNullableText(w, s.Text, ProposalFormat.MaxTextChars);
            if (s.IntArray == null)
            {
                w.WriteVarUInt32(0);
            }
            else
            {
                w.WriteVarUInt32((uint)s.IntArray.Length + 1);
                for (int i = 0; i < s.IntArray.Length; i++) w.WriteVarInt32(s.IntArray[i]);
            }
            if (s.Blob == null)
            {
                w.WriteVarUInt32(0);
            }
            else
            {
                w.WriteVarUInt32((uint)s.Blob.Length + 1);
                if (s.Blob.Length > 0) w.WriteBytes(s.Blob, 0, s.Blob.Length);
            }
        }

        /// <summary><c>varint (rank + 1)</c>; 0 for <see cref="PrefabRank.None"/>.</summary>
        internal static void WritePrefab(ByteWriter w, PrefabRank prefab) => w.WriteVarUInt32(unchecked((uint)(prefab.Value + 1)));

        /// <summary>Free text: truncated to <paramref name="maxChars"/> UTF-16 units (never splitting a surrogate pair).</summary>
        private static void WriteNullableText(ByteWriter w, string text, int maxChars)
        {
            if (text == null)
            {
                w.WriteVarUInt32(0);
                return;
            }
            string value = ProtocolText.Truncate(text, maxChars);
            int length = Utf8.GetByteCount(value);
            w.WriteVarUInt32((uint)length + 1);
            if (length == 0) return;
            int position = w.Reserve(length);
            Utf8.GetBytes(value, 0, value.Length, w.Buffer, position);
        }

        /// <summary>An identifier: written verbatim; longer than <paramref name="maxBytes"/> is an argument error.</summary>
        private static void WriteNullableIdentifier(ByteWriter w, string text, int maxBytes, string field)
        {
            if (text == null)
            {
                w.WriteVarUInt32(0);
                return;
            }
            int length = Utf8.GetByteCount(text);
            if (length > maxBytes) throw new ArgumentException(field + " has " + Format(length) + " UTF-8 bytes, more than " + Format(maxBytes));
            w.WriteVarUInt32((uint)length + 1);
            if (length == 0) return;
            int position = w.Reserve(length);
            Utf8.GetBytes(text, 0, text.Length, w.Buffer, position);
        }

        // ------------------------------------------------------------------ decoding

        /// <summary>Reads the fields every part starts with. Throws <see cref="DecodeException"/>.</summary>
        internal static void ReadPartHeader(ByteReader r, out byte flags, out uint seq, out int partIndex)
        {
            byte version = r.ReadByte();
            if (version != ProposalFormat.Version)
            {
                throw new DecodeException("proposal: unsupported format version " + Format(version) + " (expected " + Format(ProposalFormat.Version) + ")");
            }
            flags = r.ReadByte();
            if ((flags & ~KnownPartFlags) != 0) throw new DecodeException("proposal: unknown part flags 0x" + flags.ToString("X2", CultureInfo.InvariantCulture));
            seq = r.ReadVarUInt32();
            if (seq == 0) throw new DecodeException("proposal: sequence number 0");
            uint index = r.ReadVarUInt32();
            if (index >= ProposalFormat.MaxPartsPerProposal)
            {
                throw new DecodeException("proposal " + Format(seq) + ": part index " + Format(index) + " exceeds " + Format(ProposalFormat.MaxPartsPerProposal - 1));
            }
            partIndex = (int)index;
        }

        /// <summary>Reads the proposal header of part 0 into <paramref name="target"/>.</summary>
        internal static void ReadProposalHeader(ByteReader r, Proposal target)
        {
            byte kind = r.ReadByte();
            if (kind > (byte)ProposalKind.Semantic) throw new DecodeException("proposal " + Format(target.Seq) + ": undefined kind " + Format(kind));
            target.Kind = (ProposalKind)kind;
            target.BaseEpoch = new EpochId(r.ReadVarUInt32());
            target.Tool = r.ReadBool() ? ReadTool(r) : null;
        }

        /// <summary>
        /// Reads items to the end of the reader's window and appends them to the state's proposal. Returns early, with
        /// <see cref="ProposalDecodeState.Violation"/> set, when a per-proposal cap would be exceeded (nothing beyond the
        /// cap is allocated). Throws <see cref="DecodeException"/> for malformed input.
        /// </summary>
        internal static void ReadItems(ByteReader r, ProposalDecodeState state)
        {
            Proposal p = state.Target;
            ProposalLimits limits = state.Limits;
            while (!r.IsAtEnd)
            {
                byte tag = r.ReadByte();
                if (tag < TagCreate || tag > TagIntent) throw new DecodeException("proposal " + Format(p.Seq) + ": unknown item tag " + Format(tag));
                if (tag < state.LastTag)
                {
                    throw new DecodeException("proposal " + Format(p.Seq) + ": item tag " + Format(tag) + " after " + Format(state.LastTag) + " (items out of order)");
                }
                state.LastTag = tag;
                switch (tag)
                {
                    case TagCreate:
                    {
                        if (!state.ReserveEntity()) return;
                        ProposalCreate create = ReadCreate(r, state);
                        if (create == null) return;
                        p.Creates.Add(create);
                        break;
                    }
                    case TagUpdate:
                    {
                        if (!state.ReserveEntity()) return;
                        NetSlot slot = ReadSlot(r, false, "update slot");
                        ushort revision = ReadRevision(r, "update revision");
                        List<ComponentPayload> components = ReadComponents(r, state, "update of slot", slot.Value);
                        if (components == null) return;
                        p.Updates.Add(new ProposalUpdate { Slot = slot, BaseRevision = revision, Components = components });
                        break;
                    }
                    case TagDelete:
                    {
                        if (!state.ReserveEntity()) return;
                        NetSlot slot = ReadSlot(r, false, "delete slot");
                        p.Deletes.Add(new ProposalDelete { Slot = slot, BaseRevision = ReadRevision(r, "delete revision") });
                        break;
                    }
                    case TagTouch:
                    {
                        if (p.Touches.Count >= limits.MaxTouches)
                        {
                            state.Violate("the proposal has more than " + Format(limits.MaxTouches) + " touches");
                            return;
                        }
                        NetSlot slot = ReadSlot(r, false, "touch slot");
                        var flags = (TouchFlags)r.ReadByte();
                        if ((flags & ~KnownTouchFlags) != 0) throw new DecodeException("proposal: touch of " + slot + " with unknown flags 0x" + ((byte)flags).ToString("X2", CultureInfo.InvariantCulture));
                        p.Touches.Add(new TouchRecord { Slot = slot, Flags = flags });
                        break;
                    }
                    case TagZone:
                    {
                        if (!state.ReserveEntity()) return;
                        ZoneEdit zone = ReadZone(r, state);
                        if (zone == null) return;
                        p.Zones.Add(zone);
                        break;
                    }
                    case TagStamp:
                        if (p.Stamps.Count >= limits.MaxStamps)
                        {
                            state.Violate("the proposal has more than " + Format(limits.MaxStamps) + " terrain stamps");
                            return;
                        }
                        p.Stamps.Add(TerrainStamp.Read(r));
                        break;
                    default:
                        if (p.Intents.Count >= limits.MaxIntents)
                        {
                            state.Violate("the proposal has more than " + Format(limits.MaxIntents) + " intents");
                            return;
                        }
                        p.Intents.Add(ReadIntent(r));
                        break;
                }
            }
        }

        private static ToolHeader ReadTool(ByteReader r)
        {
            uint toolKind = r.ReadVarUInt32();
            if (toolKind > ushort.MaxValue) throw new DecodeException("proposal: tool kind " + Format(toolKind) + " exceeds 65535");
            return new ToolHeader
            {
                ToolKind = (ushort)toolKind,
                ToolTypeName = ReadNullableIdentifier(r, ProposalFormat.MaxToolTypeNameBytes, "tool type name"),
                Prefab = ReadPrefab(r, "tool prefab"),
                Mode = r.ReadByte(),
                CostSum = r.ReadVarInt64()
            };
        }

        private static ProposalCreate ReadCreate(ByteReader r, ProposalDecodeState state)
        {
            uint local = r.ReadVarUInt32();
            if (local == 0) throw new DecodeException("proposal: create with LocalId 0");
            byte family = r.ReadByte();
            if (family > (byte)Family.Generic) throw new DecodeException("proposal: create " + Format(local) + " with undefined family " + Format(family));
            PrefabRank prefab = ReadPrefab(r, "create prefab");
            uint typeSet = r.ReadVarUInt32();
            if (typeSet > int.MaxValue) throw new DecodeException("proposal: create " + Format(local) + " with type-set id " + Format(typeSet) + " above int.MaxValue");
            ulong enabledBits = r.ReadVarUInt64();
            List<ComponentPayload> components = ReadComponents(r, state, "create of local id", local);
            if (components == null) return null;
            return new ProposalCreate
            {
                Local = new LocalId(local),
                Family = (Family)family,
                Prefab = prefab,
                TypeSet = new TypeSetId((int)typeSet),
                EnabledBits = enabledBits,
                Components = components
            };
        }

        /// <summary>
        /// Reads a component list. The owner (<paramref name="ownerKind"/>, <paramref name="ownerId"/>) is formatted only
        /// into error messages, so the success path allocates nothing per item besides the list and the payloads.
        /// </summary>
        private static List<ComponentPayload> ReadComponents(ByteReader r, ProposalDecodeState state, string ownerKind, uint ownerId)
        {
            uint count = r.ReadVarUInt32();
            if (count > ProposalFormat.MaxComponentsPerItem)
            {
                throw new DecodeException("proposal: " + Owner(ownerKind, ownerId) + " has " + Format(count) + " components, more than " + Format(ProposalFormat.MaxComponentsPerItem));
            }
            if ((long)count * 2 > r.Remaining) throw new DecodeException("proposal: " + Owner(ownerKind, ownerId) + ": " + Format(count) + " components overrun the part");
            if ((long)state.Components + count > state.Limits.MaxComponentsTotal)
            {
                state.Violate("the proposal has more than " + Format(state.Limits.MaxComponentsTotal) + " components");
                return null;
            }
            state.Components += (int)count;
            var list = new List<ComponentPayload>((int)count);
            for (int i = 0; i < (int)count; i++)
            {
                uint type = r.ReadVarUInt32();
                if (type > ushort.MaxValue) throw new DecodeException("proposal: " + Owner(ownerKind, ownerId) + ": type rank " + Format(type) + " exceeds 65535");
                byte flags = r.ReadByte();
                if ((flags & ~KnownComponentFlags) != 0)
                {
                    throw new DecodeException("proposal: " + Owner(ownerKind, ownerId) + ": unknown component flags 0x" + flags.ToString("X2", CultureInfo.InvariantCulture));
                }
                bool hasEnabledBit = (flags & ComponentHasEnabledBit) != 0;
                bool enabled = (flags & ComponentEnabled) != 0;
                bool removed = (flags & ComponentRemoved) != 0;
                if (enabled && !hasEnabledBit) throw new DecodeException("proposal: " + Owner(ownerKind, ownerId) + ": enabled flag without an enabled bit (non-canonical)");
                if (removed && hasEnabledBit) throw new DecodeException("proposal: " + Owner(ownerKind, ownerId) + ": removed component with an enabled bit");
                byte[] bytes = null;
                if (!removed)
                {
                    uint length = r.ReadVarUInt32();
                    if (length > (uint)r.Remaining) throw new DecodeException("proposal: " + Owner(ownerKind, ownerId) + ": payload of " + Format(length) + " bytes overruns the part");
                    if (length == 0)
                    {
                        bytes = Array.Empty<byte>();
                    }
                    else
                    {
                        bytes = new byte[length];
                        r.ReadBytes(bytes, 0, (int)length);
                    }
                }
                list.Add(new ComponentPayload { Type = new TypeRank((ushort)type), Bytes = bytes, HasEnabledBit = hasEnabledBit, Enabled = enabled, Removed = removed });
            }
            return list;
        }

        private static string Owner(string kind, uint id) => kind + " " + Format(id);

        private static ZoneEdit ReadZone(ByteReader r, ProposalDecodeState state)
        {
            DerivedKey block = NetRefCodec.ReadKey(r);
            ushort revision = ReadRevision(r, "zone revision");
            ushort[] scratch = state.ZoneScratch;
            int cells = ZoneOverlayCodec.Decode(r, scratch);           // more than MaxZoneCellsPerEdit cells: DecodeException
            if ((long)state.ZoneCells + cells > state.Limits.MaxZoneCellsTotal)
            {
                state.Violate("the proposal has more than " + Format(state.Limits.MaxZoneCellsTotal) + " zone cells");
                return null;
            }
            state.ZoneCells += cells;
            var ranks = new ushort[cells];
            Array.Copy(scratch, ranks, cells);
            return new ZoneEdit { Block = block, BaseRevision = revision, ZoneRanks = ranks };
        }

        private static SemanticIntent ReadIntent(ByteReader r)
        {
            uint kind = r.ReadVarUInt32();
            if (kind > ushort.MaxValue || !SemanticIntent.IsDefined((IntentKind)kind)) throw new DecodeException("proposal: undefined intent kind " + Format(kind));
            var s = new SemanticIntent
            {
                Kind = (IntentKind)kind,
                Target = ReadSlot(r, true, "intent target"),
                BaseRevision = ReadRevision(r, "intent revision"),
                Prefab = ReadPrefab(r, "intent prefab"),
                IntValue = r.ReadVarInt32(),
                FloatValue = r.ReadSingle(),
                BoolValue = r.ReadBool(),
                Text = ReadNullableText(r, ProposalFormat.MaxTextChars, "intent text")
            };
            s.IntArray = ReadNullableInts(r, ProposalFormat.MaxIntentArrayLength, "intent array");
            s.Blob = ReadNullableBytes(r, ProposalFormat.MaxIntentBlobBytes, "intent blob");
            return s;
        }

        internal static PrefabRank ReadPrefab(ByteReader r, string field)
        {
            uint plusOne = r.ReadVarUInt32();
            if (plusOne > 0x80000000u) throw new DecodeException("proposal: " + field + " " + Format(plusOne) + " - 1 exceeds int.MaxValue");
            return plusOne == 0 ? PrefabRank.None : new PrefabRank((int)(plusOne - 1));
        }

        private static NetSlot ReadSlot(ByteReader r, bool allowNone, string field)
        {
            uint value = r.ReadVarUInt32();
            if (value > NetSlot.MaxValue) throw new DecodeException("proposal: " + field + " " + Format(value) + " exceeds 0xFFFFFF");
            if (value == 0 && !allowNone) throw new DecodeException("proposal: " + field + " 0");
            return new NetSlot(value);
        }

        private static ushort ReadRevision(ByteReader r, string field)
        {
            uint value = r.ReadVarUInt32();
            if (value > ushort.MaxValue) throw new DecodeException("proposal: " + field + " " + Format(value) + " exceeds 65535");
            return (ushort)value;
        }

        private static string ReadNullableText(ByteReader r, int maxChars, string field)
        {
            uint head = r.ReadVarUInt32();
            if (head == 0) return null;
            uint length = head - 1;
            if (length > (uint)(maxChars * ProtocolText.MaxUtf8BytesPerChar))
            {
                throw new DecodeException("proposal: " + field + " of " + Format(length) + " bytes exceeds the limit of " + Format(maxChars) + " characters");
            }
            if (length > (uint)r.Remaining) throw new DecodeException("proposal: " + field + " overruns the part");
            if (length == 0) return string.Empty;
            ArraySegment<byte> bytes = r.ReadSegment((int)length);
            string text = Utf8.GetString(bytes.Array, bytes.Offset, bytes.Count);
            if (text.Length > maxChars)
            {
                throw new DecodeException("proposal: " + field + " has " + Format(text.Length) + " characters, more than " + Format(maxChars));
            }
            return ProtocolText.Sanitize(text);
        }

        private static string ReadNullableIdentifier(ByteReader r, int maxBytes, string field)
        {
            uint head = r.ReadVarUInt32();
            if (head == 0) return null;
            uint length = head - 1;
            if (length > (uint)maxBytes) throw new DecodeException("proposal: " + field + " of " + Format(length) + " bytes exceeds " + Format(maxBytes));
            if (length > (uint)r.Remaining) throw new DecodeException("proposal: " + field + " overruns the part");
            if (length == 0) return string.Empty;
            ArraySegment<byte> bytes = r.ReadSegment((int)length);
            return Utf8.GetString(bytes.Array, bytes.Offset, bytes.Count);
        }

        private static int[] ReadNullableInts(ByteReader r, int maxCount, string field)
        {
            uint head = r.ReadVarUInt32();
            if (head == 0) return null;
            uint count = head - 1;
            if (count > (uint)maxCount) throw new DecodeException("proposal: " + field + " of " + Format(count) + " elements exceeds " + Format(maxCount));
            if (count > (uint)r.Remaining) throw new DecodeException("proposal: " + field + " overruns the part");
            if (count == 0) return Array.Empty<int>();
            var values = new int[count];
            for (int i = 0; i < values.Length; i++) values[i] = r.ReadVarInt32();
            return values;
        }

        private static byte[] ReadNullableBytes(ByteReader r, int maxBytes, string field)
        {
            uint head = r.ReadVarUInt32();
            if (head == 0) return null;
            uint length = head - 1;
            if (length > (uint)maxBytes) throw new DecodeException("proposal: " + field + " of " + Format(length) + " bytes exceeds " + Format(maxBytes));
            if (length > (uint)r.Remaining) throw new DecodeException("proposal: " + field + " overruns the part");
            if (length == 0) return Array.Empty<byte>();
            var data = new byte[length];
            r.ReadBytes(data, 0, (int)length);
            return data;
        }
    }
}
