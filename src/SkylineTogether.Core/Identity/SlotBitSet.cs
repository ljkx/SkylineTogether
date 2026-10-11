using System;

namespace SkylineTogether.Core.Identity
{
    /// <summary>
    /// Growable two-level bit set over the integers <c>0 .. maxItems-1</c> (slot values): one bit per item plus one
    /// summary bit per 64-bit word, so ascending iteration and "lowest set bit" searches skip empty regions in O(1) per
    /// 4096 items. Used for the bound and unspawned sets of <see cref="SlotTable"/> and the free pool of
    /// <see cref="SlotAllocator"/>: deterministic (ascending) order, no allocation except when growing.
    /// Memory grows with the highest item ever set (2 MiB for the whole 24-bit slot space), never with a hostile
    /// value beyond <c>maxItems</c>. Not thread-safe.
    /// </summary>
    internal sealed class SlotBitSet
    {
        private const ulong DeBruijn = 0x03F79D71B4CB0A89UL;
        private static readonly int[] DeBruijnIndex = BuildDeBruijnIndex();

        private readonly int _maxItems;
        private readonly int _maxWords;
        private ulong[] _words = Array.Empty<ulong>();
        private ulong[] _summary = Array.Empty<ulong>();
        private int _count;

        /// <param name="maxItems">Exclusive upper bound of the items (at most 2^30).</param>
        public SlotBitSet(int maxItems)
        {
            if (maxItems <= 0 || maxItems > (1 << 30)) throw new ArgumentOutOfRangeException(nameof(maxItems));
            _maxItems = maxItems;
            _maxWords = (maxItems + 63) >> 6;
        }

        /// <summary>Number of set items.</summary>
        public int Count => _count;

        /// <summary>Exclusive upper bound of the items this set accepts.</summary>
        public int MaxItems => _maxItems;

        /// <summary>Bytes held by the word and summary arrays.</summary>
        public long MemoryBytes => ((long)_words.Length + _summary.Length) * sizeof(ulong);

        /// <summary>Pre-sizes the arrays for items below <paramref name="items"/> (clamped to the maximum).</summary>
        public void EnsureCapacity(int items)
        {
            if (items <= 0) return;
            if (items > _maxItems) items = _maxItems;
            EnsureWord((items - 1) >> 6);
        }

        public bool Get(int index)
        {
            int word = index >> 6;
            if (index < 0 || word >= _words.Length) return false;
            return (_words[word] & (1UL << (index & 63))) != 0;
        }

        /// <summary>Sets the item; returns true when it was not set before.</summary>
        public bool Set(int index)
        {
            if (index < 0 || index >= _maxItems) throw new ArgumentOutOfRangeException(nameof(index), index, "outside the bit set");
            int word = index >> 6;
            EnsureWord(word);
            ulong bit = 1UL << (index & 63);
            ulong before = _words[word];
            if ((before & bit) != 0) return false;
            _words[word] = before | bit;
            if (before == 0) _summary[word >> 6] |= 1UL << (word & 63);
            _count++;
            return true;
        }

        /// <summary>Clears the item; returns true when it was set.</summary>
        public bool Clear(int index)
        {
            int word = index >> 6;
            if (index < 0 || word >= _words.Length) return false;
            ulong bit = 1UL << (index & 63);
            ulong before = _words[word];
            if ((before & bit) == 0) return false;
            ulong after = before & ~bit;
            _words[word] = after;
            if (after == 0) _summary[word >> 6] &= ~(1UL << (word & 63));
            _count--;
            return true;
        }

        /// <summary>Sets every item in <c>[fromInclusive, toExclusive)</c>.</summary>
        public void SetRange(int fromInclusive, int toExclusive)
        {
            if (fromInclusive < 0 || toExclusive > _maxItems || fromInclusive > toExclusive)
            {
                throw new ArgumentOutOfRangeException(nameof(toExclusive), "invalid range [" + fromInclusive + ", " + toExclusive + ")");
            }
            if (fromInclusive == toExclusive) return;
            int firstWord = fromInclusive >> 6;
            int lastWord = (toExclusive - 1) >> 6;
            EnsureWord(lastWord);
            for (int w = firstWord; w <= lastWord; w++)
            {
                ulong mask = ulong.MaxValue;
                if (w == firstWord) mask &= ulong.MaxValue << (fromInclusive & 63);
                if (w == lastWord)
                {
                    int top = toExclusive & 63;
                    if (top != 0) mask &= ulong.MaxValue >> (64 - top);
                }
                ulong before = _words[w];
                ulong after = before | mask;
                if (after == before) continue;
                _count += PopCount(after & ~before);
                _words[w] = after;
                _summary[w >> 6] |= 1UL << (w & 63);
            }
        }

        /// <summary>The lowest set item at or above <paramref name="fromInclusive"/>, or -1.</summary>
        public int NextSetBit(int fromInclusive)
        {
            if (fromInclusive < 0) fromInclusive = 0;
            int word = fromInclusive >> 6;
            if (word >= _words.Length) return -1;
            ulong bits = _words[word] & (ulong.MaxValue << (fromInclusive & 63));
            if (bits != 0) return (word << 6) + TrailingZeroCount(bits);

            // find the next non-empty word through the summary
            int nextWord = word + 1;
            if (nextWord >= _words.Length) return -1;
            int s = nextWord >> 6;
            ulong summaryBits = _summary[s] & (ulong.MaxValue << (nextWord & 63));
            while (true)
            {
                if (summaryBits != 0)
                {
                    int w = (s << 6) + TrailingZeroCount(summaryBits);
                    return (w << 6) + TrailingZeroCount(_words[w]);
                }
                s++;
                if (s >= _summary.Length) return -1;
                summaryBits = _summary[s];
            }
        }

        /// <summary>Clears every item and releases the arrays.</summary>
        public void Reset()
        {
            _words = Array.Empty<ulong>();
            _summary = Array.Empty<ulong>();
            _count = 0;
        }

        private void EnsureWord(int word)
        {
            if (word < _words.Length) return;
            if (word >= _maxWords) throw new ArgumentOutOfRangeException(nameof(word));
            long grown = Math.Max(Math.Max((long)word + 1, (long)_words.Length * 2), 16);
            if (grown > _maxWords) grown = _maxWords;
            Array.Resize(ref _words, (int)grown);
            int summaryLength = (int)((grown + 63) >> 6);
            if (summaryLength != _summary.Length) Array.Resize(ref _summary, summaryLength);
        }

        /// <summary>Index of the lowest set bit of a non-zero value (de Bruijn multiplication; netstandard2.0 has no
        /// <c>BitOperations</c>).</summary>
        internal static int TrailingZeroCount(ulong value)
        {
            return DeBruijnIndex[unchecked((value & (0UL - value)) * DeBruijn) >> 58];
        }

        internal static int PopCount(ulong value)
        {
            unchecked
            {
                value -= (value >> 1) & 0x5555555555555555UL;
                value = (value & 0x3333333333333333UL) + ((value >> 2) & 0x3333333333333333UL);
                value = (value + (value >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
                return (int)((value * 0x0101010101010101UL) >> 56);
            }
        }

        private static int[] BuildDeBruijnIndex()
        {
            // the table is derived from the constant itself, so it cannot contain a transcription error
            var table = new int[64];
            for (int i = 0; i < 64; i++)
            {
                table[unchecked((1UL << i) * DeBruijn) >> 58] = i;
            }
            return table;
        }
    }
}
