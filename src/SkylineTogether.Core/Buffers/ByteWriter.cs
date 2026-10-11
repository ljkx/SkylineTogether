using System;
using System.Text;

namespace SkylineTogether.Core.Buffers
{
    /// <summary>
    /// Growable little-endian byte writer for every Core wire format (ARCH §4.6: LEB128 varints, zig-zag for signed
    /// varints, raw IEEE-754 bits for floats so NaN payloads survive). Reuse one writer with <see cref="Reset"/> to avoid
    /// allocations per message. <see cref="Buffer"/> is replaced when the writer grows: never keep it across a write.
    /// Not thread-safe.
    /// </summary>
    public sealed class ByteWriter
    {
        private const int MaxCapacity = 0x7FFFFFC7;   // largest byte[] length the runtimes allow
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

        private byte[] _buffer;
        private int _length;

        public ByteWriter(int initialCapacity = 256)
        {
            if (initialCapacity < 0) throw new ArgumentOutOfRangeException(nameof(initialCapacity));
            _buffer = initialCapacity == 0 ? Array.Empty<byte>() : new byte[initialCapacity];
        }

        /// <summary>The backing array; the written bytes are <c>Buffer[0 .. Length-1]</c>.</summary>
        public byte[] Buffer => _buffer;

        /// <summary>Number of bytes written since the last <see cref="Reset"/>.</summary>
        public int Length => _length;

        /// <summary>Current size of the backing array.</summary>
        public int Capacity => _buffer.Length;

        /// <summary>Forgets the written bytes; the backing array is kept.</summary>
        public void Reset()
        {
            _length = 0;
        }

        /// <summary>Drops everything after <paramref name="length"/> (rolls back a partially written record).</summary>
        public void Truncate(int length)
        {
            if (length < 0 || length > _length) throw new ArgumentOutOfRangeException(nameof(length));
            _length = length;
        }

        public void WriteByte(byte v)
        {
            Ensure(1);
            _buffer[_length++] = v;
        }

        /// <summary>One byte, 1 or 0 (<see cref="ByteReader.ReadBool"/> rejects every other value).</summary>
        public void WriteBool(bool v)
        {
            WriteByte(v ? (byte)1 : (byte)0);
        }

        public void WriteUInt16(ushort v)
        {
            Ensure(2);
            byte[] b = _buffer;
            int p = _length;
            b[p] = (byte)v;
            b[p + 1] = (byte)(v >> 8);
            _length = p + 2;
        }

        public void WriteUInt32(uint v)
        {
            Ensure(4);
            byte[] b = _buffer;
            int p = _length;
            b[p] = (byte)v;
            b[p + 1] = (byte)(v >> 8);
            b[p + 2] = (byte)(v >> 16);
            b[p + 3] = (byte)(v >> 24);
            _length = p + 4;
        }

        public void WriteUInt64(ulong v)
        {
            Ensure(8);
            byte[] b = _buffer;
            int p = _length;
            for (int i = 0; i < 8; i++)
            {
                b[p + i] = (byte)(v >> (8 * i));
            }
            _length = p + 8;
        }

        public void WriteInt32(int v) => WriteUInt32(unchecked((uint)v));

        public void WriteInt64(long v) => WriteUInt64(unchecked((ulong)v));

        /// <summary>The raw IEEE-754 bits, little-endian (NaN payloads and negative zero are preserved).</summary>
        public unsafe void WriteSingle(float v) => WriteUInt32(*(uint*)&v);

        /// <summary>The raw IEEE-754 bits, little-endian (NaN payloads and negative zero are preserved).</summary>
        public void WriteDouble(double v) => WriteUInt64(unchecked((ulong)BitConverter.DoubleToInt64Bits(v)));

        /// <summary>LEB128: 7 bits per byte, low bits first, high bit = continuation (1-5 bytes).</summary>
        public void WriteVarUInt32(uint v)
        {
            Ensure(5);
            byte[] b = _buffer;
            int p = _length;
            while (v >= 0x80)
            {
                b[p++] = (byte)(v | 0x80);
                v >>= 7;
            }
            b[p++] = (byte)v;
            _length = p;
        }

        /// <summary>LEB128 (1-10 bytes).</summary>
        public void WriteVarUInt64(ulong v)
        {
            Ensure(10);
            byte[] b = _buffer;
            int p = _length;
            while (v >= 0x80)
            {
                b[p++] = (byte)(v | 0x80);
                v >>= 7;
            }
            b[p++] = (byte)v;
            _length = p;
        }

        /// <summary>Zig-zag (0, -1, 1, -2, ... map to 0, 1, 2, 3, ...) then LEB128.</summary>
        public void WriteVarInt32(int v) => WriteVarUInt32(unchecked((uint)((v << 1) ^ (v >> 31))));

        /// <summary>Zig-zag then LEB128.</summary>
        public void WriteVarInt64(long v) => WriteVarUInt64(unchecked((ulong)((v << 1) ^ (v >> 63))));

        public void WriteBytes(byte[] src, int offset, int count)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (offset < 0 || count < 0 || offset > src.Length - count)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "the range lies outside the source array");
            }
            Ensure(count);
            System.Buffer.BlockCopy(src, offset, _buffer, _length, count);
            _length += count;
        }

        /// <summary>
        /// A string as <c>varint utf8Length</c> followed by the UTF-8 bytes; null is written as the empty string.
        /// Throws <see cref="ArgumentException"/> when the UTF-8 form is longer than <paramref name="maxUtf8Bytes"/>
        /// (callers validate user input against the protocol limits first). Unpaired surrogates become U+FFFD.
        /// </summary>
        public void WriteString(string s, int maxUtf8Bytes)
        {
            if (maxUtf8Bytes < 0) throw new ArgumentOutOfRangeException(nameof(maxUtf8Bytes));
            if (string.IsNullOrEmpty(s))
            {
                WriteVarUInt32(0);
                return;
            }
            int byteCount = Utf8.GetByteCount(s);
            if (byteCount > maxUtf8Bytes)
            {
                throw new ArgumentException("the string has " + byteCount + " UTF-8 bytes, more than the limit of " + maxUtf8Bytes, nameof(s));
            }
            WriteVarUInt32((uint)byteCount);
            Ensure(byteCount);
            Utf8.GetBytes(s, 0, s.Length, _buffer, _length);
            _length += byteCount;
        }

        /// <summary>Reserves <paramref name="count"/> zeroed bytes (for a length patched later); returns their position.</summary>
        public int Reserve(int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            Ensure(count);
            int position = _length;
            Array.Clear(_buffer, position, count);
            _length += count;
            return position;
        }

        /// <summary>Overwrites four already written bytes at <paramref name="position"/> with a little-endian value.</summary>
        public void PatchUInt32(int position, uint value)
        {
            if (position < 0 || position > _length - 4) throw new ArgumentOutOfRangeException(nameof(position));
            byte[] b = _buffer;
            b[position] = (byte)value;
            b[position + 1] = (byte)(value >> 8);
            b[position + 2] = (byte)(value >> 16);
            b[position + 3] = (byte)(value >> 24);
        }

        /// <summary>A copy of the written bytes.</summary>
        public byte[] ToArray()
        {
            if (_length == 0) return Array.Empty<byte>();
            var result = new byte[_length];
            System.Buffer.BlockCopy(_buffer, 0, result, 0, _length);
            return result;
        }

        private void Ensure(int extra)
        {
            if (_buffer.Length - _length >= extra) return;
            long needed = (long)_length + extra;
            if (needed > MaxCapacity) throw new InvalidOperationException("ByteWriter exceeds the maximum array size");
            long capacity = Math.Max(Math.Max((long)_buffer.Length * 2, needed), 256);
            if (capacity > MaxCapacity) capacity = MaxCapacity;
            var grown = new byte[capacity];
            System.Buffer.BlockCopy(_buffer, 0, grown, 0, _length);
            _buffer = grown;
        }
    }
}
