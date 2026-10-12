using System;
using System.Diagnostics;
using System.Globalization;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Replication
{
    /// <summary>
    /// The host's value shadow (ARCH §4.5.2): the canonical bytes of the last captured value of every pair of the classes
    /// delivered per peer (V, A, A-low, M discrete state). It is the only source the workers read when they encode a
    /// peer's value records, so no ECS access happens off the main thread and a value that changed ten times while
    /// waiting is sent once, in its latest form.
    /// <para>
    /// <b>Storage</b>: an unmanaged arena of per-slot blocks. A block holds the slot's values sorted by type rank:
    /// <c>i32 blockBytes | u32 slot | u16 count | u16 0 | i32 payloadBytes | count × (u16 type | u16 0 | i32 length) |
    /// payloads</c>, 8-byte aligned. A per-slot index (pages of 4096 slots) points at the block. A value of unchanged
    /// length is overwritten in place; otherwise the block is rewritten at the end of the arena and the old one becomes
    /// garbage, which <see cref="Compact"/> reclaims incrementally by sliding live blocks down.
    /// </para>
    /// <para>
    /// <b>Threading</b>: mutated on the main thread inside the capture window; <see cref="TryCopy"/> is called by workers
    /// outside the window (the read window, ARCH §4.5.5). No locks.
    /// </para>
    /// </summary>
    public sealed unsafe class ValueShadow : IDisposable
    {
        private const int PageShift = 12;
        private const int PageSize = 1 << PageShift;
        private const int PageMask = PageSize - 1;
        private const int HeaderBytes = 16;
        private const int EntryBytes = 8;
        private const long FirstBlock = HeaderBytes;            // offset 0 holds a sentinel, so 0 means "no block"
        private const int MaxValueBytes = 64 * 1024 * 1024;
        private const int SlotPageLimit = (int)((NetSlot.MaxValue + 1) >> PageShift);

        private readonly NativeBuffer _arena;
        private long[][] _index = new long[16][];
        private int _indexPages;
        private long _used = FirstBlock;
        private long _garbage;
        private long _liveBytes;
        private int _valueCount;
        private int _slotCount;
        private bool _compacting;
        private long _scan;
        private long _destination;
        private long _garbageBehind;

        public ValueShadow(int initialArenaBytes = 16 << 20)
        {
            if (initialArenaBytes < 4096) initialArenaBytes = 4096;
            _arena = new NativeBuffer(initialArenaBytes);
        }

        /// <summary>Values stored.</summary>
        public int ValueCount => _valueCount;

        /// <summary>Slots with at least one value.</summary>
        public int SlotCount => _slotCount;

        /// <summary>Bytes of live blocks (headers included).</summary>
        public long LiveBytes => _liveBytes;

        /// <summary>Bytes of dead blocks waiting for compaction.</summary>
        public long GarbageBytes => _garbage;

        /// <summary>Share of the used arena that is garbage (0 ... 1).</summary>
        public double GarbageRatio => _used <= FirstBlock ? 0 : (double)_garbage / (_used - FirstBlock);

        /// <summary>True while an incremental compaction cycle is in progress.</summary>
        public bool IsCompacting => _compacting;

        /// <summary>Unmanaged arena plus index bytes (diagnostics, ARCH §15.2).</summary>
        public long MemoryBytes => _arena.Capacity + (long)_indexPages * PageSize * sizeof(long) + _index.Length * IntPtr.Size;

        /// <summary>Stores the canonical bytes of a pair (replacing the previous value).</summary>
        public void Set(NetSlot slot, TypeRank type, byte[] canonical, int offset, int count)
        {
            if (slot.IsNone) throw new ArgumentException("NetSlot.None has no values", nameof(slot));
            if (count < 0 || count > MaxValueBytes) throw new ArgumentOutOfRangeException(nameof(count));
            if (count > 0 && (canonical == null || offset < 0 || offset > canonical.Length - count)) throw new ArgumentOutOfRangeException(nameof(offset));
            ref long slotRef = ref IndexRef(slot.Value);
            long block = slotRef;
            if (block != 0)
            {
                byte* b = _arena.Pointer + block;
                int n = *(ushort*)(b + 8);
                long payload = PayloadStart(n);
                for (int i = 0; i < n; i++)
                {
                    byte* entry = b + HeaderBytes + (long)i * EntryBytes;
                    int length = *(int*)(entry + 4);
                    if (*(ushort*)entry == type.Value)
                    {
                        if (length == count)
                        {
                            NativeBuffer.CopyIn(canonical, offset, b + payload, count);   // same length: in place
                            return;
                        }
                        break;
                    }
                    payload += length;
                }
            }
            Rewrite(slot.Value, ref slotRef, type.Value, canonical, offset, count, false);
        }

        /// <summary>Appends the stored bytes of a pair to <paramref name="output"/>; false when absent. Worker threads, read window only.</summary>
        [ThreadAffinity(ThreadKind.Any)]
        public bool TryCopy(NetSlot slot, TypeRank type, ByteWriter output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (!TryLocate(slot, type, out long payload, out int length)) return false;
            if (length > 0)
            {
                int at = output.Reserve(length);
                fixed (byte* d = &output.Buffer[at])
                {
                    Buffer.MemoryCopy(_arena.Pointer + payload, d, length, length);
                }
            }
            return true;
        }

        /// <summary>True when the pair is stored; <paramref name="length"/> receives its byte count.</summary>
        [ThreadAffinity(ThreadKind.Any)]
        public bool TryGetLength(NetSlot slot, TypeRank type, out int length)
        {
            return TryLocate(slot, type, out _, out length);
        }

        /// <summary>True when the pair is stored.</summary>
        [ThreadAffinity(ThreadKind.Any)]
        public bool Contains(NetSlot slot, TypeRank type) => TryLocate(slot, type, out _, out _);

        /// <summary>Removes one pair; true when it was stored.</summary>
        public bool Remove(NetSlot slot, TypeRank type)
        {
            if (slot.IsNone || !TryLocate(slot, type, out _, out _)) return false;
            ref long slotRef = ref IndexRef(slot.Value);
            Rewrite(slot.Value, ref slotRef, type.Value, null, 0, 0, true);
            return true;
        }

        /// <summary>Removes every value of a slot (despawn).</summary>
        public void RemoveSlot(NetSlot slot)
        {
            if (slot.IsNone) return;
            int pageIndex = (int)(slot.Value >> PageShift);
            if (pageIndex >= _index.Length || _index[pageIndex] == null) return;
            ref long slotRef = ref _index[pageIndex][slot.Value & PageMask];
            if (slotRef == 0) return;
            FreeBlock(slotRef);
            slotRef = 0;
            _slotCount--;
        }

        /// <summary>
        /// Reclaims garbage incrementally for at most <paramref name="maxMilliseconds"/>: starts a cycle when garbage exists
        /// and continues a running one. Main thread, capture window.
        /// </summary>
        public void Compact(double maxMilliseconds)
        {
            long deadline = Stopwatch.GetTimestamp() + (long)(Math.Max(0, maxMilliseconds) * Stopwatch.Frequency / 1000.0);
            CompactCore(deadline, int.MaxValue);
        }

        /// <summary>Compaction bounded by a number of visited blocks (deterministic; tests and forced full compaction).</summary>
        public void CompactBlocks(int maxBlocks)
        {
            CompactCore(long.MaxValue, maxBlocks);
        }

        /// <summary>Drops every value (engine reset).</summary>
        public void Clear()
        {
            _index = new long[16][];
            _indexPages = 0;
            _used = FirstBlock;
            _garbage = 0;
            _liveBytes = 0;
            _valueCount = 0;
            _slotCount = 0;
            _compacting = false;
            _garbageBehind = 0;
        }

        public void Dispose()
        {
            _arena.Dispose();
        }

        public override string ToString()
        {
            return "ValueShadow(" + _valueCount.ToString(CultureInfo.InvariantCulture) + " values in " + _slotCount.ToString(CultureInfo.InvariantCulture)
                   + " slots, live " + (_liveBytes >> 10).ToString(CultureInfo.InvariantCulture) + " KiB, garbage "
                   + (_garbage >> 10).ToString(CultureInfo.InvariantCulture) + " KiB)";
        }

        // ------------------------------------------------------------------ internals

        private static long PayloadStart(int count) => HeaderBytes + (long)count * EntryBytes;

        private static int Align8(long bytes) => (int)((bytes + 7) & ~7L);

        private bool TryLocate(NetSlot slot, TypeRank type, out long payload, out int length)
        {
            payload = 0;
            length = 0;
            if (slot.IsNone) return false;
            long[][] index = _index;
            int pageIndex = (int)(slot.Value >> PageShift);
            if (pageIndex >= index.Length) return false;
            long[] page = index[pageIndex];
            if (page == null) return false;
            long block = page[slot.Value & PageMask];
            if (block == 0) return false;
            byte* b = _arena.Pointer + block;
            int n = *(ushort*)(b + 8);
            long position = block + PayloadStart(n);
            for (int i = 0; i < n; i++)
            {
                byte* entry = b + HeaderBytes + (long)i * EntryBytes;
                int entryLength = *(int*)(entry + 4);
                ushort entryType = *(ushort*)entry;
                if (entryType == type.Value)
                {
                    payload = position;
                    length = entryLength;
                    return true;
                }
                if (entryType > type.Value) return false;
                position += entryLength;
            }
            return false;
        }

        private ref long IndexRef(uint slot)
        {
            int pageIndex = (int)(slot >> PageShift);
            if (pageIndex >= _index.Length)
            {
                Array.Resize(ref _index, Math.Min(SlotPageLimit, Math.Max(pageIndex + 1, _index.Length * 2)));
            }
            long[] page = _index[pageIndex];
            if (page == null)
            {
                page = new long[PageSize];
                _index[pageIndex] = page;
                _indexPages++;
            }
            return ref page[slot & PageMask];
        }

        /// <summary>Writes a new block for the slot with <paramref name="type"/> replaced, added or (remove) dropped.</summary>
        private void Rewrite(uint slot, ref long slotRef, ushort type, byte[] source, int sourceOffset, int sourceCount, bool remove)
        {
            long oldBlock = slotRef;
            int oldCount = 0;
            int oldPayload = 0;
            bool hadType = false;
            if (oldBlock != 0)
            {
                byte* ob = _arena.Pointer + oldBlock;
                oldCount = *(ushort*)(ob + 8);
                oldPayload = *(int*)(ob + 12);
                for (int i = 0; i < oldCount; i++)
                {
                    if (*(ushort*)(ob + HeaderBytes + (long)i * EntryBytes) == type)
                    {
                        hadType = true;
                        oldPayload -= *(int*)(ob + HeaderBytes + (long)i * EntryBytes + 4);
                        break;
                    }
                }
            }
            if (remove && !hadType) return;
            int newCount = oldCount + (remove ? -1 : hadType ? 0 : 1);
            if (newCount == 0)
            {
                if (oldBlock != 0) FreeBlock(oldBlock);
                slotRef = 0;
                _slotCount--;
                return;
            }
            long payloadBytes = (long)oldPayload + (remove ? 0 : sourceCount);
            int blockBytes = Align8(PayloadStart(newCount) + payloadBytes);
            long newBlock = Allocate(blockBytes);   // may move the arena: take pointers afterwards
            byte* nb = _arena.Pointer + newBlock;
            byte* oldB = oldBlock != 0 ? _arena.Pointer + oldBlock : null;
            *(int*)nb = blockBytes;
            *(uint*)(nb + 4) = slot;
            *(ushort*)(nb + 8) = (ushort)newCount;
            *(ushort*)(nb + 10) = 0;
            *(int*)(nb + 12) = (int)payloadBytes;

            int written = 0;
            long writePayload = PayloadStart(newCount);
            long readPayload = PayloadStart(oldCount);
            bool inserted = remove;
            for (int i = 0; i < oldCount; i++)
            {
                byte* entry = oldB + HeaderBytes + (long)i * EntryBytes;
                ushort entryType = *(ushort*)entry;
                int entryLength = *(int*)(entry + 4);
                if (!inserted && entryType >= type)
                {
                    WriteEntry(nb, written++, type, sourceCount);
                    NativeBuffer.CopyIn(source, sourceOffset, nb + writePayload, sourceCount);
                    writePayload += sourceCount;
                    inserted = true;
                }
                if (entryType != type)
                {
                    WriteEntry(nb, written++, entryType, entryLength);
                    if (entryLength > 0) Buffer.MemoryCopy(oldB + readPayload, nb + writePayload, entryLength, entryLength);
                    writePayload += entryLength;
                }
                readPayload += entryLength;
            }
            if (!inserted)
            {
                WriteEntry(nb, written++, type, sourceCount);
                NativeBuffer.CopyIn(source, sourceOffset, nb + writePayload, sourceCount);
            }

            if (oldBlock != 0) FreeBlock(oldBlock);
            else _slotCount++;
            slotRef = newBlock;
            _liveBytes += blockBytes;
            _valueCount += newCount;
        }

        private static void WriteEntry(byte* block, int index, ushort type, int length)
        {
            byte* entry = block + HeaderBytes + (long)index * EntryBytes;
            *(ushort*)entry = type;
            *(ushort*)(entry + 2) = 0;
            *(int*)(entry + 4) = length;
        }

        private long Allocate(int bytes)
        {
            _arena.EnsureCapacity(_used + bytes);
            long block = _used;
            _used += bytes;
            return block;
        }

        private void FreeBlock(long block)
        {
            byte* b = _arena.Pointer + block;
            int bytes = *(int*)b;
            int count = *(ushort*)(b + 8);
            *(uint*)(b + 4) = 0;
            _garbage += bytes;
            _liveBytes -= bytes;
            _valueCount -= count;
            if (_compacting)
            {
                // a hole in the part this cycle already compacted survives the cycle; the next cycle reclaims it
                if (block < _destination) _garbageBehind += bytes;
                return;
            }
            if (block + bytes == _used)
            {
                // the newest block: give the space back at once
                _used = block;
                _garbage -= bytes;
            }
        }

        private void CompactCore(long deadline, int maxBlocks)
        {
            if (!_compacting)
            {
                if (_garbage == 0) return;
                _compacting = true;
                _scan = FirstBlock;
                _destination = FirstBlock;
            }
            int visited = 0;
            byte* arena = _arena.Pointer;
            while (_scan < _used)
            {
                byte* b = arena + _scan;
                int bytes = *(int*)b;
                if (bytes <= 0 || (bytes & 7) != 0 || _scan + bytes > _used)
                {
                    // corrupt arena: abandon (never loop forever or move garbage); live blocks keep their places
                    _compacting = false;
                    _garbageBehind = 0;
                    return;
                }
                uint slot = *(uint*)(b + 4);
                if (slot != 0)
                {
                    if (_destination != _scan)
                    {
                        MoveDown(arena + _scan, arena + _destination, bytes);
                        IndexRef(slot) = _destination;
                    }
                    _destination += bytes;
                }
                _scan += bytes;
                visited++;
                if (visited >= maxBlocks) return;
                if ((visited & 63) == 0 && Stopwatch.GetTimestamp() > deadline) return;
            }
            _used = _destination;
            _garbage = _garbageBehind;
            _garbageBehind = 0;
            _compacting = false;
        }

        /// <summary>Copies <paramref name="bytes"/> (a multiple of 8) to a lower, possibly overlapping address.</summary>
        private static void MoveDown(byte* source, byte* destination, int bytes)
        {
            ulong* s = (ulong*)source;
            ulong* d = (ulong*)destination;
            int words = bytes >> 3;
            for (int i = 0; i < words; i++) d[i] = s[i];
        }
    }
}
