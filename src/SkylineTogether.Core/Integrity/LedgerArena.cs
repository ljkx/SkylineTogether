using System;

namespace SkylineTogether.Core.Integrity
{
    /// <summary>
    /// Segmented storage of variable-size blocks of 64-bit entries for <see cref="HashLedger"/>: one block per slot, with
    /// power-of-two capacities (2 ... 65536 entries) recycled through free lists, so memory follows the number of
    /// recorded pairs (8 bytes each plus rounding) rather than slots × types. Segments of 1 MiB are never copied when the
    /// arena grows; a block never crosses a segment.
    /// <para>
    /// A block handle is the global index of the block's header entry. Header layout: bits 0-23 = used entries,
    /// bits 24-31 = log2 of the capacity; for a free block bits 32-63 hold the next free handle + 1 (0 = end of list).
    /// Entries follow the header. Not thread-safe (main thread, like the ledger).
    /// </para>
    /// </summary>
    internal sealed class LedgerArena
    {
        internal const int MaxLog2 = 16;
        internal const int MaxEntriesPerBlock = 1 << MaxLog2;
        private const int MinLog2 = 1;
        private const int SegmentShift = 17;
        private const int SegmentSize = 1 << SegmentShift;          // 131072 entries = 1 MiB
        private const int SegmentMask = SegmentSize - 1;
        private const ulong UsedMask = 0xFFFFFF;

        private ulong[][] _segments = new ulong[8][];
        private int _segmentCount;
        private int _tail = SegmentSize;                             // the first allocation opens a segment
        private readonly int[] _freeHeads = new int[MaxLog2 + 1];   // handle + 1 of the first free block, 0 = none
        private long _blockEntries;                                  // entries of live blocks (headers included)

        /// <summary>Bytes held by the segments.</summary>
        internal long MemoryBytes => (long)_segmentCount * SegmentSize * sizeof(ulong) + _segments.Length * IntPtr.Size;

        /// <summary>Entries of live blocks, headers included (for statistics).</summary>
        internal long LiveEntries => _blockEntries;

        /// <summary>Allocates an empty block with room for at least <paramref name="minEntries"/> entries.</summary>
        internal int Allocate(int minEntries)
        {
            int log2 = Log2For(minEntries);
            int handle;
            int free = _freeHeads[log2];
            if (free != 0)
            {
                handle = free - 1;
                ulong header = Header(handle);
                _freeHeads[log2] = (int)(header >> 32);
            }
            else
            {
                int size = (1 << log2) + 1;
                if (_tail + size > SegmentSize) OpenSegment();
                handle = ((_segmentCount - 1) << SegmentShift) | _tail;
                _tail += size;
            }
            SetHeader(handle, (ulong)log2 << 24);
            _blockEntries += (1 << log2) + 1;
            return handle;
        }

        /// <summary>Returns a block to its free list.</summary>
        internal void Free(int handle)
        {
            int log2 = Log2(handle);
            SetHeader(handle, ((ulong)(uint)_freeHeads[log2] << 32) | ((ulong)log2 << 24));
            _freeHeads[log2] = handle + 1;
            _blockEntries -= (1 << log2) + 1;
        }

        /// <summary>Moves the used entries into a block with room for at least <paramref name="minEntries"/> and frees the old one.</summary>
        internal int Grow(int handle, int minEntries)
        {
            int used = Used(handle);
            int grown = Allocate(minEntries);
            ulong[] from = _segments[handle >> SegmentShift];
            ulong[] to = _segments[grown >> SegmentShift];
            Array.Copy(from, (handle & SegmentMask) + 1, to, (grown & SegmentMask) + 1, used);
            SetUsed(grown, used);
            Free(handle);
            return grown;
        }

        internal int Capacity(int handle) => 1 << Log2(handle);

        internal int Used(int handle) => (int)(Header(handle) & UsedMask);

        internal void SetUsed(int handle, int used)
        {
            ulong header = Header(handle);
            SetHeader(handle, (header & ~UsedMask) | (uint)used);
        }

        internal ulong Get(int handle, int index) => _segments[handle >> SegmentShift][(handle & SegmentMask) + 1 + index];

        internal void Set(int handle, int index, ulong value) => _segments[handle >> SegmentShift][(handle & SegmentMask) + 1 + index] = value;

        /// <summary>Removes <paramref name="count"/> entries at <paramref name="index"/>, shifting the tail down (keeps order).</summary>
        internal void RemoveRange(int handle, int index, int count)
        {
            int used = Used(handle);
            ulong[] segment = _segments[handle >> SegmentShift];
            int first = (handle & SegmentMask) + 1;
            int tail = used - index - count;
            if (tail > 0) Array.Copy(segment, first + index + count, segment, first + index, tail);
            SetUsed(handle, used - count);
        }

        /// <summary>Releases every segment.</summary>
        internal void Clear()
        {
            _segments = new ulong[8][];
            _segmentCount = 0;
            _tail = SegmentSize;
            Array.Clear(_freeHeads, 0, _freeHeads.Length);
            _blockEntries = 0;
        }

        private static int Log2For(int minEntries)
        {
            if (minEntries > MaxEntriesPerBlock)
            {
                throw new InvalidOperationException("a ledger block holds at most " + MaxEntriesPerBlock + " entries");
            }
            int log2 = MinLog2;
            while ((1 << log2) < minEntries) log2++;
            return log2;
        }

        private int Log2(int handle) => (int)((Header(handle) >> 24) & 0xFF);

        private ulong Header(int handle) => _segments[handle >> SegmentShift][handle & SegmentMask];

        private void SetHeader(int handle, ulong value) => _segments[handle >> SegmentShift][handle & SegmentMask] = value;

        private void OpenSegment()
        {
            if (_segmentCount == _segments.Length)
            {
                var grown = new ulong[_segments.Length * 2][];
                Array.Copy(_segments, grown, _segments.Length);
                _segments = grown;
            }
            _segments[_segmentCount++] = new ulong[SegmentSize];
            _tail = 0;
        }
    }
}
