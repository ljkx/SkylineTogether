using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Motion
{
    /// <summary>
    /// Bit packer for the bodies of motion records: fields are appended least-significant bit first, bytes are emitted
    /// as they fill, and <see cref="Flush"/> pads the last byte with zero bits, so every record ends on a byte boundary
    /// and can be decoded on its own (ARCH §4.9.3). A mutable struct: always pass it by <c>ref</c>.
    /// </summary>
    internal struct MotionBitWriter
    {
        private readonly ByteWriter _writer;
        private ulong _accumulator;
        private int _bits;

        public MotionBitWriter(ByteWriter writer)
        {
            _writer = writer;
            _accumulator = 0;
            _bits = 0;
        }

        /// <summary>Appends the low <paramref name="count"/> bits (1..32) of <paramref name="value"/>.</summary>
        public void Write(uint value, int count)
        {
            ulong mask = count >= 32 ? 0xFFFFFFFFUL : (1UL << count) - 1UL;
            _accumulator |= (value & mask) << _bits;
            _bits += count;
            while (_bits >= 8)
            {
                _writer.WriteByte((byte)_accumulator);
                _accumulator >>= 8;
                _bits -= 8;
            }
        }

        /// <summary>Appends a two's complement value in <paramref name="count"/> bits; the caller checked the range.</summary>
        public void WriteSigned(int value, int count) => Write(unchecked((uint)value), count);

        public void WriteBool(bool value) => Write(value ? 1u : 0u, 1);

        /// <summary>Pads to the next byte boundary with zero bits and emits the last partial byte.</summary>
        public void Flush()
        {
            if (_bits > 0)
            {
                _writer.WriteByte((byte)_accumulator);
                _accumulator = 0;
                _bits = 0;
            }
        }
    }

    /// <summary>
    /// Reader for <see cref="MotionBitWriter"/> output. Overruns throw <see cref="DecodeException"/> through the
    /// underlying <see cref="ByteReader"/>; <see cref="End"/> checks that the padding bits of the last byte are zero
    /// (canonical encoding). A mutable struct: always pass it by <c>ref</c>.
    /// </summary>
    internal struct MotionBitReader
    {
        private readonly ByteReader _reader;
        private ulong _accumulator;
        private int _bits;

        public MotionBitReader(ByteReader reader)
        {
            _reader = reader;
            _accumulator = 0;
            _bits = 0;
        }

        /// <summary>The next <paramref name="count"/> bits (1..32) as an unsigned value.</summary>
        public uint Read(int count)
        {
            while (_bits < count)
            {
                _accumulator |= (ulong)_reader.ReadByte() << _bits;
                _bits += 8;
            }
            ulong mask = count >= 32 ? 0xFFFFFFFFUL : (1UL << count) - 1UL;
            uint value = (uint)(_accumulator & mask);
            _accumulator >>= count;
            _bits -= count;
            return value;
        }

        /// <summary>The next <paramref name="count"/> bits as a sign-extended two's complement value.</summary>
        public int ReadSigned(int count)
        {
            uint raw = Read(count);
            int shift = 32 - count;
            return unchecked((int)(raw << shift)) >> shift;
        }

        public bool ReadBool() => Read(1) != 0;

        /// <summary>Finishes the record: the remaining bits of the current byte must be zero padding.</summary>
        public void End()
        {
            if (_bits > 0 && (_accumulator & ((1UL << _bits) - 1UL)) != 0)
            {
                throw new DecodeException("motion record has non-zero padding bits");
            }
            _accumulator = 0;
            _bits = 0;
        }
    }
}
