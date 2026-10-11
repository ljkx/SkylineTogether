using System;
using System.Globalization;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Codec
{
    /// <summary>
    /// Writes canonical bytes (ARCH §4.3.1, §4.3.4): every member of the game's <c>IWriter</c>
    /// (<c>Colossal.Serialization.Entities/IWriter.cs</c>) in Core types, in the stock little-endian layout of the game's
    /// <c>BinaryWriter</c> (<c>BinaryWriter.cs</c>, R01 §2.2), except that entities are written as <see cref="NetRef"/>s
    /// obtained from an <see cref="IEntityRefWriter"/>. The game's <c>NetWriter</c> forwards each <c>IWriter</c> member to
    /// one method here, so the byte layout is defined and unit-tested in Core.
    /// <para>Layout per member family (all multi-byte values little-endian, regardless of the host's byte order):</para>
    /// <list type="bullet">
    /// <item>bool: 1 byte, 1 or 0. byte/sbyte: 1 byte. short/ushort: 2. int/uint/float: 4. long/ulong/double: 8.
    /// Floats and doubles are their raw IEEE-754 bits, so NaN payloads and negative zero survive.</item>
    /// <item>char: the UTF-16 code unit on its own in UTF-8 form, 1-3 bytes; surrogates are encoded individually
    /// (<c>BinaryWriter.cs:275-303</c>). string: int count of UTF-16 code units, then every char (<c>:171-178</c>);
    /// null is written like the empty string (the stock writer would throw).</item>
    /// <item>float2/3/4, int2/3/4, uint4, quaternion (x, y, z, w), Color (r, g, b, a), Hash128 (four uints),
    /// Bezier4x3 (a, b, c, d as float3 = 12 floats): the components in order (<c>:163-241, 262-273</c>).</item>
    /// <item>bool2/3/4: one byte of bits, x = 1, y = 2, z = 4, w = 8 (<c>:243-260</c>). Color32: 4 bytes r, g, b, a.</item>
    /// <item>Begin/End: a 4-byte length placeholder patched with the payload size (<c>:27-49</c>), which lets a reader
    /// skip a block it does not fully consume.</item>
    /// <item>Entity: one <see cref="NetRef"/> (see <see cref="CanonicalNetRef"/>), never the machine-local index.</item>
    /// <item>Native arrays: the elements back to back without a length prefix (<c>:51-102</c>); lists and buffers write a
    /// <see cref="WriteCount"/> first. Strided byte arrays are transposed by <see cref="ByteShuffle"/>.</item>
    /// </list>
    /// <para>Forwarding of every <c>IWriter</c> member (game-ecs-schema's <c>NetWriter</c>, one line each):</para>
    /// <code>
    /// Begin() / End(b) / End(b, out size)                 Begin() / End(token) / End(token, out size)
    /// Write(bool|byte|sbyte|short|ushort|int|uint)         WriteBool / WriteByte / WriteSByte / WriteShort / WriteUShort / WriteInt / WriteUInt
    /// Write(long|ulong|char|float|double|string)           WriteLong / WriteULong / WriteChar / WriteFloat / WriteDouble / WriteString
    /// Write(float2|float3|float4|int2|int3|int4|uint4)     WriteFloat2 ... WriteInt4 / WriteUInt4 (components in order)
    /// Write(bool2|bool3|bool4)                             WriteBool2 / WriteBool3 / WriteBool4
    /// Write(quaternion|Color|Color32|Hash128)              WriteQuaternion(v.value.x..w) / WriteColor / WriteColor32 / WriteHash128(v.value.x..w)
    ///                                                      (IWriter's Hash128 is Colossal.Hash128, whose uint4 field is "value")
    /// Write(Bezier4x3)                                     WriteBezier4x3(a.xyz, b.xyz, c.xyz, d.xyz in a reused float[12])
    /// Write(Entity) / Write(Entity, ignoreVersion)         WriteEntity(e.Index, e.Version) / ignoreVersion ? WriteEntityIgnoreVersion(e.Index) : WriteEntity(...)
    /// Write(NativeArray&lt;Entity&gt;)                    WriteEntityArray(indices, versions, length)
    /// Write(NativeList&lt;Entity&gt;)                     WriteCount(length) + WriteEntityArray(...)
    /// Write(NativeList&lt;int&gt;)                        WriteCount(length) + WriteIntArray(...)
    /// Write(NativeArray&lt;int|int2|ushort|byte|float4|float2&gt;)   WriteIntArray / WriteInt2Array / WriteUShortArray / WriteByteArray / WriteFloat4Array / WriteFloat2Array
    /// Write(NativeArray&lt;byte&gt;, stride)              WriteByteArray(bytes, length, stride)
    /// Write&lt;T&gt;(T) / Write&lt;T&gt;(NativeArray&lt;T&gt;)   value.Serialize(this writer), element by element, no count (no codec call)
    /// buffers of ISerializable elements (codec layer)      WriteCount(length), then each element's Serialize
    /// </code>
    /// <para>
    /// The writer appends to its target <see cref="ByteWriter"/> (switchable with <see cref="Retarget"/>); the caller owns
    /// the target's lifetime (resets, copying the payload out). An instance is not thread-safe; use one per thread. The
    /// class has no thread affinity of its own: an instance has that of its entity seam (the game's
    /// <c>EntityRefBridge</c> is main-thread-only, a fake seam is not).
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class CanonicalWriter
    {
        /// <summary>Size of the length prefix of a block (a little-endian <c>int</c>, like the stock writer).</summary>
        public const int BlockHeaderBytes = 4;

        /// <summary>Number of floats in a <c>Bezier4x3</c> (four <c>float3</c> control points a, b, c, d).</summary>
        public const int Bezier4x3Floats = 12;

        private ByteWriter _target;
        private readonly IEntityRefWriter _entities;
        private int[] _openBlocks = new int[8];
        private int _openCount;

        public CanonicalWriter(ByteWriter target, IEntityRefWriter entities)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
            _entities = entities ?? throw new ArgumentNullException(nameof(entities));
        }

        /// <summary>The buffer the canonical bytes are appended to.</summary>
        public ByteWriter Target => _target;

        /// <summary>
        /// Appends to <paramref name="target"/> from now on, without allocating: the seams of core-contracts hand the game
        /// layer a different <see cref="ByteWriter"/> per call (<c>IHostWorldReader.TrySerialize</c>,
        /// <c>IComponentPayloadSource.Write</c>), and one long-lived writer per thread serves all of them. Open blocks
        /// belong to the previous target and are forgotten, so this must not be called inside a <c>Serialize</c> call.
        /// </summary>
        public void Retarget(ByteWriter target)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
            _openCount = 0;
        }

        /// <summary>The entity seam every <see cref="WriteEntity"/> goes through.</summary>
        public IEntityRefWriter Entities => _entities;

        /// <summary>
        /// Blocks begun and not yet ended. A complete <c>Serialize</c> call leaves this unchanged; callers check it after
        /// each value and call <see cref="ClearOpenBlocks"/> after an aborted one.
        /// </summary>
        public int OpenBlockCount => _openCount;

        /// <summary>Forgets every open block (after an aborted <c>Serialize</c> call or a reset of the target).</summary>
        public void ClearOpenBlocks()
        {
            _openCount = 0;
        }

        // ---------------------------------------------------------------- blocks

        /// <summary>
        /// Opens a size-prefixed block: writes a zero 4-byte placeholder that <see cref="End(BlockToken, out int)"/>
        /// patches with the payload size, as <c>BinaryWriter.Begin</c> does (<c>BinaryWriter.cs:27-33</c>).
        /// </summary>
        public BlockToken Begin()
        {
            // blocks left open by an aborted Serialize call whose bytes were discarded since (target reset) are dropped
            while (_openCount > 0 && _openBlocks[_openCount - 1] > _target.Length - BlockHeaderBytes)
            {
                _openCount--;
            }
            int position = _target.Reserve(BlockHeaderBytes);
            if (_openCount == _openBlocks.Length) Array.Resize(ref _openBlocks, _openBlocks.Length * 2);
            _openBlocks[_openCount++] = position;
            return new BlockToken { Position = position };
        }

        /// <summary>Closes the innermost open block; true when its payload is not empty (as <c>BinaryWriter.End</c>).</summary>
        public bool End(BlockToken block)
        {
            return End(block, out _);
        }

        /// <summary>
        /// Closes the innermost open block, patches its length prefix and returns the payload size in
        /// <paramref name="size"/>; true when the payload is not empty (<c>BinaryWriter.cs:41-49</c>). Blocks must be
        /// closed innermost first: anything else is a programming error (<see cref="InvalidOperationException"/>).
        /// </summary>
        public bool End(BlockToken block, out int size)
        {
            if (_openCount == 0)
            {
                throw new InvalidOperationException("End(" + block.Position.ToString(CultureInfo.InvariantCulture) + ") without an open block");
            }
            int top = _openBlocks[_openCount - 1];
            if (block.Position != top)
            {
                throw new InvalidOperationException("End(" + block.Position.ToString(CultureInfo.InvariantCulture)
                                                    + ") does not close the innermost open block (at "
                                                    + top.ToString(CultureInfo.InvariantCulture) + "); blocks must be closed innermost first");
            }
            if (top > _target.Length - BlockHeaderBytes)
            {
                _openCount--;
                throw new InvalidOperationException("the block at " + top.ToString(CultureInfo.InvariantCulture)
                                                    + " lies beyond the written bytes (the target was reset or truncated)");
            }
            size = _target.Length - top - BlockHeaderBytes;
            _target.PatchUInt32(top, unchecked((uint)size));
            _openCount--;
            return size != 0;
        }

        // ---------------------------------------------------------------- scalars

        public void WriteBool(bool v)
        {
            _target.WriteByte(v ? (byte)1 : (byte)0);
        }

        public void WriteByte(byte v)
        {
            _target.WriteByte(v);
        }

        public void WriteSByte(sbyte v)
        {
            _target.WriteByte(unchecked((byte)v));
        }

        public void WriteShort(short v)
        {
            _target.WriteUInt16(unchecked((ushort)v));
        }

        public void WriteUShort(ushort v)
        {
            _target.WriteUInt16(v);
        }

        public void WriteInt(int v)
        {
            _target.WriteInt32(v);
        }

        public void WriteUInt(uint v)
        {
            _target.WriteUInt32(v);
        }

        public void WriteLong(long v)
        {
            _target.WriteInt64(v);
        }

        public void WriteULong(ulong v)
        {
            _target.WriteUInt64(v);
        }

        /// <summary>
        /// One UTF-16 code unit in UTF-8 form, 1-3 bytes, exactly as <c>BinaryWriter.Write(char)</c>
        /// (<c>BinaryWriter.cs:275-303</c>): a surrogate is encoded on its own as a 3-byte sequence, so every
        /// <c>char</c> value round-trips (the stock 4-byte branch is unreachable for a 16-bit value).
        /// </summary>
        public void WriteChar(char v)
        {
            if (v <= '\u007F')
            {
                _target.WriteByte((byte)v);
            }
            else if (v <= '߿')
            {
                _target.WriteByte((byte)(0xC0 | (v >> 6)));
                _target.WriteByte((byte)(0x80 | (v & 0x3F)));
            }
            else
            {
                _target.WriteByte((byte)(0xE0 | (v >> 12)));
                _target.WriteByte((byte)(0x80 | ((v >> 6) & 0x3F)));
                _target.WriteByte((byte)(0x80 | (v & 0x3F)));
            }
        }

        /// <summary>Raw IEEE-754 bits, little-endian (<c>math.asuint</c> in the stock writer).</summary>
        public void WriteFloat(float v)
        {
            _target.WriteSingle(v);
        }

        /// <summary>Raw IEEE-754 bits, little-endian (<c>math.asulong</c> in the stock writer).</summary>
        public void WriteDouble(double v)
        {
            _target.WriteDouble(v);
        }

        /// <summary>
        /// <c>int</c> count of UTF-16 code units, then every char as <see cref="WriteChar"/> (<c>BinaryWriter.cs:171-178</c>).
        /// null is written as the empty string and therefore reads back as <see cref="string.Empty"/>.
        /// </summary>
        public void WriteString(string v)
        {
            if (string.IsNullOrEmpty(v))
            {
                _target.WriteInt32(0);
                return;
            }
            _target.WriteInt32(v.Length);
            for (int i = 0; i < v.Length; i++)
            {
                WriteChar(v[i]);
            }
        }

        // ---------------------------------------------------------------- vectors and composites

        public void WriteFloat2(float x, float y)
        {
            _target.WriteSingle(x);
            _target.WriteSingle(y);
        }

        public void WriteFloat3(float x, float y, float z)
        {
            _target.WriteSingle(x);
            _target.WriteSingle(y);
            _target.WriteSingle(z);
        }

        public void WriteFloat4(float x, float y, float z, float w)
        {
            _target.WriteSingle(x);
            _target.WriteSingle(y);
            _target.WriteSingle(z);
            _target.WriteSingle(w);
        }

        public void WriteInt2(int x, int y)
        {
            _target.WriteInt32(x);
            _target.WriteInt32(y);
        }

        public void WriteInt3(int x, int y, int z)
        {
            _target.WriteInt32(x);
            _target.WriteInt32(y);
            _target.WriteInt32(z);
        }

        public void WriteInt4(int x, int y, int z, int w)
        {
            _target.WriteInt32(x);
            _target.WriteInt32(y);
            _target.WriteInt32(z);
            _target.WriteInt32(w);
        }

        public void WriteUInt4(uint x, uint y, uint z, uint w)
        {
            _target.WriteUInt32(x);
            _target.WriteUInt32(y);
            _target.WriteUInt32(z);
            _target.WriteUInt32(w);
        }

        /// <summary>One byte: x = bit 0, y = bit 1 (<c>BinaryWriter.cs:256-260</c>).</summary>
        public void WriteBool2(bool x, bool y)
        {
            _target.WriteByte((byte)((x ? 1 : 0) | (y ? 2 : 0)));
        }

        /// <summary>One byte: x = bit 0, y = bit 1, z = bit 2 (<c>BinaryWriter.cs:250-254</c>).</summary>
        public void WriteBool3(bool x, bool y, bool z)
        {
            _target.WriteByte((byte)((x ? 1 : 0) | (y ? 2 : 0) | (z ? 4 : 0)));
        }

        /// <summary>One byte: x = bit 0, y = bit 1, z = bit 2, w = bit 3 (<c>BinaryWriter.cs:243-248</c>).</summary>
        public void WriteBool4(bool x, bool y, bool z, bool w)
        {
            _target.WriteByte((byte)((x ? 1 : 0) | (y ? 2 : 0) | (z ? 4 : 0) | (w ? 8 : 0)));
        }

        /// <summary><c>quaternion.value</c> as a float4: x, y, z, w (<c>BinaryWriter.cs:196-199</c>).</summary>
        public void WriteQuaternion(float x, float y, float z, float w)
        {
            WriteFloat4(x, y, z, w);
        }

        /// <summary>
        /// A <c>Bezier4x3</c> as its control points a, b, c, d, each a float3: <c>twelveFloats[0..11]</c> =
        /// a.x, a.y, a.z, b.x, ..., d.z (<c>BinaryWriter.cs:163-169</c>).
        /// </summary>
        public void WriteBezier4x3(float[] twelveFloats)
        {
            if (twelveFloats == null) throw new ArgumentNullException(nameof(twelveFloats));
            if (twelveFloats.Length < Bezier4x3Floats)
            {
                throw new ArgumentException("a Bezier4x3 has 12 floats (a, b, c, d as float3), got "
                                            + twelveFloats.Length.ToString(CultureInfo.InvariantCulture), nameof(twelveFloats));
            }
            for (int i = 0; i < Bezier4x3Floats; i++)
            {
                _target.WriteSingle(twelveFloats[i]);
            }
        }

        /// <summary><c>UnityEngine.Color</c>: four floats r, g, b, a (<c>BinaryWriter.cs:180-186</c>).</summary>
        public void WriteColor(float r, float g, float b, float a)
        {
            WriteFloat4(r, g, b, a);
        }

        /// <summary><c>UnityEngine.Color32</c>: four bytes r, g, b, a (<c>BinaryWriter.cs:188-194</c>).</summary>
        public void WriteColor32(byte r, byte g, byte b, byte a)
        {
            _target.WriteByte(r);
            _target.WriteByte(g);
            _target.WriteByte(b);
            _target.WriteByte(a);
        }

        /// <summary>A <c>Hash128</c> as its <c>uint4</c> value: four uints in order (<c>BinaryWriter.cs:270-273</c>).</summary>
        public void WriteHash128(uint a, uint b, uint c, uint d)
        {
            WriteUInt4(a, b, c, d);
        }

        // ---------------------------------------------------------------- entities

        /// <summary>
        /// <c>IWriter.Write(Entity)</c>: the <see cref="NetRef"/> the seam returns for <c>(index, version)</c>, never the
        /// machine-local handle (ARCH §4.3.1). The seam is consulted for the null entity too.
        /// </summary>
        public void WriteEntity(int index, int version)
        {
            NetRef reference = _entities.EncodeEntity(index, version, false);
            CanonicalNetRef.Write(_target, in reference);
        }

        /// <summary>
        /// <c>IWriter.Write(Entity, ignoreVersion: true)</c>: resolved by index only (the seam receives version 0). Only
        /// <c>Game.Pathfind.PathNode</c> uses it, with an index-only entity (R01 §3). It reads back with
        /// <see cref="CanonicalReader.ReadEntity"/>, like every other reference.
        /// </summary>
        public void WriteEntityIgnoreVersion(int index)
        {
            NetRef reference = _entities.EncodeEntity(index, 0, true);
            CanonicalNetRef.Write(_target, in reference);
        }

        /// <summary>A reference that is already portable (no seam); the encoding of ARCH §4.3.3.</summary>
        public void WriteNetRef(in NetRef value)
        {
            CanonicalNetRef.Write(_target, in value);
        }

        /// <summary><c>IWriter.Write(NativeArray&lt;Entity&gt;)</c>: <paramref name="count"/> references, no length prefix.</summary>
        public void WriteEntityArray(int[] indices, int[] versions, int count)
        {
            CheckSource(indices, count, 1, nameof(indices));
            CheckSource(versions, count, 1, nameof(versions));
            for (int i = 0; i < count; i++)
            {
                WriteEntity(indices[i], versions[i]);
            }
        }

        // ---------------------------------------------------------------- arrays

        /// <summary>
        /// The element count of a list or buffer that follows: a little-endian <c>int</c>, the layout the stock writer uses
        /// for <c>NativeList</c> lengths (<c>BinaryWriter.cs:59-66, 111-118</c>) and per-entity buffer lengths
        /// (<c>BufferElementDataSerializer.cs</c>). Arrays of <c>ISerializable</c> (buffers): count, then the elements.
        /// </summary>
        public void WriteCount(int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), count, "a count is never negative");
            _target.WriteInt32(count);
        }

        /// <summary><c>IWriter.Write(NativeArray&lt;int&gt;)</c>: <paramref name="count"/> ints, no length prefix.</summary>
        public void WriteIntArray(int[] v, int count)
        {
            CheckSource(v, count, 1, nameof(v));
            if (count == 0) return;
            int p = _target.Reserve(count * 4);
            byte[] b = _target.Buffer;
            for (int i = 0; i < count; i++, p += 4)
            {
                PutUInt32(b, p, unchecked((uint)v[i]));
            }
        }

        /// <summary><c>IWriter.Write(NativeArray&lt;ushort&gt;)</c>: <paramref name="count"/> ushorts, no length prefix.</summary>
        public void WriteUShortArray(ushort[] v, int count)
        {
            CheckSource(v, count, 1, nameof(v));
            if (count == 0) return;
            int p = _target.Reserve(count * 2);
            byte[] b = _target.Buffer;
            for (int i = 0; i < count; i++, p += 2)
            {
                ushort x = v[i];
                b[p] = (byte)x;
                b[p + 1] = (byte)(x >> 8);
            }
        }

        /// <summary>
        /// <c>IWriter.Write(NativeArray&lt;byte&gt;)</c> (stride 1) and <c>Write(NativeArray&lt;byte&gt;, int stride)</c>:
        /// <paramref name="count"/> bytes, no length prefix; with a stride above 1 the bytes are transposed by
        /// <see cref="ByteShuffle"/> (same length).
        /// </summary>
        public void WriteByteArray(byte[] v, int count, int stride = 1)
        {
            CheckSource(v, count, 1, nameof(v));
            if (count == 0) return;
            if (stride <= 1)
            {
                _target.WriteBytes(v, 0, count);
                return;
            }
            int p = _target.Reserve(count);
            ByteShuffle.Shuffle(v, 0, _target.Buffer, p, count, stride);
        }

        /// <summary><c>IWriter.Write(NativeArray&lt;float2&gt;)</c>: <paramref name="count"/> pairs from <c>xy[0 .. 2·count-1]</c>.</summary>
        public void WriteFloat2Array(float[] xy, int count)
        {
            CheckSource(xy, count, 2, nameof(xy));
            WriteFloats(xy, count * 2);
        }

        /// <summary><c>IWriter.Write(NativeArray&lt;float4&gt;)</c>: <paramref name="count"/> quads from <c>xyzw[0 .. 4·count-1]</c>.</summary>
        public void WriteFloat4Array(float[] xyzw, int count)
        {
            CheckSource(xyzw, count, 4, nameof(xyzw));
            WriteFloats(xyzw, count * 4);
        }

        /// <summary><c>IWriter.Write(NativeArray&lt;int2&gt;)</c>: <paramref name="count"/> pairs from <c>xy[0 .. 2·count-1]</c>.</summary>
        public void WriteInt2Array(int[] xy, int count)
        {
            CheckSource(xy, count, 2, nameof(xy));
            int values = count * 2;
            if (values == 0) return;
            int p = _target.Reserve(values * 4);
            byte[] b = _target.Buffer;
            for (int i = 0; i < values; i++, p += 4)
            {
                PutUInt32(b, p, unchecked((uint)xy[i]));
            }
        }

        // ---------------------------------------------------------------- helpers

        private unsafe void WriteFloats(float[] source, int values)
        {
            if (values == 0) return;
            int p = _target.Reserve(values * 4);
            byte[] b = _target.Buffer;
            fixed (float* s = source)
            {
                uint* bits = (uint*)s;
                for (int i = 0; i < values; i++, p += 4)
                {
                    PutUInt32(b, p, bits[i]);   // raw bits: NaN payloads are preserved
                }
            }
        }

        private static void PutUInt32(byte[] b, int p, uint x)
        {
            b[p] = (byte)x;
            b[p + 1] = (byte)(x >> 8);
            b[p + 2] = (byte)(x >> 16);
            b[p + 3] = (byte)(x >> 24);
        }

        /// <summary>
        /// Validates a source array for <paramref name="count"/> items of <paramref name="valuesPerItem"/> elements; also
        /// rejects counts whose byte size would not fit an array (at most 8 bytes per element).
        /// </summary>
        private static void CheckSource(Array source, int count, int valuesPerItem, string name)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(name, count, "the count must not be negative");
            if (count == 0) return;
            if (source == null) throw new ArgumentNullException(name);
            long needed = (long)count * valuesPerItem;
            if (needed > source.Length)
            {
                throw new ArgumentOutOfRangeException(name, count, "the array holds " + source.Length.ToString(CultureInfo.InvariantCulture)
                                                                   + " elements, " + needed.ToString(CultureInfo.InvariantCulture) + " are needed");
            }
            if (needed > int.MaxValue / 8) throw new ArgumentOutOfRangeException(name, count, "the array is too large for one record");
        }
    }
}
