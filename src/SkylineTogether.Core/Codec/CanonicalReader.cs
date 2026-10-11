using System;
using System.Globalization;

namespace SkylineTogether.Core.Codec
{
    /// <summary>
    /// Reads canonical bytes written by <see cref="CanonicalWriter"/>: the mirror of every member of the game's
    /// <c>IReader</c> (<c>Colossal.Serialization.Entities/IReader.cs</c>) in Core types. The game's <c>NetReader</c>
    /// forwards each <c>IReader</c> member to one method here.
    /// <para>
    /// <b>Bounded.</b> <see cref="Open"/> confines the reader to exactly one record; every read checks the bound first,
    /// so the reader never touches a byte outside the record, and every overrun throws <see cref="DecodeException"/>
    /// (ARCH §4.3.1). Inside a block the bound is the block's end: a value that would cross it is an overrun too,
    /// whereas the stock reader would read on into the next data (<c>BinaryReader.cs</c>).
    /// </para>
    /// <para>
    /// <b>Strict.</b> Canonical bytes are hashed, so exactly one encoding per value is accepted: booleans 0/1, unused
    /// bits of bool2/3/4 zero, chars in their shortest 1-3 byte form, non-negative sizes and counts that fit the
    /// remaining bytes, canonical <see cref="NetRef"/>s. Data errors throw <see cref="DecodeException"/> and nothing
    /// else; misuse by the caller (an unbalanced <see cref="End"/>, a too small destination array) throws
    /// <see cref="InvalidOperationException"/> or an <see cref="ArgumentException"/>.
    /// </para>
    /// <para>
    /// <b>Failures.</b> Every member validates its whole value before it consumes anything: fixed-size members check the
    /// bound first, strings, counts and blocks validate their prefix (and a string all of its chars), and a reference is
    /// decoded completely and resolved by the seam before the cursor moves (an exception from the seam also leaves the
    /// cursor and the reference counters unchanged). A failed read therefore leaves the cursor where it was. The only
    /// exception is <see cref="ReadEntityArray"/>, which reads element by element through the seam: after a failure
    /// inside the array the position and the reference counters are unspecified. Either way a
    /// <see cref="DecodeException"/> means the record is malformed and must be abandoned (decode-then-commit, ARCH §4.7.4).
    /// </para>
    /// <para>
    /// <b>References.</b> Entities decode through the <see cref="IEntityRefReader"/> seam. A reference that does not
    /// resolve yet decodes to whatever the seam returns (the null entity or a stand-in) and is recorded in
    /// <see cref="HadMissingReference"/>/<see cref="FirstMissing"/>, so the applier can park the record and apply it
    /// again once the slot is bound (ARCH §4.7.5).
    /// </para>
    /// <para>Forwarding of every <c>IReader</c> member (game-ecs-schema's <c>NetReader</c>, one line each):</para>
    /// <code>
    /// Begin() / Begin(out size) / End(b) / Skip(size)      Begin() / Begin(out size) / End(token) / Skip(size)
    /// Read(out bool|byte|sbyte|short|ushort|int|uint)      ReadBool / ReadByte / ReadSByte / ReadShort / ReadUShort / ReadInt / ReadUInt
    /// Read(out long|ulong|char|float|double|string)        ReadLong / ReadULong / ReadChar / ReadFloat / ReadDouble / ReadString
    /// Read(out float2 ... uint4, bool2|bool3|bool4)        ReadFloat2(out x, out y) ... ReadUInt4 / ReadBool2 ... ReadBool4
    /// Read(out quaternion|Color|Color32|Hash128)           ReadQuaternion / ReadColor / ReadColor32 / ReadHash128 → new Colossal.Hash128(new uint4(a, b, c, d))
    /// Read(out Bezier4x3)                                  ReadBezier4x3(reused float[12]) → a, b, c, d
    /// Read(out Entity)                                     ReadEntity(out index, out version) (also for ignoreVersion writes)
    /// Read(NativeArray&lt;Entity&gt;)                     ReadEntityArray(indices, versions, length)
    /// Read(NativeList&lt;Entity&gt;)                      n = ReadCount(1); resize; ReadEntityArray(..., n)
    /// Read(NativeList&lt;int&gt;)                         n = ReadCount(4); resize; ReadIntArray(..., n)
    /// Read(NativeArray&lt;int|int2|ushort|byte|float4|float2&gt;)   ReadIntArray / ReadInt2Array / ReadUShortArray / ReadByteArray / ReadFloat4Array / ReadFloat2Array
    /// Read(NativeArray&lt;byte&gt;, stride)               ReadByteArray(bytes, length, stride)
    /// Read&lt;T&gt;(out T) / Read&lt;T&gt;(T) / Read&lt;T&gt;(NativeArray&lt;T&gt;)   default + Deserialize(this reader), element by element (no codec call)
    /// buffers of ISerializable elements (codec layer)      n = ReadCount(); decode n elements into scratch; then check IsRecordConsumed
    /// </code>
    /// An instance is not thread-safe; use one per thread. The class has no thread affinity of its own: an instance has
    /// that of its entity seam (the game's <c>EntityRefBridge</c> is main-thread-only, a fake seam is not).
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class CanonicalReader
    {
        /// <summary>Strings up to this many chars are decoded into a reused scratch array.</summary>
        private const int MaxRetainedChars = 256;

        private readonly IEntityRefReader _entities;
        private byte[] _buffer = Array.Empty<byte>();
        private int _start;      // absolute offset of the record
        private int _end;        // absolute end of the record
        private int _limit;      // absolute end of the innermost open block, else _end
        private int _pos;        // absolute cursor
        private int[] _outerLimits = new int[8];   // per open block: the bound of the enclosing level
        private int _openCount;
        private char[] _chars = new char[32];
        private bool _hadMissing;
        private NetSlot _firstMissing;
        private int _missingCount;
        private int _referenceCount;
        private int _derivedCount;

        public CanonicalReader(IEntityRefReader entities)
        {
            _entities = entities ?? throw new ArgumentNullException(nameof(entities));
        }

        /// <summary>The entity seam every <see cref="ReadEntity"/> goes through.</summary>
        public IEntityRefReader Entities => _entities;

        /// <summary>
        /// Confines the reader to the record <c>buffer[offset .. offset+length-1]</c> with the cursor at its start, and
        /// resets the open blocks, the missing-reference state and the reference counters. The buffer is not copied:
        /// it must stay unchanged while the record is read.
        /// </summary>
        public void Open(byte[] buffer, int offset, int length)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || length < 0 || offset > buffer.Length - length)
            {
                throw new ArgumentOutOfRangeException(nameof(length), "the record lies outside the buffer");
            }
            _buffer = buffer;
            _start = offset;
            _end = offset + length;
            _limit = _end;
            _pos = offset;
            _openCount = 0;
            _hadMissing = false;
            _firstMissing = NetSlot.None;
            _missingCount = 0;
            _referenceCount = 0;
            _derivedCount = 0;
        }

        /// <summary>
        /// Drops the reference to the current buffer and leaves the reader on an empty record (every read then throws
        /// <see cref="DecodeException"/>). Call it before a pooled buffer goes back to <c>BufferPool</c>, so a stale reader
        /// can never decode bytes that belong to the buffer's next user.
        /// </summary>
        public void Close()
        {
            Open(Array.Empty<byte>(), 0, 0);
        }

        /// <summary>Cursor position relative to the start of the record.</summary>
        public int Position => _pos - _start;

        /// <summary>Length of the record given to <see cref="Open"/>.</summary>
        public int Length => _end - _start;

        /// <summary>Bytes left before the current bound: the end of the innermost open block, else the end of the record.</summary>
        public int Remaining => _limit - _pos;

        /// <summary>True when <see cref="Remaining"/> is 0.</summary>
        public bool IsAtEnd => _pos >= _limit;

        /// <summary>Bytes left before the end of the record, ignoring open blocks.</summary>
        public int RecordRemaining => _end - _pos;

        /// <summary>
        /// The whole-record check of the applier (ARCH §4.7.4): every byte of the record was consumed and no block is
        /// left open.
        /// </summary>
        public bool IsRecordConsumed => _openCount == 0 && _pos == _end;

        /// <summary>Blocks begun and not yet ended since <see cref="Open"/>.</summary>
        public int OpenBlockCount => _openCount;

        /// <summary>True when a reference of the current record did not resolve (the seam reported it missing).</summary>
        public bool HadMissingReference => _hadMissing;

        /// <summary>
        /// The slot to wait for: the missing slot of the first missing reference of the current record that named one;
        /// <see cref="NetSlot.None"/> when no missing reference named a slot.
        /// </summary>
        public NetSlot FirstMissing => _firstMissing;

        /// <summary>Missing references in the current record.</summary>
        public int MissingReferenceCount => _missingCount;

        /// <summary>Entity references decoded through the seam in the current record (null references included).</summary>
        public int ReferenceCount => _referenceCount;

        /// <summary>Entity references of the current record that carried a derived key (received-value cache, ARCH §3.7).</summary>
        public int DerivedReferenceCount => _derivedCount;

        // ---------------------------------------------------------------- blocks

        /// <summary>Opens a size-prefixed block (<c>IReader.Begin()</c>).</summary>
        public BlockToken Begin()
        {
            return Begin(out _);
        }

        /// <summary>
        /// Opens a size-prefixed block and returns its payload size (<c>IReader.Begin(out int size)</c>,
        /// <c>BinaryReader.cs:36-40</c>). Until the matching <see cref="End"/>, reads are bounded by the block's end.
        /// A negative size or one beyond the remaining bytes throws <see cref="DecodeException"/>.
        /// </summary>
        public BlockToken Begin(out int size)
        {
            int at = _pos;
            int declared = unchecked((int)PeekUInt32Core());
            int payload = at + CanonicalWriter.BlockHeaderBytes;
            if (declared < 0)
            {
                throw Invalid("negative block size " + declared.ToString(CultureInfo.InvariantCulture), at);
            }
            if (declared > _limit - payload)
            {
                throw Invalid("block of " + declared.ToString(CultureInfo.InvariantCulture) + " bytes exceeds the "
                              + (_limit - payload).ToString(CultureInfo.InvariantCulture) + " bytes remaining", at);
            }
            if (_openCount == _outerLimits.Length) Array.Resize(ref _outerLimits, _outerLimits.Length * 2);
            _outerLimits[_openCount++] = _limit;
            _pos = payload;
            _limit = payload + declared;
            size = declared;
            return new BlockToken { Position = _limit - _start };
        }

        /// <summary>
        /// Closes the innermost open block. Unread bytes of the block are skipped and the result is false, exactly as
        /// <c>BinaryReader.End</c> does (<c>BinaryReader.cs:42-50</c>); true when the block was consumed completely.
        /// Closing anything but the innermost open block is a programming error (<see cref="InvalidOperationException"/>).
        /// </summary>
        public bool End(BlockToken block)
        {
            if (_openCount == 0)
            {
                throw new InvalidOperationException("End(" + block.Position.ToString(CultureInfo.InvariantCulture) + ") without an open block");
            }
            int blockEnd = _limit - _start;
            if (block.Position != blockEnd)
            {
                throw new InvalidOperationException("End(" + block.Position.ToString(CultureInfo.InvariantCulture)
                                                    + ") does not close the innermost open block (ending at "
                                                    + blockEnd.ToString(CultureInfo.InvariantCulture) + ")");
            }
            bool consumed = _pos == _limit;
            _pos = _limit;
            _limit = _outerLimits[--_openCount];
            return consumed;
        }

        /// <summary>
        /// Skips <paramref name="count"/> bytes (<c>IReader.Skip(int size)</c>). The size usually comes from the data, so a
        /// negative size or one beyond the bound is a <see cref="DecodeException"/>.
        /// </summary>
        public void Skip(int count)
        {
            if (count < 0) throw Invalid("negative skip of " + count.ToString(CultureInfo.InvariantCulture) + " bytes", _pos);
            Need(count);
            _pos += count;
        }

        // ---------------------------------------------------------------- scalars

        /// <summary>One byte that must be 0 or 1.</summary>
        public bool ReadBool()
        {
            Need(1);
            byte b = _buffer[_pos];
            if (b > 1) throw Invalid("invalid bool byte " + b.ToString(CultureInfo.InvariantCulture), _pos);
            _pos++;
            return b == 1;
        }

        public byte ReadByte()
        {
            Need(1);
            return _buffer[_pos++];
        }

        public sbyte ReadSByte()
        {
            Need(1);
            return unchecked((sbyte)_buffer[_pos++]);
        }

        public short ReadShort()
        {
            return unchecked((short)ReadUShort());
        }

        public ushort ReadUShort()
        {
            Need(2);
            int p = _pos;
            _pos = p + 2;
            return (ushort)(_buffer[p] | (_buffer[p + 1] << 8));
        }

        public int ReadInt()
        {
            return unchecked((int)ReadUInt32Core());
        }

        public uint ReadUInt()
        {
            return ReadUInt32Core();
        }

        public long ReadLong()
        {
            return unchecked((long)ReadUInt64Core());
        }

        public ulong ReadULong()
        {
            return ReadUInt64Core();
        }

        /// <summary>
        /// One UTF-16 code unit in its shortest UTF-8 form, 1-3 bytes (the mirror of <see cref="CanonicalWriter.WriteChar"/>).
        /// Surrogates are accepted as individual 3-byte sequences, because the writer encodes them that way; overlong
        /// forms, stray continuation bytes and 4-byte sequences (never produced for a char) are rejected.
        /// </summary>
        public char ReadChar()
        {
            int p = _pos;
            char value = DecodeChar(ref p);
            _pos = p;
            return value;
        }

        /// <summary>Raw IEEE-754 bits (NaN payloads are preserved).</summary>
        public unsafe float ReadFloat()
        {
            uint bits = ReadUInt32Core();
            return *(float*)&bits;
        }

        /// <summary>Raw IEEE-754 bits (NaN payloads are preserved).</summary>
        public double ReadDouble()
        {
            return BitConverter.Int64BitsToDouble(unchecked((long)ReadUInt64Core()));
        }

        /// <summary>
        /// The mirror of <see cref="CanonicalWriter.WriteString"/>: an <c>int</c> count of UTF-16 code units, then the
        /// chars. A negative count, or one larger than the remaining bytes (every char takes at least one), is rejected
        /// before anything is allocated. The empty string (also a written null) reads as <see cref="string.Empty"/>.
        /// The cursor moves only when the whole string decoded.
        /// </summary>
        public string ReadString()
        {
            int at = _pos;
            int count = unchecked((int)PeekUInt32Core());
            int p = at + 4;
            if (count < 0) throw Invalid("negative string length " + count.ToString(CultureInfo.InvariantCulture), at);
            if (count > _limit - p)
            {
                throw Invalid("string of " + count.ToString(CultureInfo.InvariantCulture) + " chars exceeds the "
                              + (_limit - p).ToString(CultureInfo.InvariantCulture) + " bytes remaining", at);
            }
            if (count == 0)
            {
                _pos = p;
                return string.Empty;
            }
            char[] chars;
            if (count <= MaxRetainedChars)
            {
                if (_chars.Length < count) _chars = new char[MaxRetainedChars];
                chars = _chars;
            }
            else
            {
                chars = new char[count];
            }
            for (int i = 0; i < count; i++)
            {
                chars[i] = DecodeChar(ref p);
            }
            _pos = p;
            return new string(chars, 0, count);
        }

        // ---------------------------------------------------------------- vectors and composites

        public void ReadFloat2(out float x, out float y)
        {
            Need(8);
            x = ReadFloat();
            y = ReadFloat();
        }

        public void ReadFloat3(out float x, out float y, out float z)
        {
            Need(12);
            x = ReadFloat();
            y = ReadFloat();
            z = ReadFloat();
        }

        public void ReadFloat4(out float x, out float y, out float z, out float w)
        {
            Need(16);
            x = ReadFloat();
            y = ReadFloat();
            z = ReadFloat();
            w = ReadFloat();
        }

        public void ReadInt2(out int x, out int y)
        {
            Need(8);
            x = ReadInt();
            y = ReadInt();
        }

        public void ReadInt3(out int x, out int y, out int z)
        {
            Need(12);
            x = ReadInt();
            y = ReadInt();
            z = ReadInt();
        }

        public void ReadInt4(out int x, out int y, out int z, out int w)
        {
            Need(16);
            x = ReadInt();
            y = ReadInt();
            z = ReadInt();
            w = ReadInt();
        }

        public void ReadUInt4(out uint x, out uint y, out uint z, out uint w)
        {
            Need(16);
            x = ReadUInt32Core();
            y = ReadUInt32Core();
            z = ReadUInt32Core();
            w = ReadUInt32Core();
        }

        /// <summary>One byte of bits (x = 1, y = 2); other bits must be zero.</summary>
        public void ReadBool2(out bool x, out bool y)
        {
            int bits = ReadBits(0x3, "bool2");
            x = (bits & 1) != 0;
            y = (bits & 2) != 0;
        }

        /// <summary>One byte of bits (x = 1, y = 2, z = 4); other bits must be zero.</summary>
        public void ReadBool3(out bool x, out bool y, out bool z)
        {
            int bits = ReadBits(0x7, "bool3");
            x = (bits & 1) != 0;
            y = (bits & 2) != 0;
            z = (bits & 4) != 0;
        }

        /// <summary>One byte of bits (x = 1, y = 2, z = 4, w = 8); other bits must be zero.</summary>
        public void ReadBool4(out bool x, out bool y, out bool z, out bool w)
        {
            int bits = ReadBits(0xF, "bool4");
            x = (bits & 1) != 0;
            y = (bits & 2) != 0;
            z = (bits & 4) != 0;
            w = (bits & 8) != 0;
        }

        /// <summary><c>quaternion.value</c>: x, y, z, w.</summary>
        public void ReadQuaternion(out float x, out float y, out float z, out float w)
        {
            ReadFloat4(out x, out y, out z, out w);
        }

        /// <summary>A <c>Bezier4x3</c> into <c>twelveFloats[0..11]</c> = a.x, a.y, a.z, b.x, ..., d.z.</summary>
        public void ReadBezier4x3(float[] twelveFloats)
        {
            if (twelveFloats == null) throw new ArgumentNullException(nameof(twelveFloats));
            if (twelveFloats.Length < CanonicalWriter.Bezier4x3Floats)
            {
                throw new ArgumentException("a Bezier4x3 has 12 floats (a, b, c, d as float3), the array holds "
                                            + twelveFloats.Length.ToString(CultureInfo.InvariantCulture), nameof(twelveFloats));
            }
            Need(CanonicalWriter.Bezier4x3Floats * 4);
            for (int i = 0; i < CanonicalWriter.Bezier4x3Floats; i++)
            {
                twelveFloats[i] = ReadFloat();
            }
        }

        /// <summary><c>UnityEngine.Color</c>: r, g, b, a.</summary>
        public void ReadColor(out float r, out float g, out float b, out float a)
        {
            ReadFloat4(out r, out g, out b, out a);
        }

        /// <summary><c>UnityEngine.Color32</c>: four bytes r, g, b, a.</summary>
        public void ReadColor32(out byte r, out byte g, out byte b, out byte a)
        {
            Need(4);
            int p = _pos;
            r = _buffer[p];
            g = _buffer[p + 1];
            b = _buffer[p + 2];
            a = _buffer[p + 3];
            _pos = p + 4;
        }

        /// <summary>A <c>Hash128</c>: its <c>uint4</c> value, four uints in order.</summary>
        public void ReadHash128(out uint a, out uint b, out uint c, out uint d)
        {
            ReadUInt4(out a, out b, out c, out d);
        }

        // ---------------------------------------------------------------- entities

        /// <summary>
        /// <c>IReader.Read(out Entity)</c>: decodes a <see cref="NetRef"/> and resolves it through the seam. Returns false
        /// when the reference did not resolve (missing): the handle is then what the seam returned for it, and the record
        /// is flagged (<see cref="HadMissingReference"/>, <see cref="FirstMissing"/>). This also reads references written
        /// with <see cref="CanonicalWriter.WriteEntityIgnoreVersion"/>.
        /// </summary>
        public bool ReadEntity(out int index, out int version)
        {
            int p = _pos;
            NetRef reference = CanonicalNetRef.Read(_buffer, ref p, _limit, _start);
            // the cursor and the counters move only after the seam accepted the reference, so an exception thrown by the
            // seam leaves the reader exactly as a malformed reference would (failure contract above)
            (int i, int v) = _entities.DecodeEntity(in reference, out bool missing, out NetSlot missingSlot);
            _pos = p;
            index = i;
            version = v;
            _referenceCount++;
            if (reference.Kind == NetRefKind.Derived) _derivedCount++;
            if (!missing) return true;
            _hadMissing = true;
            _missingCount++;
            if (_firstMissing.IsNone) _firstMissing = missingSlot;
            return false;
        }

        /// <summary>
        /// The counterpart of <see cref="CanonicalWriter.WriteEntityIgnoreVersion"/>: the same bytes as any reference, so
        /// this is <see cref="ReadEntity"/> without the version. The game's <c>IReader</c> has no index-only read
        /// (<c>PathNode.Deserialize</c> calls <c>Read(out Entity)</c> and keeps the index), so <c>NetReader</c> forwards
        /// to <see cref="ReadEntity"/>; this member exists for Core callers that wrote index-only references.
        /// </summary>
        public bool ReadEntityIgnoreVersion(out int index)
        {
            return ReadEntity(out index, out _);
        }

        /// <summary>
        /// A portable reference without the seam (ARCH §4.3.3); the mirror of <see cref="CanonicalWriter.WriteNetRef"/>.
        /// The cursor moves only when the whole reference decoded.
        /// </summary>
        public NetRef ReadNetRef()
        {
            int p = _pos;
            NetRef value = CanonicalNetRef.Read(_buffer, ref p, _limit, _start);
            _pos = p;
            return value;
        }

        /// <summary>
        /// <c>IReader.Read(NativeArray&lt;Entity&gt;)</c>: <paramref name="count"/> references into
        /// <paramref name="indices"/>/<paramref name="versions"/>; true when all of them resolved. Elements after a
        /// missing one are still decoded. A malformed element throws <see cref="DecodeException"/> after the elements
        /// before it went through the seam (the record is abandoned then).
        /// </summary>
        public bool ReadEntityArray(int[] indices, int[] versions, int count)
        {
            CheckDestination(indices, count, 1, nameof(indices));
            CheckDestination(versions, count, 1, nameof(versions));
            NeedItems(count, 1);   // every reference takes at least one byte
            bool all = true;
            for (int i = 0; i < count; i++)
            {
                all &= ReadEntity(out indices[i], out versions[i]);
            }
            return all;
        }

        // ---------------------------------------------------------------- arrays

        /// <summary>
        /// The mirror of <see cref="CanonicalWriter.WriteCount"/> for elements of at least one byte each (buffers of
        /// <c>ISerializable</c> elements: the game refuses element types that serialise nothing,
        /// <c>BufferElementDataSerializer.cs</c>). A negative count or one the remaining bytes cannot hold throws
        /// <see cref="DecodeException"/>.
        /// </summary>
        public int ReadCount()
        {
            return ReadCount(1);
        }

        /// <summary>
        /// A count of elements that take at least <paramref name="minElementBytes"/> bytes each (for example 4 for the
        /// ints of a <c>NativeList&lt;int&gt;</c>, 1 for its entities); validated against the remaining bytes before
        /// the caller allocates anything.
        /// </summary>
        public int ReadCount(int minElementBytes)
        {
            if (minElementBytes < 1) throw new ArgumentOutOfRangeException(nameof(minElementBytes), minElementBytes, "elements take at least one byte");
            int at = _pos;
            int count = unchecked((int)PeekUInt32Core());
            int p = at + 4;
            if (count < 0) throw Invalid("negative count " + count.ToString(CultureInfo.InvariantCulture), at);
            if ((long)count * minElementBytes > _limit - p)
            {
                throw Invalid("count " + count.ToString(CultureInfo.InvariantCulture) + " needs at least "
                              + ((long)count * minElementBytes).ToString(CultureInfo.InvariantCulture) + " bytes, "
                              + (_limit - p).ToString(CultureInfo.InvariantCulture) + " remaining", at);
            }
            _pos = p;
            return count;
        }

        /// <summary><c>IReader.Read(NativeArray&lt;int&gt;)</c>: <paramref name="count"/> ints into <c>v[0 .. count-1]</c>.</summary>
        public void ReadIntArray(int[] v, int count)
        {
            CheckDestination(v, count, 1, nameof(v));
            NeedItems(count, 4);
            int p = _pos;
            byte[] b = _buffer;
            for (int i = 0; i < count; i++, p += 4)
            {
                v[i] = unchecked((int)GetUInt32(b, p));
            }
            _pos = p;
        }

        /// <summary><c>IReader.Read(NativeArray&lt;ushort&gt;)</c>: <paramref name="count"/> ushorts into <c>v[0 .. count-1]</c>.</summary>
        public void ReadUShortArray(ushort[] v, int count)
        {
            CheckDestination(v, count, 1, nameof(v));
            NeedItems(count, 2);
            int p = _pos;
            byte[] b = _buffer;
            for (int i = 0; i < count; i++, p += 2)
            {
                v[i] = (ushort)(b[p] | (b[p + 1] << 8));
            }
            _pos = p;
        }

        /// <summary>
        /// <c>IReader.Read(NativeArray&lt;byte&gt;)</c> (stride 1) and <c>Read(NativeArray&lt;byte&gt;, int stride)</c>:
        /// <paramref name="count"/> bytes into <c>v[0 .. count-1]</c>, undoing the <see cref="ByteShuffle"/> transposition
        /// when the stride is above 1. The stride must equal the writer's.
        /// </summary>
        public void ReadByteArray(byte[] v, int count, int stride = 1)
        {
            CheckDestination(v, count, 1, nameof(v));
            NeedItems(count, 1);
            if (count == 0) return;
            if (stride <= 1)
            {
                Buffer.BlockCopy(_buffer, _pos, v, 0, count);
            }
            else
            {
                ByteShuffle.Unshuffle(_buffer, _pos, v, 0, count, stride);
            }
            _pos += count;
        }

        /// <summary><c>IReader.Read(NativeArray&lt;float2&gt;)</c>: <paramref name="count"/> pairs into <c>xy[0 .. 2·count-1]</c>.</summary>
        public void ReadFloat2Array(float[] xy, int count)
        {
            CheckDestination(xy, count, 2, nameof(xy));
            ReadFloats(xy, count * 2);
        }

        /// <summary><c>IReader.Read(NativeArray&lt;float4&gt;)</c>: <paramref name="count"/> quads into <c>xyzw[0 .. 4·count-1]</c>.</summary>
        public void ReadFloat4Array(float[] xyzw, int count)
        {
            CheckDestination(xyzw, count, 4, nameof(xyzw));
            ReadFloats(xyzw, count * 4);
        }

        /// <summary><c>IReader.Read(NativeArray&lt;int2&gt;)</c>: <paramref name="count"/> pairs into <c>xy[0 .. 2·count-1]</c>.</summary>
        public void ReadInt2Array(int[] xy, int count)
        {
            CheckDestination(xy, count, 2, nameof(xy));
            int values = count * 2;
            NeedItems(values, 4);
            int p = _pos;
            byte[] b = _buffer;
            for (int i = 0; i < values; i++, p += 4)
            {
                xy[i] = unchecked((int)GetUInt32(b, p));
            }
            _pos = p;
        }

        // ---------------------------------------------------------------- helpers

        private unsafe void ReadFloats(float[] destination, int values)
        {
            NeedItems(values, 4);
            if (values == 0) return;
            int p = _pos;
            byte[] b = _buffer;
            fixed (float* d = destination)
            {
                uint* bits = (uint*)d;
                for (int i = 0; i < values; i++, p += 4)
                {
                    bits[i] = GetUInt32(b, p);   // raw bits: NaN payloads are preserved
                }
            }
            _pos = p;
        }

        private uint ReadUInt32Core()
        {
            Need(4);
            int p = _pos;
            _pos = p + 4;
            return GetUInt32(_buffer, p);
        }

        /// <summary>The little-endian uint at the cursor, without consuming it (length prefixes are validated first).</summary>
        private uint PeekUInt32Core()
        {
            Need(4);
            return GetUInt32(_buffer, _pos);
        }

        /// <summary>
        /// Decodes one char in its shortest 1-3 byte UTF-8 form at the absolute offset <paramref name="p"/> and advances
        /// <paramref name="p"/>; never reads at or beyond the current bound. The mirror of
        /// <see cref="CanonicalWriter.WriteChar"/> (stock reader: <c>BinaryReader.cs:287-321</c>). Surrogates are accepted
        /// as individual 3-byte sequences, because the writer encodes them that way; overlong forms, stray continuation
        /// bytes and 4-byte sequences (the stock writer never produces one for a 16-bit char) are rejected.
        /// </summary>
        private char DecodeChar(ref int p)
        {
            int at = p;
            if (_limit - at < 1) throw OverrunAt(1, at);
            int b0 = _buffer[at];
            if (b0 < 0x80)
            {
                p = at + 1;
                return (char)b0;
            }
            if ((b0 & 0xE0) == 0xC0)
            {
                if (_limit - at < 2) throw OverrunAt(2, at);
                int value = ((b0 & 0x1F) << 6) | Continuation(at + 1);
                if (value < 0x80) throw Invalid("overlong UTF-8 sequence for a char", at);
                p = at + 2;
                return (char)value;
            }
            if ((b0 & 0xF0) == 0xE0)
            {
                if (_limit - at < 3) throw OverrunAt(3, at);
                int value = ((b0 & 0x0F) << 12) | (Continuation(at + 1) << 6) | Continuation(at + 2);
                if (value < 0x800) throw Invalid("overlong UTF-8 sequence for a char", at);
                p = at + 3;
                return (char)value;
            }
            throw Invalid("invalid UTF-8 lead byte 0x" + b0.ToString("X2", CultureInfo.InvariantCulture) + " for a char", at);
        }

        private ulong ReadUInt64Core()
        {
            Need(8);
            int p = _pos;
            _pos = p + 8;
            ulong lo = GetUInt32(_buffer, p);
            ulong hi = GetUInt32(_buffer, p + 4);
            return lo | (hi << 32);
        }

        private static uint GetUInt32(byte[] b, int p)
        {
            return b[p] | ((uint)b[p + 1] << 8) | ((uint)b[p + 2] << 16) | ((uint)b[p + 3] << 24);
        }

        private int ReadBits(int allowedMask, string what)
        {
            Need(1);
            int bits = _buffer[_pos];
            if ((bits & ~allowedMask) != 0)
            {
                throw Invalid("invalid " + what + " byte 0x" + bits.ToString("X2", CultureInfo.InvariantCulture), _pos);
            }
            _pos++;
            return bits;
        }

        /// <summary>The 6 payload bits of the UTF-8 continuation byte at the absolute offset <paramref name="p"/>.</summary>
        private int Continuation(int p)
        {
            int b = _buffer[p];
            if ((b & 0xC0) != 0x80)
            {
                throw Invalid("invalid UTF-8 continuation byte 0x" + b.ToString("X2", CultureInfo.InvariantCulture), p);
            }
            return b & 0x3F;
        }

        private void Need(int count)
        {
            if (_limit - _pos < count) throw Overrun(count);
        }

        private void NeedItems(int count, int bytesPerItem)
        {
            if ((long)count * bytesPerItem > _limit - _pos) throw Overrun((long)count * bytesPerItem);
        }

        /// <summary>Validates a destination array for <paramref name="count"/> items of <paramref name="valuesPerItem"/> elements.</summary>
        private static void CheckDestination(Array destination, int count, int valuesPerItem, string name)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(name, count, "the count must not be negative");
            if (count == 0) return;
            if (destination == null) throw new ArgumentNullException(name);
            long needed = (long)count * valuesPerItem;
            if (needed > destination.Length)
            {
                throw new ArgumentOutOfRangeException(name, count, "the array holds " + destination.Length.ToString(CultureInfo.InvariantCulture)
                                                                   + " elements, " + needed.ToString(CultureInfo.InvariantCulture) + " are needed");
            }
        }

        private DecodeException Overrun(long count)
        {
            return OverrunAt(count, _pos);
        }

        private DecodeException OverrunAt(long count, int absolutePosition)
        {
            string bound = _openCount > 0 ? "the enclosing block" : "the record";
            return new DecodeException("read of " + count.ToString(CultureInfo.InvariantCulture) + " byte(s) at position "
                                       + (absolutePosition - _start).ToString(CultureInfo.InvariantCulture) + " overruns " + bound + " ("
                                       + Math.Max(0, _limit - absolutePosition).ToString(CultureInfo.InvariantCulture) + " remaining)");
        }

        private DecodeException Invalid(string what, int absolutePosition)
        {
            return new DecodeException(what + " at position " + (absolutePosition - _start).ToString(CultureInfo.InvariantCulture));
        }
    }
}
