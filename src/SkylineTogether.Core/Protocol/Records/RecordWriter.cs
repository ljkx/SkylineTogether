using System;
using System.Collections.Generic;
using System.Globalization;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// Writes the records of epoch sections (ARCH §4.6.3). Every record is <c>u8 kind</c> followed by its body; bodies
    /// are self-delimiting (every variable part carries its length), so records need no outer length and a reader can
    /// find each record boundary. Records are appended to the target writer.
    /// <code>
    /// TYPESET     01  varint typeSetId (≥ 1) | varint count (≤ 64) | count × varint typeRank (strictly ascending)
    /// SPAWN       02  varint slot | u8 gen | varint prefabRank+1 (0 = none) | varint typeSetId | u8 flags {Mover, HasUpdateFrame, Repair}
    ///                 | [u8 updateFrame] | varint componentCount (≤ 64) | count × (varint typeRank | varint length | bytes)
    ///                 | varint64 enabledBits
    /// DESPAWN     03  varint slot | u8 gen | u8 flags {bit 0 DeleteChildrenAsHost}
    /// STRUCT      04  varint slot | varint typeSetId | varint addedCount (≤ 64) | count × (varint typeRank | varint length | bytes)
    /// VALUE       05  varint slot | varint typeRank | u8 hdr {HasEnabledBit, Enabled, PresenceOnly} | varint length | bytes
    /// TOUCH       06  varint slot | u8 tags {Updated, BatchesUpdated, PathfindUpdated, EffectsUpdated}
    /// BIND        07  u8 origin | varint localId (≥ 1) | varint slot | u8 gen
    /// POSE        08  varint slot | varint length | bytes                   (quantised transform and flags, core-motion)
    /// KEYED       09  key | varint typeRank | varint length | bytes        (key = NetRefCodec.WriteKey: u8 kind + fields)
    /// KEYED_DEL   0A  key
    /// ZONE        0B  key (Block) | varint revision (≤ 65535) | varint length | RLE bytes (ZoneOverlayCodec)
    /// ICON        0C  u8 op {bits 0-1 IconOp, bit 6 target, bit 7 location} | NetRef owner | varint prefabRank+1
    ///                 | u8 priority | u8 layer | u16 flags | [NetRef target] | [f32 x | f32 y | f32 z]
    /// NAME        0D  NetRef target | varint utf8Length | utf8                (≤ 64 characters; empty = clear)
    /// PFB         0E  varint length | bytes                                  (prefab-state payload, game-state-channels)
    /// SYS         0F  u32 entryId | varint length | blob
    /// RESULT      10  u8 origin | varint proposalSeq | u8 status {bit 0 rejected, bit 1 conflict} | varint reason | [varint conflictSlot]
    /// HASH        11  u8 class | varint bucket (≤ 16383) | u64 sum | varint count
    /// EVENT       12  u8 kind | varint length | bytes
    /// TERRAIN     13  varint length | bytes
    /// TSETKEY     14  varint length | keyframe (ITypeSetRegistry.WriteKeyframe)  (TSET section only)
    /// </code>
    /// Every method validates its arguments before writing (an <see cref="ArgumentException"/> leaves the target
    /// unchanged); SPAWN and STRUCT are written in three steps and can be rolled back with
    /// <see cref="CancelOpenRecord"/>. Free text (NAME) is truncated to its limit and non-finite floats (ICON location)
    /// are written as 0, so game data can never make an encoder throw.
    /// <para>Not thread-safe; one instance per thread.</para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class RecordWriter
    {
        private enum Open : byte { None, Spawn, Struct }

        private readonly ByteWriter _target;
        private ByteWriter _keyframe;
        private Open _open;
        private int _openStart;
        private int _declaredComponents;
        private int _writtenComponents;

        public RecordWriter(ByteWriter target)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
        }

        /// <summary>The writer the records go to.</summary>
        public ByteWriter Target => _target;

        /// <summary>Records completed since construction or <see cref="Reset"/>.</summary>
        public int RecordCount { get; private set; }

        /// <summary>True while a SPAWN or STRUCT record is open.</summary>
        public bool HasOpenRecord => _open != Open.None;

        /// <summary>Resets the target writer and the record count, and abandons an open record.</summary>
        public void Reset()
        {
            _target.Reset();
            RecordCount = 0;
            _open = Open.None;
        }

        /// <summary>Removes a partly written SPAWN or STRUCT record from the target.</summary>
        public void CancelOpenRecord()
        {
            if (_open == Open.None) return;
            _target.Truncate(_openStart);
            _open = Open.None;
        }

        public void TypeSetDef(TypeSetId id, IReadOnlyList<TypeRank> types)
        {
            EnsureNoOpenRecord();
            if (id.Value <= 0) throw new ArgumentOutOfRangeException(nameof(id), id, "a type-set id is 1 or more");
            if (types == null) throw new ArgumentNullException(nameof(types));
            if (types.Count > Limits.MaxComponentsPerRecord) throw new ArgumentException("a type set has at most 64 types", nameof(types));
            for (int i = 1; i < types.Count; i++)
            {
                if (types[i].Value <= types[i - 1].Value) throw new ArgumentException("type ranks must be strictly ascending", nameof(types));
            }
            _target.WriteByte((byte)RecordKind.TypeSetDef);
            _target.WriteVarUInt32((uint)id.Value);
            _target.WriteVarUInt32((uint)types.Count);
            for (int i = 0; i < types.Count; i++) _target.WriteVarUInt32(types[i].Value);
            RecordCount++;
        }

        /// <summary>Starts a SPAWN; exactly <see cref="SpawnHeader.ComponentCount"/> <see cref="Component"/> calls and
        /// <see cref="EndSpawn"/> follow.</summary>
        public void BeginSpawn(in SpawnHeader header)
        {
            EnsureNoOpenRecord();
            CheckSlot(header.Slot, "header.Slot");
            if (header.TypeSet.Value < 0) throw new ArgumentOutOfRangeException(nameof(header), "negative type-set id");
            if ((header.Flags & ~RecordFormat.KnownSpawnFlags) != 0) throw new ArgumentException("unknown spawn flags", nameof(header));
            if ((header.Flags & SpawnFlags.HasUpdateFrame) == 0 && header.UpdateFrame != 0)
            {
                throw new ArgumentException("UpdateFrame is set but SpawnFlags.HasUpdateFrame is not", nameof(header));
            }
            CheckComponentCount(header.ComponentCount);
            _openStart = _target.Length;
            _target.WriteByte((byte)RecordKind.Spawn);
            _target.WriteVarUInt32(header.Slot.Value);
            _target.WriteByte(header.Generation);
            WritePrefab(header.Prefab);
            _target.WriteVarUInt32((uint)header.TypeSet.Value);
            _target.WriteByte((byte)header.Flags);
            if ((header.Flags & SpawnFlags.HasUpdateFrame) != 0) _target.WriteByte(header.UpdateFrame);
            _target.WriteVarUInt32((uint)header.ComponentCount);
            OpenRecord(Open.Spawn, header.ComponentCount);
        }

        /// <summary>One component payload of the open SPAWN or STRUCT.</summary>
        public void Component(TypeRank type, byte[] payload, int offset, int count)
        {
            if (_open == Open.None) throw new InvalidOperationException("Component outside SPAWN or STRUCT");
            WireFormat.CheckOptionalRange(payload, offset, count, nameof(payload));
            if (_writtenComponents >= _declaredComponents)
            {
                throw new InvalidOperationException("more components than the " + _declaredComponents.ToString(CultureInfo.InvariantCulture) + " declared");
            }
            _target.WriteVarUInt32(type.Value);
            WritePayload(payload, offset, count);
            _writtenComponents++;
        }

        public void EndSpawn(ulong enabledBits)
        {
            if (_open != Open.Spawn) throw new InvalidOperationException("EndSpawn without BeginSpawn");
            EnsureAllComponents();
            _target.WriteVarUInt64(enabledBits);
            CloseRecord();
        }

        public void Despawn(in DespawnRecord record)
        {
            EnsureNoOpenRecord();
            CheckSlot(record.Slot, "record.Slot");
            _target.WriteByte((byte)RecordKind.Despawn);
            _target.WriteVarUInt32(record.Slot.Value);
            _target.WriteByte(record.Generation);
            _target.WriteByte(record.DeleteChildrenAsHost ? RecordFormat.DespawnDeleteChildren : (byte)0);
            RecordCount++;
        }

        /// <summary>Starts a STRUCT; exactly <see cref="StructHeader.AddedComponentCount"/> <see cref="Component"/> calls
        /// and <see cref="EndStruct"/> follow.</summary>
        public void BeginStruct(in StructHeader header)
        {
            EnsureNoOpenRecord();
            CheckSlot(header.Slot, "header.Slot");
            if (header.NewTypeSet.Value < 0) throw new ArgumentOutOfRangeException(nameof(header), "negative type-set id");
            CheckComponentCount(header.AddedComponentCount);
            _openStart = _target.Length;
            _target.WriteByte((byte)RecordKind.Struct);
            _target.WriteVarUInt32(header.Slot.Value);
            _target.WriteVarUInt32((uint)header.NewTypeSet.Value);
            _target.WriteVarUInt32((uint)header.AddedComponentCount);
            OpenRecord(Open.Struct, header.AddedComponentCount);
        }

        public void EndStruct()
        {
            if (_open != Open.Struct) throw new InvalidOperationException("EndStruct without BeginStruct");
            EnsureAllComponents();
            CloseRecord();
        }

        /// <summary>A VALUE; a presence-only value carries no payload.</summary>
        public void Value(in ValueHeader header, byte[] payload, int offset, int count)
        {
            EnsureNoOpenRecord();
            CheckSlot(header.Slot, "header.Slot");
            WireFormat.CheckOptionalRange(payload, offset, count, nameof(payload));
            if ((header.Flags & ~RecordFormat.KnownValueFlags) != 0) throw new ArgumentException("unknown value header flags", nameof(header));
            if ((header.Flags & ValueHeaderFlags.Enabled) != 0 && (header.Flags & ValueHeaderFlags.HasEnabledBit) == 0)
            {
                throw new ArgumentException("ValueHeaderFlags.Enabled without HasEnabledBit", nameof(header));
            }
            if ((header.Flags & ValueHeaderFlags.PresenceOnly) != 0 && count != 0)
            {
                throw new ArgumentException("a presence-only value carries no payload", nameof(count));
            }
            _target.WriteByte((byte)RecordKind.Value);
            _target.WriteVarUInt32(header.Slot.Value);
            _target.WriteVarUInt32(header.Type.Value);
            _target.WriteByte((byte)header.Flags);
            WritePayload(payload, offset, count);
            RecordCount++;
        }

        public void Touch(in TouchRecord record)
        {
            EnsureNoOpenRecord();
            CheckSlot(record.Slot, "record.Slot");
            if ((record.Flags & ~RecordFormat.KnownTouchFlags) != 0) throw new ArgumentException("unknown touch flags", nameof(record));
            _target.WriteByte((byte)RecordKind.Touch);
            _target.WriteVarUInt32(record.Slot.Value);
            _target.WriteByte((byte)record.Flags);
            RecordCount++;
        }

        public void Bind(in BindRecord record)
        {
            EnsureNoOpenRecord();
            CheckSlot(record.Slot, "record.Slot");
            if (record.Local.IsNone) throw new ArgumentException("a BIND names a LocalId of 1 or more", nameof(record));
            _target.WriteByte((byte)RecordKind.Bind);
            _target.WriteByte(record.Origin.Value);
            _target.WriteVarUInt32(record.Local.Value);
            _target.WriteVarUInt32(record.Slot.Value);
            _target.WriteByte(record.Generation);
            RecordCount++;
        }

        public void Pose(NetSlot slot, byte[] payload, int offset, int count)
        {
            EnsureNoOpenRecord();
            CheckSlot(slot, nameof(slot));
            WireFormat.CheckOptionalRange(payload, offset, count, nameof(payload));
            _target.WriteByte((byte)RecordKind.Pose);
            _target.WriteVarUInt32(slot.Value);
            WritePayload(payload, offset, count);
            RecordCount++;
        }

        public void Keyed(in DerivedKey key, TypeRank type, byte[] payload, int offset, int count)
        {
            EnsureNoOpenRecord();
            CheckKey(key, nameof(key));
            WireFormat.CheckOptionalRange(payload, offset, count, nameof(payload));
            _target.WriteByte((byte)RecordKind.Keyed);
            NetRefCodec.WriteKey(_target, key);
            _target.WriteVarUInt32(type.Value);
            WritePayload(payload, offset, count);
            RecordCount++;
        }

        public void KeyedDelete(in DerivedKey key)
        {
            EnsureNoOpenRecord();
            CheckKey(key, nameof(key));
            _target.WriteByte((byte)RecordKind.KeyedDelete);
            NetRefCodec.WriteKey(_target, key);
            RecordCount++;
        }

        /// <summary>A ZONE: the cells of the block <paramref name="blockKey"/> (kind <see cref="DerivedKeyKind.Block"/>)
        /// encoded by <see cref="ZoneOverlayCodec.Encode"/>.</summary>
        public void Zone(in DerivedKey blockKey, ushort revision, byte[] rle, int offset, int count)
        {
            EnsureNoOpenRecord();
            if (blockKey.Kind != DerivedKeyKind.Block) throw new ArgumentException("a ZONE record is keyed by a block key", nameof(blockKey));
            WireFormat.CheckOptionalRange(rle, offset, count, nameof(rle));
            _target.WriteByte((byte)RecordKind.Zone);
            NetRefCodec.WriteKey(_target, blockKey);
            _target.WriteVarUInt32(revision);
            WritePayload(rle, offset, count);
            RecordCount++;
        }

        /// <summary>An ICON; a null <see cref="IconRecord.Target"/> is omitted and decodes as null.</summary>
        public void Icon(in IconRecord record)
        {
            EnsureNoOpenRecord();
            if ((byte)record.Op > (byte)IconOp.Event) throw new ArgumentException("undefined icon op", nameof(record));
            CheckRef(record.Owner, "record.Owner");
            CheckRef(record.Target, "record.Target");
            byte op = (byte)record.Op;
            if (!record.Target.IsNull) op |= RecordFormat.IconHasTarget;
            if (record.HasLocation) op |= RecordFormat.IconHasLocation;
            _target.WriteByte((byte)RecordKind.Icon);
            _target.WriteByte(op);
            NetRefCodec.Write(_target, record.Owner);
            WritePrefab(record.Prefab);
            _target.WriteByte(record.Priority);
            _target.WriteByte(record.Layer);
            _target.WriteUInt16(record.Flags);
            if (!record.Target.IsNull) NetRefCodec.Write(_target, record.Target);
            if (record.HasLocation)
            {
                _target.WriteSingle(WireFormat.Finite(record.X));
                _target.WriteSingle(WireFormat.Finite(record.Y));
                _target.WriteSingle(WireFormat.Finite(record.Z));
            }
            RecordCount++;
        }

        /// <summary>A NAME; <paramref name="text"/> is truncated to <see cref="Limits.MaxNameChars"/> characters, null or
        /// empty clears the custom name.</summary>
        public void Name(in NetRef target, string text)
        {
            EnsureNoOpenRecord();
            CheckRef(target, nameof(target));
            string name = ProtocolText.Truncate(text, Limits.MaxNameChars);
            _target.WriteByte((byte)RecordKind.Name);
            NetRefCodec.Write(_target, target);
            _target.WriteString(name, RecordFormat.MaxNameUtf8Bytes);
            RecordCount++;
        }

        /// <summary>A PFB record: a prefab-state payload whose layout belongs to game-state-channels (ARCH §4.10.3).</summary>
        public void PrefabState(byte[] payload, int offset, int count)
        {
            EnsureNoOpenRecord();
            WireFormat.CheckOptionalRange(payload, offset, count, nameof(payload));
            _target.WriteByte((byte)RecordKind.PrefabState);
            WritePayload(payload, offset, count);
            RecordCount++;
        }

        /// <summary>A SYS record: the system's own serialised state, named by the stable id of its entry (ARCH §4.10.1).</summary>
        public void SysState(uint entryStableId, byte[] blob, int offset, int count)
        {
            EnsureNoOpenRecord();
            WireFormat.CheckOptionalRange(blob, offset, count, nameof(blob));
            _target.WriteByte((byte)RecordKind.SysState);
            _target.WriteUInt32(entryStableId);
            WritePayload(blob, offset, count);
            RecordCount++;
        }

        /// <summary>A RESULT; <see cref="RejectReason.None"/> means accepted.</summary>
        public void Result(in ResultRecord record)
        {
            EnsureNoOpenRecord();
            byte status = 0;
            if (record.Reason != RejectReason.None) status |= RecordFormat.ResultRejected;
            if (!record.ConflictSlot.IsNone) status |= RecordFormat.ResultHasConflict;
            _target.WriteByte((byte)RecordKind.Result);
            _target.WriteByte(record.Origin.Value);
            _target.WriteVarUInt32(record.ProposalSeq);
            _target.WriteByte(status);
            _target.WriteVarUInt32((ushort)record.Reason);
            if (!record.ConflictSlot.IsNone) _target.WriteVarUInt32(record.ConflictSlot.Value);
            RecordCount++;
        }

        public void Hash(in HashBeacon beacon)
        {
            EnsureNoOpenRecord();
            if ((byte)beacon.Class > RecordFormat.MaxReplicationClass) throw new ArgumentException("undefined replication class", nameof(beacon));
            if (beacon.Bucket < 0 || beacon.Bucket > Limits.MaxBucket) throw new ArgumentOutOfRangeException(nameof(beacon), "bucket outside 0 ... 16383");
            if (beacon.Count < 0) throw new ArgumentOutOfRangeException(nameof(beacon), "negative member count");
            _target.WriteByte((byte)RecordKind.Hash);
            _target.WriteByte((byte)beacon.Class);
            _target.WriteVarUInt32((uint)beacon.Bucket);
            _target.WriteUInt64(beacon.Sum);
            _target.WriteVarUInt32((uint)beacon.Count);
            RecordCount++;
        }

        public void Event(EventRecordKind kind, byte[] payload, int offset, int count)
        {
            EnsureNoOpenRecord();
            if ((byte)kind > RecordFormat.MaxEventKind) throw new ArgumentException("undefined event kind", nameof(kind));
            WireFormat.CheckOptionalRange(payload, offset, count, nameof(payload));
            _target.WriteByte((byte)RecordKind.Event);
            _target.WriteByte((byte)kind);
            WritePayload(payload, offset, count);
            RecordCount++;
        }

        public void TerrainStamp(byte[] payload, int offset, int count)
        {
            EnsureNoOpenRecord();
            WireFormat.CheckOptionalRange(payload, offset, count, nameof(payload));
            _target.WriteByte((byte)RecordKind.TerrainStamp);
            WritePayload(payload, offset, count);
            RecordCount++;
        }

        /// <summary>A TSETKEY record with the whole registry as of now (the TSET section of a joiner or resync, ARCH §3.6).</summary>
        public void TypeSetKeyframe(ITypeSetRegistry registry)
        {
            EnsureNoOpenRecord();
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            if (_keyframe == null) _keyframe = new ByteWriter(1024);
            _keyframe.Reset();
            registry.WriteKeyframe(_keyframe);
            _target.WriteByte((byte)RecordKind.TypeSetKeyframe);
            WritePayload(_keyframe.Buffer, 0, _keyframe.Length);
            RecordCount++;
        }

        private void WritePayload(byte[] payload, int offset, int count)
        {
            _target.WriteVarUInt32((uint)count);
            if (count > 0) _target.WriteBytes(payload, offset, count);
        }

        private void WritePrefab(PrefabRank prefab) => _target.WriteVarUInt32(unchecked((uint)(prefab.Value + 1)));

        private void OpenRecord(Open kind, int declared)
        {
            _open = kind;
            _declaredComponents = declared;
            _writtenComponents = 0;
        }

        private void CloseRecord()
        {
            _open = Open.None;
            RecordCount++;
        }

        private void EnsureNoOpenRecord()
        {
            if (_open != Open.None) throw new InvalidOperationException("a " + (_open == Open.Spawn ? "SPAWN" : "STRUCT") + " record is still open");
        }

        private void EnsureAllComponents()
        {
            if (_writtenComponents != _declaredComponents)
            {
                throw new InvalidOperationException(_writtenComponents.ToString(CultureInfo.InvariantCulture) + " of "
                                                    + _declaredComponents.ToString(CultureInfo.InvariantCulture) + " declared components written");
            }
        }

        private static void CheckSlot(NetSlot slot, string name)
        {
            if (slot.IsNone) throw new ArgumentException("a record addresses a slot of 1 or more", name);
        }

        private static void CheckComponentCount(int count)
        {
            if (count < 0 || count > Limits.MaxComponentsPerRecord)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, "0 ... " + Limits.MaxComponentsPerRecord.ToString(CultureInfo.InvariantCulture) + " components");
            }
        }

        private static void CheckKey(in DerivedKey key, string name)
        {
            if (!DerivedKey.IsValidKind((byte)key.Kind)) throw new ArgumentException("the derived key has no valid kind", name);
        }

        private static void CheckRef(in NetRef value, string name)
        {
            if (value.Kind > NetRefKind.Derived) throw new ArgumentException("undefined NetRef kind", name);
        }
    }
}
