using System;
using System.Globalization;
using System.Text;

namespace SkylineTogether.Core.Buffers
{
    /// <summary>
    /// Bounds-checked little-endian reader over a window of a byte array, the mirror of <see cref="ByteWriter"/>.
    /// It is a class so that every holder shares one cursor (the reason the game's own reader keeps its position in
    /// shared storage, R01 §2.2). Every overrun and every invalid encoding throws <see cref="DecodeException"/>:
    /// varints longer than their type width or with an overlong (non-canonical) encoding, booleans other than 0/1,
    /// strings above their limit. The reader never reads outside the window given to <see cref="Reset"/>.
    /// Not thread-safe.
    /// </summary>
    public sealed class ByteReader
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

        private byte[] _buffer;
        private int _start;
        private int _end;
        private int _pos;

        /// <summary>An empty reader; call <see cref="Reset"/> before reading.</summary>
        public ByteReader()
        {
            _buffer = Array.Empty<byte>();
        }

        public ByteReader(byte[] buffer, int offset, int count)
        {
            Reset(buffer, offset, count);
        }

        /// <summary>Points the reader at <c>buffer[offset .. offset+count-1]</c> with the cursor at its start.</summary>
        public void Reset(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "the window lies outside the buffer");
            }
            _buffer = buffer;
            _start = offset;
            _end = offset + count;
            _pos = offset;
        }

        /// <summary>Cursor position relative to the start of the window.</summary>
        public int Position => _pos - _start;

        /// <summary>Bytes left in the window.</summary>
        public int Remaining => _end - _pos;

        public bool IsAtEnd => _pos >= _end;

        public byte ReadByte()
        {
            if (_pos >= _end) throw Overrun(1);
            return _buffer[_pos++];
        }

        /// <summary>The next byte without advancing.</summary>
        public byte PeekByte()
        {
            if (_pos >= _end) throw Overrun(1);
            return _buffer[_pos];
        }

        /// <summary>A boolean byte; values other than 0 and 1 are rejected (canonical encoding).</summary>
        public bool ReadBool()
        {
            byte b = ReadByte();
            if (b > 1) throw new DecodeException("invalid boolean byte " + b.ToString(CultureInfo.InvariantCulture) + " at position " + (Position - 1).ToString(CultureInfo.InvariantCulture));
            return b == 1;
        }

        public ushort ReadUInt16()
        {
            Need(2);
            byte[] b = _buffer;
            int p = _pos;
            _pos = p + 2;
            return (ushort)(b[p] | (b[p + 1] << 8));
        }

        public uint ReadUInt32()
        {
            Need(4);
            byte[] b = _buffer;
            int p = _pos;
            _pos = p + 4;
            return b[p] | ((uint)b[p + 1] << 8) | ((uint)b[p + 2] << 16) | ((uint)b[p + 3] << 24);
        }

        public ulong ReadUInt64()
        {
            Need(8);
            byte[] b = _buffer;
            int p = _pos;
            _pos = p + 8;
            ulong lo = b[p] | ((uint)b[p + 1] << 8) | ((uint)b[p + 2] << 16) | ((uint)b[p + 3] << 24);
            ulong hi = b[p + 4] | ((uint)b[p + 5] << 8) | ((uint)b[p + 6] << 16) | ((uint)b[p + 7] << 24);
            return lo | (hi << 32);
        }

        public int ReadInt32() => unchecked((int)ReadUInt32());

        public long ReadInt64() => unchecked((long)ReadUInt64());

        /// <summary>Raw IEEE-754 bits (NaN payloads are preserved).</summary>
        public unsafe float ReadSingle()
        {
            uint bits = ReadUInt32();
            return *(float*)&bits;
        }

        /// <summary>Raw IEEE-754 bits (NaN payloads are preserved).</summary>
        public double ReadDouble() => BitConverter.Int64BitsToDouble(unchecked((long)ReadUInt64()));

        /// <summary>LEB128 of at most 5 bytes; values above 32 bits and overlong encodings are rejected.</summary>
        public uint ReadVarUInt32()
        {
            uint result = 0;
            int shift = 0;
            int first = _pos;
            for (int i = 0; i < 5; i++)
            {
                if (_pos >= _end) throw Overrun(1);
                byte b = _buffer[_pos++];
                if (i == 4 && b > 0x0F) throw Invalid("varint exceeds 32 bits", first);
                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    if (b == 0 && i > 0) throw Invalid("overlong varint", first);
                    return result;
                }
                shift += 7;
            }
            throw Invalid("varint exceeds 32 bits", first);
        }

        /// <summary>LEB128 of at most 10 bytes; values above 64 bits and overlong encodings are rejected.</summary>
        public ulong ReadVarUInt64()
        {
            ulong result = 0;
            int shift = 0;
            int first = _pos;
            for (int i = 0; i < 10; i++)
            {
                if (_pos >= _end) throw Overrun(1);
                byte b = _buffer[_pos++];
                if (i == 9 && b > 0x01) throw Invalid("varint exceeds 64 bits", first);
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    if (b == 0 && i > 0) throw Invalid("overlong varint", first);
                    return result;
                }
                shift += 7;
            }
            throw Invalid("varint exceeds 64 bits", first);
        }

        /// <summary>Zig-zag LEB128 (see <see cref="ByteWriter.WriteVarInt32"/>).</summary>
        public int ReadVarInt32()
        {
            uint u = ReadVarUInt32();
            return unchecked((int)(u >> 1) ^ -(int)(u & 1));
        }

        /// <summary>Zig-zag LEB128 (see <see cref="ByteWriter.WriteVarInt64"/>).</summary>
        public long ReadVarInt64()
        {
            ulong u = ReadVarUInt64();
            return unchecked((long)(u >> 1) ^ -(long)(u & 1));
        }

        /// <summary>Copies <paramref name="count"/> bytes into <paramref name="dst"/>. A negative count or one beyond the
        /// remaining bytes is a decode error; an invalid destination range is an argument error.</summary>
        public void ReadBytes(byte[] dst, int offset, int count)
        {
            if (count < 0 || count > Remaining) throw Overrun(count);
            if (dst == null) throw new ArgumentNullException(nameof(dst));
            if (offset < 0 || offset > dst.Length - count)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), "the range lies outside the destination array");
            }
            System.Buffer.BlockCopy(_buffer, _pos, dst, offset, count);
            _pos += count;
        }

        /// <summary>The next <paramref name="count"/> bytes as a segment of the underlying array (no copy).</summary>
        public ArraySegment<byte> ReadSegment(int count)
        {
            if (count < 0 || count > Remaining) throw Overrun(count);
            var segment = new ArraySegment<byte>(_buffer, _pos, count);
            _pos += count;
            return segment;
        }

        /// <summary>
        /// A string written by <see cref="ByteWriter.WriteString"/>. A declared length above
        /// <paramref name="maxUtf8Bytes"/> or beyond the window is rejected before anything is decoded; invalid UTF-8
        /// sequences decode to U+FFFD.
        /// </summary>
        public string ReadString(int maxUtf8Bytes)
        {
            int first = _pos;
            uint length = ReadVarUInt32();
            if (length > (uint)Math.Max(0, maxUtf8Bytes))
            {
                throw Invalid("string of " + length.ToString(CultureInfo.InvariantCulture) + " bytes exceeds the limit of "
                              + maxUtf8Bytes.ToString(CultureInfo.InvariantCulture), first);
            }
            if (length > (uint)Remaining) throw Overrun(unchecked((int)length));
            if (length == 0) return string.Empty;
            string s = Utf8.GetString(_buffer, _pos, (int)length);
            _pos += (int)length;
            return s;
        }

        public void Skip(int count)
        {
            if (count < 0 || count > Remaining) throw Overrun(count);
            _pos += count;
        }

        private void Need(int count)
        {
            if (_end - _pos < count) throw Overrun(count);
        }

        private DecodeException Overrun(int count)
        {
            return new DecodeException("read of " + count.ToString(CultureInfo.InvariantCulture) + " byte(s) at position "
                                       + Position.ToString(CultureInfo.InvariantCulture) + " overruns the buffer ("
                                       + Remaining.ToString(CultureInfo.InvariantCulture) + " remaining)");
        }

        private DecodeException Invalid(string what, int absolutePosition)
        {
            return new DecodeException(what + " at position " + (absolutePosition - _start).ToString(CultureInfo.InvariantCulture));
        }
    }
}
