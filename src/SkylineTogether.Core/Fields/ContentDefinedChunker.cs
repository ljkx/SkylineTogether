using System;
using System.IO;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Fields
{
    /// <summary>
    /// Content-defined chunking for <see cref="BlockDelta"/> (FastCDC-style gear hash with normalised chunking). Chunk
    /// boundaries depend only on the bytes around them, so an insertion or deletion in a file moves the boundaries
    /// after it together with the data, and every unchanged region still produces the same chunks and digests; fixed
    /// offsets would lose every block after the first insertion.
    /// <para>
    /// For an average size A (the block size, rounded down to a power of two for the masks): chunks are at least A/4
    /// and at most 4·A bytes; from A/4 to A a boundary needs log2(A)+2 zero bits in the top of the rolling hash, after A
    /// only log2(A)−2, which pulls the sizes towards A. The gear hash <c>h = (h &lt;&lt; 1) + Gear[byte]</c> depends on
    /// the last 64 bytes only. <see cref="Gear"/> is generated from a fixed seed and pinned by a unit test: host and
    /// client must cut identically.
    /// </para>
    /// <para>
    /// The chunker reads its stream sequentially through one pooled buffer of 8·A bytes; the chunk returned by
    /// <see cref="Next"/> is a view into that buffer, valid until the next call. Not thread-safe.
    /// </para>
    /// </summary>
    internal sealed class ContentDefinedChunker
    {
        /// <summary>Seed of the gear table ("STBLOCK1"); part of the warm-join format.</summary>
        internal const ulong GearSeed = 0x5354424C4F434B31UL;

        /// <summary>256 pseudo-random 64-bit values (SplitMix64 from <see cref="GearSeed"/>); never written after initialisation.</summary>
        internal static readonly ulong[] Gear = CreateGear();

        private readonly int _minSize;
        private readonly int _averageSize;
        private readonly int _maxSize;
        private readonly ulong _maskSmall;
        private readonly ulong _maskLarge;
        private byte[] _buffer;
        private int _start;
        private int _end;
        private Stream _stream;
        private bool _eof;

        internal ContentDefinedChunker(int averageSize)
        {
            if (averageSize < BlockDelta.MinBlockSize || averageSize > BlockDelta.MaxBlockSize) throw new ArgumentOutOfRangeException(nameof(averageSize));
            _averageSize = averageSize;
            _minSize = averageSize / 4;
            _maxSize = averageSize * 4;
            int bits = Log2Floor(averageSize);
            _maskSmall = TopBits(bits + 2);
            _maskLarge = TopBits(Math.Max(bits - 2, 1));
        }

        internal int MinSize => _minSize;

        internal int MaxSize => _maxSize;

        /// <summary>Starts reading <paramref name="stream"/> from its current position.</summary>
        internal void Start(Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            if (_buffer == null) _buffer = BufferPool.Rent(2 * _maxSize);
            _start = 0;
            _end = 0;
            _eof = false;
        }

        /// <summary>The next chunk (a view into the internal buffer, valid until the next call); false at the end.</summary>
        internal bool Next(out byte[] buffer, out int offset, out int length)
        {
            if (_stream == null) throw new InvalidOperationException("Start was not called");
            if (_end - _start < _maxSize && !_eof) Fill();
            int available = _end - _start;
            if (available == 0)
            {
                buffer = null;
                offset = 0;
                length = 0;
                return false;
            }
            int cut = FindCut(_buffer, _start, Math.Min(available, _maxSize));
            buffer = _buffer;
            offset = _start;
            length = cut;
            _start += cut;
            return true;
        }

        /// <summary>Returns the buffer to the pool; the chunker can be started again afterwards.</summary>
        internal void Finish()
        {
            if (_buffer != null)
            {
                BufferPool.Return(_buffer);
                _buffer = null;
            }
            _stream = null;
        }

        /// <summary>The boundary in <c>data[start .. start + n)</c>; only n &lt; MaxSize at the end of the stream.</summary>
        internal int FindCut(byte[] data, int start, int n)
        {
            if (n <= _minSize) return n;
            ulong[] gear = Gear;
            ulong hash = 0;
            int i = _minSize;
            int normal = Math.Min(_averageSize, n);
            ulong mask = _maskSmall;
            for (; i < normal; i++)
            {
                hash = unchecked((hash << 1) + gear[data[start + i]]);
                if ((hash & mask) == 0) return i + 1;
            }
            mask = _maskLarge;
            for (; i < n; i++)
            {
                hash = unchecked((hash << 1) + gear[data[start + i]]);
                if ((hash & mask) == 0) return i + 1;
            }
            return n;
        }

        private void Fill()
        {
            if (_start > 0)
            {
                int kept = _end - _start;
                if (kept > 0) Buffer.BlockCopy(_buffer, _start, _buffer, 0, kept);
                _start = 0;
                _end = kept;
            }
            while (_end < _buffer.Length)
            {
                int read = _stream.Read(_buffer, _end, _buffer.Length - _end);
                if (read <= 0)
                {
                    _eof = true;
                    break;
                }
                _end += read;
            }
        }

        private static ulong TopBits(int count) => count >= 64 ? ulong.MaxValue : ulong.MaxValue << (64 - count);

        private static int Log2Floor(int value)
        {
            int bits = 0;
            while ((value >>= 1) != 0) bits++;
            return bits;
        }

        private static ulong[] CreateGear()
        {
            var table = new ulong[256];
            ulong x = GearSeed;
            for (int i = 0; i < table.Length; i++)
            {
                unchecked
                {
                    x += 0x9E3779B97F4A7C15UL;
                    ulong z = x;
                    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                    z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                    table[i] = z ^ (z >> 31);
                }
            }
            return table;
        }
    }
}
