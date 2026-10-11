using System;

namespace SkylineTogether.Core
{
    /// <summary>
    /// Append-only byte arena holding the payloads of one <see cref="ApplyPlan"/> (ARCH §4.7). Payload ops refer to
    /// ranges by <see cref="PayloadRef"/>, so merging epochs copies bytes once and allocates nothing per record.
    /// <see cref="Buffer"/> may be replaced when the arena grows: never keep it across an <see cref="Append"/>.
    /// Not thread-safe; a plan belongs to one thread at a time.
    /// </summary>
    public sealed class PayloadArena
    {
        /// <summary>Default initial capacity in bytes.</summary>
        public const int DefaultCapacity = 4096;

        /// <summary>A buffer larger than this is released by <see cref="Clear"/> (after a huge catch-up plan).</summary>
        public const int RetainLimitBytes = 32 * 1024 * 1024;

        private const int MaxCapacity = 0x7FFFFFC7;   // largest byte[] length the runtimes allow

        private readonly int _initialCapacity;
        private byte[] _buffer;
        private int _length;

        public PayloadArena() : this(DefaultCapacity)
        {
        }

        public PayloadArena(int initialCapacity)
        {
            if (initialCapacity < 0) throw new ArgumentOutOfRangeException(nameof(initialCapacity));
            _initialCapacity = initialCapacity;
            _buffer = initialCapacity == 0 ? Array.Empty<byte>() : new byte[initialCapacity];
        }

        /// <summary>The backing array; bytes beyond <see cref="Length"/> are unspecified.</summary>
        public byte[] Buffer => _buffer;

        /// <summary>Number of bytes appended since the last <see cref="Clear"/>.</summary>
        public int Length => _length;

        /// <summary>Copies <paramref name="count"/> bytes to the end of the arena and returns their range.</summary>
        public PayloadRef Append(byte[] src, int offset, int count)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (offset < 0 || count < 0 || offset > src.Length - count)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "the range lies outside the source array");
            }
            EnsureCapacity(count);
            System.Buffer.BlockCopy(src, offset, _buffer, _length, count);
            var result = new PayloadRef(_length, count);
            _length += count;
            return result;
        }

        /// <summary>Forgets every payload; releases the buffer when it grew beyond <see cref="RetainLimitBytes"/>.</summary>
        public void Clear()
        {
            _length = 0;
            if (_buffer.Length > RetainLimitBytes)
            {
                _buffer = _initialCapacity == 0 ? Array.Empty<byte>() : new byte[_initialCapacity];
            }
        }

        /// <summary>True when <paramref name="payload"/> lies inside the appended bytes.</summary>
        public bool Contains(PayloadRef payload)
        {
            return payload.Offset >= 0 && payload.Length >= 0 && payload.Offset <= _length - payload.Length;
        }

        /// <summary>The bytes of <paramref name="payload"/> (no copy); throws when the range is not inside the arena.</summary>
        public ArraySegment<byte> Segment(PayloadRef payload)
        {
            if (!Contains(payload))
            {
                throw new ArgumentOutOfRangeException(nameof(payload), "the payload range lies outside the arena");
            }
            return new ArraySegment<byte>(_buffer, payload.Offset, payload.Length);
        }

        private void EnsureCapacity(int extra)
        {
            long needed = (long)_length + extra;
            if (needed <= _buffer.Length) return;
            if (needed > MaxCapacity) throw new InvalidOperationException("payload arena exceeds the maximum array size");
            long capacity = Math.Max(Math.Max((long)_buffer.Length * 2, needed), 256);
            if (capacity > MaxCapacity) capacity = MaxCapacity;
            var grown = new byte[capacity];
            System.Buffer.BlockCopy(_buffer, 0, grown, 0, _length);
            _buffer = grown;
        }
    }
}
