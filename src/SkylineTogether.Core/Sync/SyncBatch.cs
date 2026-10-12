using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Sync
{
    /// <summary>Kinds of records in a <see cref="SyncBatch"/>.</summary>
    public enum SyncRecordKind : byte
    {
        /// <summary>Create the entity (or reconcile it when the id is already bound) with the given components.</summary>
        Spawn = 1,
        /// <summary>Structural or authored change: write the components, add/remove presence, tag Updated.</summary>
        Upsert = 2,
        /// <summary>Delete the entity.</summary>
        Despawn = 3,
        /// <summary>Simulation values only: write the components without tagging (no local re-derivation).</summary>
        Values = 4,
        /// <summary>Binds a net id to a prefab by its PrefabID.</summary>
        Prefab = 5,
        /// <summary>Zone cells of a derived zone block, keyed by owner edge and block geometry.</summary>
        ZoneCells = 6,
        /// <summary>Host → client: a range of net ids the client may assign to entities it creates.</summary>
        IdRange = 7,
        /// <summary>Client → host: the money the local apply charged (the host charges it for real).</summary>
        Cost = 8,
        /// <summary>Host → client: the host's simulation frame (clock follow).</summary>
        Clock = 9,
        /// <summary>Client → host: a request (<see cref="SyncRequest"/>).</summary>
        Request = 10,
        /// <summary>
        /// Global state (<c>GlobalStateSync</c>): a channel id (<see cref="GlobalChannel"/>) and an opaque payload.
        /// Host → client: the channel's full state; client → host: the entries the client changed.
        /// </summary>
        Global = 11,
    }

    /// <summary>What a client asks the host for.</summary>
    public enum SyncRequest : byte
    {
        /// <summary>Another range of net ids.</summary>
        MoreIds = 1,
        /// <summary>The full state of every line (after a local route-tool preview window).</summary>
        ResendRoutes = 2,
        /// <summary>The full state of every replicated entity (repair).</summary>
        ResendAll = 3,
    }

    /// <summary>How one component travels.</summary>
    public enum SyncComponentKind : byte
    {
        /// <summary>A component's data through its <c>ISerializable</c>.</summary>
        Data = 0,
        /// <summary>A dynamic buffer: element count, then each element through <c>ISerializable</c>.</summary>
        Buffer = 1,
        /// <summary>Presence only (tags and <c>IEmptySerializable</c> types).</summary>
        Presence = 2,
    }

    /// <summary>One component of an entity record.</summary>
    public struct SyncComponent
    {
        public ulong TypeHash;
        public SyncComponentKind Kind;
        /// <summary>Serialized bytes (empty for <see cref="SyncComponentKind.Presence"/>).</summary>
        public byte[] Data;
        public int Offset;
        public int Count;
    }

    /// <summary>A decoded record; fields are used according to <see cref="Kind"/>.</summary>
    public sealed class SyncRecord
    {
        public SyncRecordKind Kind;
        public uint NetId;
        public Family Family;
        /// <summary>For <see cref="SyncRecordKind.Spawn"/> and <see cref="SyncRecordKind.Upsert"/>: the full replicated type set
        /// (components not listed are removed on the receiver when they are replicated types).</summary>
        public bool CompleteSet;
        public readonly List<SyncComponent> Components = new List<SyncComponent>();
        /// <summary>For <see cref="SyncRecordKind.Prefab"/>: a readable name (diagnostics) and the game-encoded PrefabID.</summary>
        public string PrefabName;
        public byte[] PrefabData;
        public ZoneBlockKey Block;
        public uint[] ZoneIds;
        public uint RangeStart, RangeCount;
        public long Amount;
        public uint Frame;
        public SyncRequest Request;
        /// <summary>For <see cref="SyncRecordKind.Global"/>: the channel id and a copy of its payload.</summary>
        public uint Channel;
        public byte[] Payload;

        public override string ToString() => Kind + " #" + NetId + (Components.Count > 0 ? " (" + Components.Count + " components)" : "");
    }

    /// <summary>Identity of a zone block without an entity: its owner edge, side and quantised geometry.</summary>
    public struct ZoneBlockKey : IEquatable<ZoneBlockKey>
    {
        public uint OwnerNetId;
        /// <summary>Block position x/z quantised to 1/64 m.</summary>
        public int X, Z;
        /// <summary>Block direction quantised to 1/1024.</summary>
        public int DirX, DirZ;
        public int SizeX, SizeY;

        public bool Equals(ZoneBlockKey o) => OwnerNetId == o.OwnerNetId && X == o.X && Z == o.Z && DirX == o.DirX && DirZ == o.DirZ && SizeX == o.SizeX && SizeY == o.SizeY;
        public override bool Equals(object obj) => obj is ZoneBlockKey o && Equals(o);
        public override int GetHashCode() => unchecked((int)OwnerNetId * 31 + X * 17 + Z * 13 + DirX * 7 + DirZ * 3 + SizeX + SizeY * 5);

        public static int Quantize(float v, float scale) => (int)Math.Round(v * scale);

        /// <summary>Squared distance in quantised units, for the nearest-match fallback.</summary>
        public long DistanceSquared(ZoneBlockKey o)
        {
            long dx = X - o.X, dz = Z - o.Z;
            return dx * dx + dz * dz;
        }
    }

    /// <summary>
    /// The replication batch: a self-describing list of entity, prefab and channel records. The host sends one per
    /// frame with changes (<c>MessageKind.Epoch</c>); a client sends its predicted edits the same way
    /// (<c>MessageKind.Proposal</c>). Component bytes are the game's own save encoding with entity references written
    /// as net ids, so the format follows game patches by itself.
    /// </summary>
    public static class SyncBatch
    {
        public const byte Version = 1;
        private const int MaxRecords = 1 << 22;
        private const int MaxComponents = 512;
        private const int MaxStringBytes = 1024;
        private const int MaxZoneCells = 4096;
        private const int MaxGlobalPayload = 1 << 22;

        public sealed class Writer
        {
            private readonly ByteWriter _w = new ByteWriter(1 << 16);
            private int _countPosition;
            private uint _count;

            public Writer()
            {
                Reset();
            }

            public int RecordCount => (int)_count;

            public int Length => _w.Length;

            public bool IsEmpty => _count == 0;

            public void Reset()
            {
                _w.Reset();
                _w.WriteByte(Version);
                _countPosition = _w.Reserve(4);
                _count = 0;
            }

            /// <summary>Starts an entity record; follow with <see cref="AddComponent"/> calls and <see cref="EndEntity"/>.</summary>
            public void BeginEntity(SyncRecordKind kind, uint netId, Family family, bool completeSet)
            {
                if (kind != SyncRecordKind.Spawn && kind != SyncRecordKind.Upsert && kind != SyncRecordKind.Values) throw new ArgumentException("not an entity record kind", nameof(kind));
                _w.WriteByte((byte)kind);
                _w.WriteVarUInt32(netId);
                _w.WriteByte((byte)family);
                _w.WriteBool(completeSet);
                _componentCountPosition = _w.Reserve(2);
                _componentCount = 0;
            }

            private int _componentCountPosition;
            private int _componentCount;

            public void AddComponent(ulong typeHash, SyncComponentKind kind, byte[] data, int offset, int count)
            {
                if (_componentCount >= MaxComponents) throw new InvalidOperationException("too many components in one record");
                _w.WriteUInt64(typeHash);
                _w.WriteByte((byte)kind);
                if (kind != SyncComponentKind.Presence)
                {
                    _w.WriteVarUInt32((uint)count);
                    if (count > 0) _w.WriteBytes(data, offset, count);
                }
                _componentCount++;
            }

            public void EndEntity()
            {
                _w.Buffer[_componentCountPosition] = (byte)_componentCount;
                _w.Buffer[_componentCountPosition + 1] = (byte)(_componentCount >> 8);
                _count++;
            }

            public void Despawn(uint netId)
            {
                _w.WriteByte((byte)SyncRecordKind.Despawn);
                _w.WriteVarUInt32(netId);
                _count++;
            }

            public void Prefab(uint netId, string name, byte[] data, int offset, int count)
            {
                if (count > MaxStringBytes * 4) throw new ArgumentException("prefab id too long", nameof(count));
                _w.WriteByte((byte)SyncRecordKind.Prefab);
                _w.WriteVarUInt32(netId);
                _w.WriteString(Truncate(name), MaxStringBytes);
                _w.WriteVarUInt32((uint)count);
                if (count > 0) _w.WriteBytes(data, offset, count);
                _count++;
            }

            private static string Truncate(string s)
            {
                s = s ?? "";
                return s.Length > 200 ? s.Substring(0, 200) : s;
            }

            public void ZoneCells(ZoneBlockKey key, IReadOnlyList<uint> zoneIds)
            {
                if (zoneIds.Count > MaxZoneCells) throw new ArgumentException("too many zone cells", nameof(zoneIds));
                _w.WriteByte((byte)SyncRecordKind.ZoneCells);
                _w.WriteVarUInt32(key.OwnerNetId);
                _w.WriteVarInt32(key.X);
                _w.WriteVarInt32(key.Z);
                _w.WriteVarInt32(key.DirX);
                _w.WriteVarInt32(key.DirZ);
                _w.WriteVarInt32(key.SizeX);
                _w.WriteVarInt32(key.SizeY);
                _w.WriteVarUInt32((uint)zoneIds.Count);
                // run-length: (value, run) pairs
                int i = 0;
                while (i < zoneIds.Count)
                {
                    uint v = zoneIds[i];
                    int run = 1;
                    while (i + run < zoneIds.Count && zoneIds[i + run] == v) run++;
                    _w.WriteVarUInt32(v);
                    _w.WriteVarUInt32((uint)run);
                    i += run;
                }
                _count++;
            }

            public void IdRange(uint start, uint count)
            {
                _w.WriteByte((byte)SyncRecordKind.IdRange);
                _w.WriteVarUInt32(start);
                _w.WriteVarUInt32(count);
                _count++;
            }

            public void Cost(long amount)
            {
                _w.WriteByte((byte)SyncRecordKind.Cost);
                _w.WriteVarInt64(amount);
                _count++;
            }

            public void Request(SyncRequest request)
            {
                _w.WriteByte((byte)SyncRecordKind.Request);
                _w.WriteByte((byte)request);
                _count++;
            }

            public void Clock(uint frame)
            {
                _w.WriteByte((byte)SyncRecordKind.Clock);
                _w.WriteUInt32(frame);
                _count++;
            }

            /// <summary>A global-state record (<see cref="SyncRecordKind.Global"/>).</summary>
            public void Global(uint channel, byte[] data, int offset, int count)
            {
                if (count < 0 || count > MaxGlobalPayload) throw new ArgumentException("global payload of " + count + " bytes", nameof(count));
                _w.WriteByte((byte)SyncRecordKind.Global);
                _w.WriteUInt32(channel);
                _w.WriteVarUInt32((uint)count);
                if (count > 0) _w.WriteBytes(data, offset, count);
                _count++;
            }

            /// <summary>The finished batch bytes (valid until the next write).</summary>
            public ArraySegment<byte> Finish()
            {
                _w.PatchUInt32(_countPosition, _count);
                return new ArraySegment<byte>(_w.Buffer, 0, _w.Length);
            }
        }

        /// <summary>
        /// Decodes a batch. Component data segments point into <paramref name="buffer"/>, which must stay unchanged while
        /// the records are used. Throws <see cref="DecodeException"/> for malformed input.
        /// </summary>
        public static List<SyncRecord> Read(byte[] buffer, int offset, int count)
        {
            var r = new ByteReader(buffer, offset, count);
            try
            {
                byte version = r.ReadByte();
                if (version != Version) throw new DecodeException("sync batch version " + version + " (expected " + Version + ")");
                uint n = r.ReadUInt32();
                if (n > MaxRecords) throw new DecodeException("sync batch with " + n + " records");
                var list = new List<SyncRecord>((int)Math.Min(n, 4096));
                for (uint i = 0; i < n; i++) list.Add(ReadRecord(r, buffer, offset));
                if (!r.IsAtEnd) throw new DecodeException("sync batch has " + r.Remaining + " trailing bytes");
                return list;
            }
            catch (DecodeException)
            {
                throw;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IndexOutOfRangeException || ex is InvalidOperationException)
            {
                throw new DecodeException("malformed sync batch: " + ex.Message);
            }
        }

        private static SyncRecord ReadRecord(ByteReader r, byte[] buffer, int baseOffset)
        {
            var rec = new SyncRecord { Kind = (SyncRecordKind)r.ReadByte() };
            switch (rec.Kind)
            {
                case SyncRecordKind.Spawn:
                case SyncRecordKind.Upsert:
                case SyncRecordKind.Values:
                {
                    rec.NetId = r.ReadVarUInt32();
                    rec.Family = (Family)r.ReadByte();
                    rec.CompleteSet = r.ReadBool();
                    int components = r.ReadUInt16();
                    if (components > MaxComponents) throw new DecodeException("record with " + components + " components");
                    for (int c = 0; c < components; c++)
                    {
                        var comp = new SyncComponent { TypeHash = r.ReadUInt64(), Kind = (SyncComponentKind)r.ReadByte(), Data = buffer };
                        if (comp.Kind > SyncComponentKind.Presence) throw new DecodeException("unknown component kind " + (byte)comp.Kind);
                        if (comp.Kind != SyncComponentKind.Presence)
                        {
                            uint len = r.ReadVarUInt32();
                            if (len > (uint)r.Remaining) throw new DecodeException("component of " + len + " bytes overruns the batch");
                            comp.Offset = baseOffset + r.Position;
                            comp.Count = (int)len;
                            r.Skip((int)len);
                        }
                        rec.Components.Add(comp);
                    }
                    return rec;
                }
                case SyncRecordKind.Despawn:
                    rec.NetId = r.ReadVarUInt32();
                    return rec;
                case SyncRecordKind.Prefab:
                    rec.NetId = r.ReadVarUInt32();
                    rec.PrefabName = r.ReadString(MaxStringBytes);
                    uint idLength = r.ReadVarUInt32();
                    if (idLength > MaxStringBytes * 4 || idLength > (uint)r.Remaining) throw new DecodeException("prefab id of " + idLength + " bytes");
                    rec.PrefabData = new byte[idLength];
                    r.ReadBytes(rec.PrefabData, 0, (int)idLength);
                    return rec;
                case SyncRecordKind.ZoneCells:
                {
                    rec.Block = new ZoneBlockKey
                    {
                        OwnerNetId = r.ReadVarUInt32(),
                        X = r.ReadVarInt32(),
                        Z = r.ReadVarInt32(),
                        DirX = r.ReadVarInt32(),
                        DirZ = r.ReadVarInt32(),
                        SizeX = r.ReadVarInt32(),
                        SizeY = r.ReadVarInt32(),
                    };
                    uint cells = r.ReadVarUInt32();
                    if (cells > MaxZoneCells) throw new DecodeException("zone block with " + cells + " cells");
                    rec.ZoneIds = new uint[cells];
                    int i = 0;
                    while (i < cells)
                    {
                        uint v = r.ReadVarUInt32();
                        uint run = r.ReadVarUInt32();
                        if (run == 0 || run > cells - i) throw new DecodeException("bad zone cell run");
                        for (int k = 0; k < run; k++) rec.ZoneIds[i++] = v;
                    }
                    return rec;
                }
                case SyncRecordKind.IdRange:
                    rec.RangeStart = r.ReadVarUInt32();
                    rec.RangeCount = r.ReadVarUInt32();
                    return rec;
                case SyncRecordKind.Cost:
                    rec.Amount = r.ReadVarInt64();
                    return rec;
                case SyncRecordKind.Clock:
                    rec.Frame = r.ReadUInt32();
                    return rec;
                case SyncRecordKind.Request:
                    rec.Request = (SyncRequest)r.ReadByte();
                    return rec;
                case SyncRecordKind.Global:
                {
                    rec.Channel = r.ReadUInt32();
                    uint length = r.ReadVarUInt32();
                    if (length > MaxGlobalPayload || length > (uint)r.Remaining) throw new DecodeException("global payload of " + length + " bytes");
                    rec.Payload = new byte[length];
                    if (length > 0) r.ReadBytes(rec.Payload, 0, (int)length);
                    return rec;
                }
                default:
                    throw new DecodeException("unknown sync record kind " + (byte)rec.Kind);
            }
        }
    }
}
