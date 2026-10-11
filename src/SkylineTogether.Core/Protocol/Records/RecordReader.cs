using System;
using System.Globalization;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// Reads the records of one epoch section (formats: <see cref="RecordWriter"/>). Usage: <see cref="Reset"/> on the
    /// section body, then <see cref="Next"/> and the matching <c>Read*</c> method per record. A record that is not read
    /// (or only partly read, such as a SPAWN whose components were not all visited) is skipped by the next
    /// <see cref="Next"/>, with full validation, so a consumer can ignore records it does not act on (RESULT and BIND of
    /// other origins, SYS entries it has not enabled).
    /// <para>
    /// Validation is syntactic and complete: defined record kinds, strict varints, slots within 24 bits and non-zero
    /// where a record addresses one, defined flag bits and enum values, counts within their limits and the remaining
    /// bytes, payload lengths inside the section, canonical encodings (ascending type sets, presence-only values without
    /// payload, an absent rather than a null ICON target, a RESULT status that matches its reason), finite ICON
    /// coordinates and NAME texts within <see cref="Limits.MaxNameChars"/>. Every violation throws
    /// <see cref="DecodeException"/>. Semantic checks (a type rank in the catalog, a known type set, a bound slot) are the
    /// consumer's. Payload offsets are absolute offsets into the buffer given to <see cref="Reset"/>.
    /// </para>
    /// <para>
    /// After a <see cref="DecodeException"/> the section is abandoned: the reader does not resynchronise inside a
    /// malformed section, and every further <see cref="Next"/> throws again until <see cref="Reset"/>. (An exception of
    /// the registry inside <see cref="ReadTypeSetKeyframe"/> is the exception: that record was fully consumed first.)
    /// </para>
    /// <para>Not thread-safe; one instance per thread.</para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class RecordReader
    {
        // Reading: a decode step started and has not finished. It is still set when that step threw, so the reader
        // refuses to continue from the middle of a record (every later call throws) instead of misparsing the rest.
        private enum State : byte { None, Fresh, SpawnComponents, SpawnBits, StructComponents, Done, Reading }

        private readonly ByteReader _r = new ByteReader();
        private readonly ByteReader _keyframeReader = new ByteReader();
        private byte[] _buffer = Array.Empty<byte>();
        private int _offset;
        private State _state;
        private RecordKind _kind;
        private int _componentsLeft;
        private int _recordOffset;
        private int _records;

        /// <summary>The buffer given to <see cref="Reset"/>.</summary>
        public byte[] Buffer => _buffer;

        /// <summary>The kind of the current record (valid after <see cref="Next"/> returned true).</summary>
        public RecordKind Kind => _kind;

        /// <summary>Absolute offset of the current record's kind byte.</summary>
        public int RecordOffset => _recordOffset;

        /// <summary>Absolute offset of the reading position.</summary>
        public int Position => _offset + _r.Position;

        /// <summary>Records returned by <see cref="Next"/> since <see cref="Reset"/>.</summary>
        public int RecordsRead => _records;

        /// <summary>Starts reading the records in <c>buffer[offset .. offset+count-1]</c>.</summary>
        public void Reset(byte[] buffer, int offset, int count)
        {
            WireFormat.CheckRange(buffer, offset, count, nameof(buffer));
            _r.Reset(buffer, offset, count);
            _buffer = buffer;
            _offset = offset;
            _state = State.None;
            _kind = default;
            _componentsLeft = 0;
            _recordOffset = offset;
            _records = 0;
        }

        /// <summary>Advances to the next record, skipping what is left of the current one; false at the end of the section.</summary>
        public bool Next(out RecordKind kind)
        {
            FinishCurrent();
            if (_r.IsAtEnd)
            {
                _state = State.None;
                kind = default;
                return false;
            }
            _recordOffset = Position;
            byte kindByte = _r.ReadByte();
            if (!RecordFormat.IsDefinedKind(kindByte))
            {
                _state = State.Reading;
                throw new DecodeException("record at offset " + (_recordOffset - _offset).ToString(CultureInfo.InvariantCulture)
                                          + ": unknown record kind " + kindByte.ToString(CultureInfo.InvariantCulture));
            }
            _kind = (RecordKind)kindByte;
            _state = State.Fresh;
            _records++;
            kind = _kind;
            return true;
        }

        public TypeSetDefinition ReadTypeSetDef()
        {
            Expect(RecordKind.TypeSetDef);
            uint id = _r.ReadVarUInt32();
            if (id == 0 || id > int.MaxValue) throw Malformed("type-set id " + WireFormat.Format(id) + " outside 1 ... int.MaxValue");
            int count = ReadCount(Limits.MaxComponentsPerRecord, 1, "type count");
            var types = new TypeRank[count];
            int previous = -1;
            for (int i = 0; i < count; i++)
            {
                int rank = ReadTypeRank();
                if (rank <= previous) throw Malformed("type ranks of a type set must be strictly ascending");
                types[i] = new TypeRank((ushort)rank);
                previous = rank;
            }
            _state = State.Done;
            return new TypeSetDefinition { Id = new TypeSetId((int)id), Types = types };
        }

        /// <summary>The SPAWN header; then <see cref="NextComponent"/> until false, then <see cref="ReadEnabledBits"/>.</summary>
        public SpawnHeader ReadSpawnHeader()
        {
            Expect(RecordKind.Spawn);
            var header = new SpawnHeader
            {
                Slot = ReadSlot(false, "spawn slot"),
                Generation = _r.ReadByte(),
                Prefab = ReadPrefab("spawn prefab"),
                TypeSet = ReadTypeSetId(),
            };
            var flags = (SpawnFlags)_r.ReadByte();
            if ((flags & ~RecordFormat.KnownSpawnFlags) != 0) throw Malformed("unknown spawn flags 0x" + ((byte)flags).ToString("X2", CultureInfo.InvariantCulture));
            header.Flags = flags;
            if ((flags & SpawnFlags.HasUpdateFrame) != 0) header.UpdateFrame = _r.ReadByte();
            header.ComponentCount = ReadCount(Limits.MaxComponentsPerRecord, 2, "component count");
            _componentsLeft = header.ComponentCount;
            _state = State.SpawnComponents;
            return header;
        }

        /// <summary>The next component payload of the current SPAWN or STRUCT; false after the last one.</summary>
        public bool NextComponent(out TypeRank type, out int payloadOffset, out int payloadCount)
        {
            if (_state != State.SpawnComponents && _state != State.StructComponents)
            {
                throw new InvalidOperationException("NextComponent outside the components of a SPAWN or STRUCT");
            }
            if (_componentsLeft == 0)
            {
                _state = _state == State.SpawnComponents ? State.SpawnBits : State.Done;
                type = default;
                payloadOffset = 0;
                payloadCount = 0;
                return false;
            }
            State resume = _state;
            _state = State.Reading;
            type = new TypeRank((ushort)ReadTypeRank());
            ReadPayload(out payloadOffset, out payloadCount);
            _componentsLeft--;
            _state = resume;
            return true;
        }

        /// <summary>The enabled bits that end a SPAWN; components not visited are skipped first.</summary>
        public ulong ReadEnabledBits()
        {
            if (_state == State.SpawnComponents)
            {
                while (NextComponent(out _, out _, out _))
                {
                }
            }
            if (_state != State.SpawnBits) throw new InvalidOperationException("ReadEnabledBits outside a SPAWN");
            _state = State.Reading;
            ulong bits = _r.ReadVarUInt64();
            _state = State.Done;
            return bits;
        }

        public DespawnRecord ReadDespawn()
        {
            Expect(RecordKind.Despawn);
            var record = new DespawnRecord { Slot = ReadSlot(false, "despawn slot"), Generation = _r.ReadByte() };
            byte flags = _r.ReadByte();
            if ((flags & ~RecordFormat.DespawnDeleteChildren) != 0) throw Malformed("unknown despawn flags 0x" + flags.ToString("X2", CultureInfo.InvariantCulture));
            record.DeleteChildrenAsHost = flags != 0;
            _state = State.Done;
            return record;
        }

        /// <summary>The STRUCT header; then <see cref="NextComponent"/> until false for the payloads of the added types.</summary>
        public StructHeader ReadStructHeader()
        {
            Expect(RecordKind.Struct);
            var header = new StructHeader
            {
                Slot = ReadSlot(false, "struct slot"),
                NewTypeSet = ReadTypeSetId(),
                AddedComponentCount = ReadCount(Limits.MaxComponentsPerRecord, 2, "added component count")
            };
            _componentsLeft = header.AddedComponentCount;
            _state = State.StructComponents;
            return header;
        }

        public ValueHeader ReadValue(out int payloadOffset, out int payloadCount)
        {
            Expect(RecordKind.Value);
            var header = new ValueHeader { Slot = ReadSlot(false, "value slot"), Type = new TypeRank((ushort)ReadTypeRank()) };
            var flags = (ValueHeaderFlags)_r.ReadByte();
            if ((flags & ~RecordFormat.KnownValueFlags) != 0) throw Malformed("unknown value flags 0x" + ((byte)flags).ToString("X2", CultureInfo.InvariantCulture));
            if ((flags & ValueHeaderFlags.Enabled) != 0 && (flags & ValueHeaderFlags.HasEnabledBit) == 0) throw Malformed("value: Enabled without HasEnabledBit");
            header.Flags = flags;
            ReadPayload(out payloadOffset, out payloadCount);
            if ((flags & ValueHeaderFlags.PresenceOnly) != 0 && payloadCount != 0) throw Malformed("presence-only value with a payload");
            _state = State.Done;
            return header;
        }

        public TouchRecord ReadTouch()
        {
            Expect(RecordKind.Touch);
            var record = new TouchRecord { Slot = ReadSlot(false, "touch slot") };
            var flags = (TouchFlags)_r.ReadByte();
            if ((flags & ~RecordFormat.KnownTouchFlags) != 0) throw Malformed("unknown touch flags 0x" + ((byte)flags).ToString("X2", CultureInfo.InvariantCulture));
            record.Flags = flags;
            _state = State.Done;
            return record;
        }

        public BindRecord ReadBind()
        {
            Expect(RecordKind.Bind);
            var record = new BindRecord { Origin = new PeerId(_r.ReadByte()) };
            uint local = _r.ReadVarUInt32();
            if (local == 0) throw Malformed("bind of LocalId 0");
            record.Local = new LocalId(local);
            record.Slot = ReadSlot(false, "bind slot");
            record.Generation = _r.ReadByte();
            _state = State.Done;
            return record;
        }

        public NetSlot ReadPose(out int payloadOffset, out int payloadCount)
        {
            Expect(RecordKind.Pose);
            NetSlot slot = ReadSlot(false, "pose slot");
            ReadPayload(out payloadOffset, out payloadCount);
            _state = State.Done;
            return slot;
        }

        /// <summary>A KEYED or KEYED_DEL record; for a deletion <paramref name="isDelete"/> is true and the type and payload are empty.</summary>
        public DerivedKey ReadKeyed(out TypeRank type, out int payloadOffset, out int payloadCount, out bool isDelete)
        {
            if (_state != State.Fresh || (_kind != RecordKind.Keyed && _kind != RecordKind.KeyedDelete))
            {
                throw new InvalidOperationException("ReadKeyed on a " + DescribeCurrent());
            }
            _state = State.Reading;
            DerivedKey key = NetRefCodec.ReadKey(_r);
            isDelete = _kind == RecordKind.KeyedDelete;
            if (isDelete)
            {
                type = default;
                payloadOffset = 0;
                payloadCount = 0;
            }
            else
            {
                type = new TypeRank((ushort)ReadTypeRank());
                ReadPayload(out payloadOffset, out payloadCount);
            }
            _state = State.Done;
            return key;
        }

        public DerivedKey ReadZone(out ushort revision, out int rleOffset, out int rleCount)
        {
            Expect(RecordKind.Zone);
            DerivedKey key = NetRefCodec.ReadKey(_r);
            if (key.Kind != DerivedKeyKind.Block) throw Malformed("zone record keyed by a " + key.Kind + " key");
            uint value = _r.ReadVarUInt32();
            if (value > ushort.MaxValue) throw Malformed("zone revision " + WireFormat.Format(value) + " exceeds 65535");
            revision = (ushort)value;
            ReadPayload(out rleOffset, out rleCount);
            _state = State.Done;
            return key;
        }

        public IconRecord ReadIcon()
        {
            Expect(RecordKind.Icon);
            byte op = _r.ReadByte();
            if ((op & ~(RecordFormat.IconOpMask | RecordFormat.IconHasTarget | RecordFormat.IconHasLocation)) != 0)
            {
                throw Malformed("unknown icon op bits 0x" + op.ToString("X2", CultureInfo.InvariantCulture));
            }
            var record = new IconRecord
            {
                Op = (IconOp)(op & RecordFormat.IconOpMask),
                Owner = NetRefCodec.Read(_r),
                Prefab = ReadPrefab("icon prefab"),
                Priority = _r.ReadByte(),
                Layer = _r.ReadByte(),
                Flags = _r.ReadUInt16(),
                Target = NetRef.Null
            };
            if ((op & RecordFormat.IconHasTarget) != 0)
            {
                record.Target = NetRefCodec.Read(_r);
                if (record.Target.IsNull) throw Malformed("icon target encoded as null (an absent target is not written)");
            }
            if ((op & RecordFormat.IconHasLocation) != 0)
            {
                record.HasLocation = true;
                record.X = ReadFinite("icon x");
                record.Y = ReadFinite("icon y");
                record.Z = ReadFinite("icon z");
            }
            _state = State.Done;
            return record;
        }

        /// <summary>A NAME: the target and its custom name, control characters stripped (empty = clear).</summary>
        public NetRef ReadName(out string text)
        {
            Expect(RecordKind.Name);
            NetRef target = NetRefCodec.Read(_r);
            string raw = _r.ReadString(RecordFormat.MaxNameUtf8Bytes);
            if (raw.Length > Limits.MaxNameChars) throw Malformed("name of " + raw.Length.ToString(CultureInfo.InvariantCulture) + " characters exceeds 64");
            text = ProtocolText.Sanitize(raw);
            _state = State.Done;
            return target;
        }

        /// <summary>A PFB record's payload (layout owned by game-state-channels).</summary>
        public void ReadPrefabState(out int payloadOffset, out int payloadCount)
        {
            Expect(RecordKind.PrefabState);
            ReadPayload(out payloadOffset, out payloadCount);
            _state = State.Done;
        }

        /// <summary>A SYS record: returns the entry's stable id; the blob is the system's own serialised state.</summary>
        public uint ReadSysState(out int blobOffset, out int blobCount)
        {
            Expect(RecordKind.SysState);
            uint entry = _r.ReadUInt32();
            ReadPayload(out blobOffset, out blobCount);
            _state = State.Done;
            return entry;
        }

        public ResultRecord ReadResult()
        {
            Expect(RecordKind.Result);
            var record = new ResultRecord { Origin = new PeerId(_r.ReadByte()), ProposalSeq = _r.ReadVarUInt32() };
            byte status = _r.ReadByte();
            if ((status & ~(RecordFormat.ResultRejected | RecordFormat.ResultHasConflict)) != 0)
            {
                throw Malformed("unknown result status bits 0x" + status.ToString("X2", CultureInfo.InvariantCulture));
            }
            uint reason = _r.ReadVarUInt32();
            if (reason > ushort.MaxValue) throw Malformed("result reason " + WireFormat.Format(reason) + " exceeds 65535");
            if (((status & RecordFormat.ResultRejected) != 0) != (reason != 0))
            {
                throw Malformed("result status " + status.ToString(CultureInfo.InvariantCulture) + " contradicts reason " + WireFormat.Format(reason));
            }
            record.Reason = (RejectReason)reason;
            record.ConflictSlot = (status & RecordFormat.ResultHasConflict) != 0 ? ReadSlot(false, "result conflict slot") : NetSlot.None;
            _state = State.Done;
            return record;
        }

        public HashBeacon ReadHash()
        {
            Expect(RecordKind.Hash);
            byte cls = _r.ReadByte();
            if (cls > RecordFormat.MaxReplicationClass) throw Malformed("undefined replication class " + cls.ToString(CultureInfo.InvariantCulture));
            uint bucket = _r.ReadVarUInt32();
            if (bucket > Limits.MaxBucket) throw Malformed("hash bucket " + WireFormat.Format(bucket) + " exceeds 16383");
            ulong sum = _r.ReadUInt64();
            uint count = _r.ReadVarUInt32();
            if (count > int.MaxValue) throw Malformed("hash member count " + WireFormat.Format(count) + " exceeds int.MaxValue");
            _state = State.Done;
            return new HashBeacon { Class = (ReplicationClass)cls, Bucket = (int)bucket, Sum = sum, Count = (int)count };
        }

        public EventRecordKind ReadEvent(out int payloadOffset, out int payloadCount)
        {
            Expect(RecordKind.Event);
            byte kind = _r.ReadByte();
            if (kind > RecordFormat.MaxEventKind) throw Malformed("undefined event kind " + kind.ToString(CultureInfo.InvariantCulture));
            ReadPayload(out payloadOffset, out payloadCount);
            _state = State.Done;
            return (EventRecordKind)kind;
        }

        public void ReadTerrainStamp(out int payloadOffset, out int payloadCount)
        {
            Expect(RecordKind.TerrainStamp);
            ReadPayload(out payloadOffset, out payloadCount);
            _state = State.Done;
        }

        /// <summary>
        /// Loads a TSETKEY record into <paramref name="into"/> (<see cref="ITypeSetRegistry.ReadKeyframe"/>), which must
        /// consume the keyframe exactly. A contradiction with a known definition, a malformed keyframe or any failure of
        /// the registry throws <see cref="DecodeException"/>.
        /// </summary>
        public void ReadTypeSetKeyframe(ITypeSetRegistry into)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            Expect(RecordKind.TypeSetKeyframe);
            ReadPayload(out int offset, out int count);
            _state = State.Done;
            _keyframeReader.Reset(_buffer, offset, count);
            try
            {
                into.ReadKeyframe(_keyframeReader);
            }
            catch (DecodeException)
            {
                throw;
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                // ReadKeyframe reports malformed input as DecodeException; nothing else may escape a decoder
                throw new DecodeException("type-set keyframe rejected by the registry: " + e.Message, e);
            }
            if (!_keyframeReader.IsAtEnd)
            {
                throw Malformed(_keyframeReader.Remaining.ToString(CultureInfo.InvariantCulture) + " unread byte(s) after the type-set keyframe");
            }
        }

        // ---------------------------------------------------------------------------------------------------------

        /// <summary>Skips the rest of the current record with full validation (allocation-free).</summary>
        private void FinishCurrent()
        {
            switch (_state)
            {
                case State.None:
                case State.Done:
                    return;
                case State.Reading:
                    throw new DecodeException("record " + _kind + " at offset " + (_recordOffset - _offset).ToString(CultureInfo.InvariantCulture)
                                              + " failed to decode; the rest of the section cannot be read");
                case State.SpawnComponents:
                case State.SpawnBits:
                    ReadEnabledBits();
                    return;
                case State.StructComponents:
                    while (NextComponent(out _, out _, out _))
                    {
                    }
                    return;
            }

            // a fresh record: parse it as the consumer would
            switch (_kind)
            {
                case RecordKind.TypeSetDef: SkipTypeSetDef(); break;
                case RecordKind.Spawn: ReadSpawnHeader(); ReadEnabledBits(); break;
                case RecordKind.Despawn: ReadDespawn(); break;
                case RecordKind.Struct: ReadStructHeader(); FinishCurrent(); break;
                case RecordKind.Value: ReadValue(out _, out _); break;
                case RecordKind.Touch: ReadTouch(); break;
                case RecordKind.Bind: ReadBind(); break;
                case RecordKind.Pose: ReadPose(out _, out _); break;
                case RecordKind.Keyed:
                case RecordKind.KeyedDelete: ReadKeyed(out _, out _, out _, out _); break;
                case RecordKind.Zone: ReadZone(out _, out _, out _); break;
                case RecordKind.Icon: ReadIcon(); break;
                case RecordKind.Name: SkipName(); break;
                case RecordKind.PrefabState: ReadPrefabState(out _, out _); break;
                case RecordKind.SysState: ReadSysState(out _, out _); break;
                case RecordKind.Result: ReadResult(); break;
                case RecordKind.Hash: ReadHash(); break;
                case RecordKind.Event: ReadEvent(out _, out _); break;
                case RecordKind.TerrainStamp: ReadTerrainStamp(out _, out _); break;
                case RecordKind.TypeSetKeyframe:
                    Expect(RecordKind.TypeSetKeyframe);
                    ReadPayload(out _, out _);
                    _state = State.Done;
                    break;
                default:
                    throw Malformed("unknown record kind " + ((byte)_kind).ToString(CultureInfo.InvariantCulture));
            }
        }

        private void SkipTypeSetDef()
        {
            _state = State.Reading;
            uint id = _r.ReadVarUInt32();
            if (id == 0 || id > int.MaxValue) throw Malformed("type-set id " + WireFormat.Format(id) + " outside 1 ... int.MaxValue");
            int count = ReadCount(Limits.MaxComponentsPerRecord, 1, "type count");
            int previous = -1;
            for (int i = 0; i < count; i++)
            {
                int rank = ReadTypeRank();
                if (rank <= previous) throw Malformed("type ranks of a type set must be strictly ascending");
                previous = rank;
            }
            _state = State.Done;
        }

        private void SkipName()
        {
            _state = State.Reading;
            NetRefCodec.Read(_r);
            uint length = _r.ReadVarUInt32();
            if (length > RecordFormat.MaxNameUtf8Bytes) throw Malformed("name of " + WireFormat.Format(length) + " bytes exceeds the limit");
            _r.Skip((int)length);
            _state = State.Done;
        }

        private void Expect(RecordKind kind)
        {
            if (_state != State.Fresh || _kind != kind) throw new InvalidOperationException("Read" + kind + " on a " + DescribeCurrent());
            _state = State.Reading;
        }

        private string DescribeCurrent()
        {
            return _state == State.None ? "reader without a current record" : _kind + " record in state " + _state;
        }

        private NetSlot ReadSlot(bool allowNone, string what)
        {
            uint value = _r.ReadVarUInt32();
            if (value > NetSlot.MaxValue) throw Malformed(what + " " + WireFormat.Format(value) + " exceeds 0xFFFFFF");
            if (value == 0 && !allowNone) throw Malformed(what + " is 0");
            return new NetSlot(value);
        }

        private PrefabRank ReadPrefab(string what)
        {
            uint plusOne = _r.ReadVarUInt32();
            if (plusOne > 0x80000000u) throw Malformed(what + " rank exceeds int.MaxValue");
            return plusOne == 0 ? PrefabRank.None : new PrefabRank((int)(plusOne - 1));
        }

        private TypeSetId ReadTypeSetId()
        {
            uint value = _r.ReadVarUInt32();
            if (value > int.MaxValue) throw Malformed("type-set id " + WireFormat.Format(value) + " exceeds int.MaxValue");
            return new TypeSetId((int)value);
        }

        private int ReadTypeRank()
        {
            uint value = _r.ReadVarUInt32();
            if (value > ushort.MaxValue) throw Malformed("type rank " + WireFormat.Format(value) + " exceeds 65535");
            return (int)value;
        }

        private int ReadCount(int max, int minBytesEach, string what)
        {
            uint value = _r.ReadVarUInt32();
            if (value > (uint)max) throw Malformed(what + " " + WireFormat.Format(value) + " exceeds " + max.ToString(CultureInfo.InvariantCulture));
            if ((long)value * minBytesEach > _r.Remaining) throw Malformed(what + " " + WireFormat.Format(value) + " exceeds the remaining bytes");
            return (int)value;
        }

        private void ReadPayload(out int payloadOffset, out int payloadCount)
        {
            uint length = _r.ReadVarUInt32();
            if (length > (uint)_r.Remaining)
            {
                throw Malformed("payload of " + WireFormat.Format(length) + " bytes overruns the section (" + _r.Remaining.ToString(CultureInfo.InvariantCulture) + " left)");
            }
            payloadOffset = Position;
            payloadCount = (int)length;
            _r.Skip(payloadCount);
        }

        private float ReadFinite(string what)
        {
            float value = _r.ReadSingle();
            if (!WireFormat.IsFinite(value)) throw Malformed(what + " is not finite");
            return value;
        }

        private DecodeException Malformed(string what)
        {
            return new DecodeException("record " + (_state == State.None ? "?" : _kind.ToString()) + " at offset "
                                       + (_recordOffset - _offset).ToString(CultureInfo.InvariantCulture) + ": " + what);
        }
    }
}
