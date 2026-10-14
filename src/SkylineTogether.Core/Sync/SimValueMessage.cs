using System;
using System.Collections.Generic;
using System.Globalization;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// Wire format of <see cref="MessageKind.SimValues"/> (host → client; ARCH D5, §4.7.9, §4.8): simulation values of
    /// structure entities, either streamed by attention (in view, rolling refresh) or as a time-aligned inspection
    /// sample of the selected entity.
    /// <code>
    /// u8 version | u8 type (1 = values, 2 = sample) | [type 2: u32 sampleTick | varuint selectedNetId]
    /// | varuint n | n × entity
    /// entity    = varuint netId | u8 family | u16 componentCount | componentCount × component
    /// component = u64 stableTypeHash | u8 kind (data 0, buffer 1) | varuint length | length bytes
    /// </code>
    /// Component bytes are exactly what <c>EntityCodec</c> writes for a component (derived-key header plus the game's
    /// save encoding), so the decoded components are <see cref="SyncComponent"/>s that the codec's reader applies.
    /// Presence never travels here: a values message cannot add or remove components.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class SimValueMessage
    {
        public const byte Version = 1;
        public const byte TypeValues = 1;
        public const byte TypeSample = 2;

        /// <summary>Entities in one message at most.</summary>
        public const int MaxEntities = 1 << 16;

        /// <summary>Components of one entity at most (same limit as a sync batch record).</summary>
        public const int MaxComponents = 512;

        /// <summary>Bytes of one component at most.</summary>
        public const int MaxComponentBytes = 1 << 22;

        private const string What = "sim-values message";

        /// <summary>Starts a values message; returns the position of the entity count for <see cref="End"/>.</summary>
        public static int BeginValues(ByteWriter w)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            w.WriteByte(Version);
            w.WriteByte(TypeValues);
            return w.Reserve(4);
        }

        /// <summary>Starts a sample message stamped with the host tick it was taken at; returns the count position.</summary>
        public static int BeginSample(ByteWriter w, uint sampleTick, uint selectedNetId)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            w.WriteByte(Version);
            w.WriteByte(TypeSample);
            w.WriteUInt32(sampleTick);
            w.WriteVarUInt32(selectedNetId);
            return w.Reserve(4);
        }

        /// <summary>Patches the entity count (a fixed u32 slot, so writers can append entities incrementally).</summary>
        public static void End(ByteWriter w, int countPosition, int entities)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            if (entities < 0 || entities > MaxEntities) throw new ArgumentOutOfRangeException(nameof(entities), entities, "0 to " + MaxEntities + " entities");
            w.PatchUInt32(countPosition, (uint)entities);
        }

        /// <summary>Starts an entity; returns the position of its component count for <see cref="EndEntity"/>.</summary>
        public static int BeginEntity(ByteWriter w, uint netId, Family family)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            w.WriteVarUInt32(netId);
            w.WriteByte((byte)family);
            int position = w.Length;
            w.WriteUInt16(0);
            return position;
        }

        /// <summary>Appends one component (data or buffer kind) to the entity being written.</summary>
        public static void AddComponent(ByteWriter w, ulong typeHash, SyncComponentKind kind, byte[] data, int offset, int count)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            if (kind == SyncComponentKind.Presence) throw new ArgumentException("values messages carry no presence", nameof(kind));
            if (count < 0 || count > MaxComponentBytes) throw new ArgumentOutOfRangeException(nameof(count));
            w.WriteUInt64(typeHash);
            w.WriteByte((byte)kind);
            w.WriteVarUInt32((uint)count);
            if (count > 0) w.WriteBytes(data, offset, count);
        }

        /// <summary>Patches the entity's component count.</summary>
        public static void EndEntity(ByteWriter w, int countPosition, int components)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            if (components < 0 || components > MaxComponents) throw new ArgumentOutOfRangeException(nameof(components));
            w.Buffer[countPosition] = (byte)components;
            w.Buffer[countPosition + 1] = (byte)(components >> 8);
        }

        /// <summary>Removes an entity begun at <paramref name="entityStart"/> (the writer length before <see cref="BeginEntity"/>).</summary>
        public static void DropEntity(ByteWriter w, int entityStart)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            w.Truncate(entityStart);
        }

        /// <summary>
        /// Decodes a message. Component segments point into <paramref name="buffer"/> (absolute offsets), which must stay
        /// unchanged while the records are used; the records have kind <see cref="SyncRecordKind.Values"/>. Throws
        /// <see cref="DecodeException"/> on malformed input.
        /// </summary>
        /// <summary>
        /// The header of an inspection sample (its tick and entity count) without decoding the entities (the
        /// sample is queued until its tick and decoded once, when applied). Throws <see cref="DecodeException"/> for a
        /// malformed header or another message type.
        /// </summary>
        public static void ReadSampleHeader(byte[] buffer, int offset, int count, out uint sampleTick, out uint entities)
        {
            var r = new ByteReader(buffer, offset, count);
            try
            {
                byte version = r.ReadByte();
                if (version != Version) throw new DecodeException(What + " version " + version.ToString(CultureInfo.InvariantCulture) + " (expected " + Version + ")");
                if (r.ReadByte() != TypeSample) throw new DecodeException(What + ": not a sample");
                sampleTick = r.ReadUInt32();
                r.ReadVarUInt32();
                entities = r.ReadUInt32();
                if (entities > MaxEntities || entities > (uint)(r.Remaining / 4)) throw new DecodeException(What + " with " + entities + " entities");
            }
            catch (DecodeException)
            {
                throw;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IndexOutOfRangeException || ex is InvalidOperationException)
            {
                throw new DecodeException("malformed " + What + ": " + ex.Message);
            }
        }

        public static List<SyncRecord> Read(byte[] buffer, int offset, int count, out byte type, out uint sampleTick, out uint selectedNetId)
        {
            var r = new ByteReader(buffer, offset, count);
            type = 0;
            sampleTick = 0;
            selectedNetId = 0;
            try
            {
                byte version = r.ReadByte();
                if (version != Version) throw new DecodeException(What + " version " + version.ToString(CultureInfo.InvariantCulture) + " (expected " + Version + ")");
                type = r.ReadByte();
                if (type == TypeSample)
                {
                    sampleTick = r.ReadUInt32();
                    selectedNetId = r.ReadVarUInt32();
                }
                else if (type != TypeValues)
                {
                    throw new DecodeException(What + ": unknown type " + type.ToString(CultureInfo.InvariantCulture));
                }
                uint n = r.ReadUInt32();
                // every entity needs at least 4 bytes (id, family, component count)
                if (n > MaxEntities || n > (uint)(r.Remaining / 4)) throw new DecodeException(What + " with " + n + " entities");
                var list = new List<SyncRecord>((int)n);
                for (uint i = 0; i < n; i++)
                {
                    var rec = new SyncRecord { Kind = SyncRecordKind.Values, NetId = r.ReadVarUInt32(), Family = (Family)r.ReadByte() };
                    int components = r.ReadUInt16();
                    if (components > MaxComponents) throw new DecodeException(What + ": entity with " + components + " components");
                    for (int c = 0; c < components; c++)
                    {
                        var comp = new SyncComponent { TypeHash = r.ReadUInt64(), Kind = (SyncComponentKind)r.ReadByte(), Data = buffer };
                        if (comp.Kind != SyncComponentKind.Data && comp.Kind != SyncComponentKind.Buffer)
                        {
                            throw new DecodeException(What + ": component kind " + ((byte)comp.Kind).ToString(CultureInfo.InvariantCulture));
                        }
                        uint length = r.ReadVarUInt32();
                        if (length > MaxComponentBytes || length > (uint)r.Remaining) throw new DecodeException(What + ": component of " + length + " bytes overruns the message");
                        comp.Offset = offset + r.Position;
                        comp.Count = (int)length;
                        r.Skip((int)length);
                        rec.Components.Add(comp);
                    }
                    list.Add(rec);
                }
                if (!r.IsAtEnd) throw new DecodeException(What + " has " + r.Remaining + " trailing bytes");
                return list;
            }
            catch (DecodeException)
            {
                throw;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IndexOutOfRangeException || ex is InvalidOperationException)
            {
                throw new DecodeException("malformed " + What + ": " + ex.Message);
            }
        }
    }
}
