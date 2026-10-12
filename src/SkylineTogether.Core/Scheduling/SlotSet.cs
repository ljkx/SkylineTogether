using System.Collections.Generic;

namespace SkylineTogether.Core.Scheduling
{
    /// <summary>
    /// An immutable set of slots: a paged bitset (pages of 4096 slots, allocated only where a member lives) with O(1)
    /// membership and ascending iteration by <see cref="NextMember"/>. Built once (the in-view set at 1 Hz, closures,
    /// topic sets) and then shared with any number of reader threads without locks; it is never modified after
    /// <see cref="Create"/> returns. Building is O(members + pages), no sort.
    /// </summary>
    internal sealed class SlotSet
    {
        internal const int PageShift = 12;                 // 4096 slots per page
        internal const int WordsPerPage = 1 << (PageShift - 6);

        public static readonly SlotSet Empty = new SlotSet(new ulong[0][], 0);

        private readonly ulong[][] _pages;

        private SlotSet(ulong[][] pages, int count)
        {
            _pages = pages;
            Count = count;
        }

        public int Count { get; }

        /// <summary>
        /// Builds a set from <paramref name="slots"/>: <see cref="NetSlot.None"/> and duplicates are dropped; members beyond
        /// <paramref name="maxCount"/> are dropped too (counted in <paramref name="dropped"/>). A null list is empty.
        /// </summary>
        public static SlotSet Create(IReadOnlyList<NetSlot> slots, int maxCount, out int dropped)
        {
            dropped = 0;
            if (slots == null || slots.Count == 0) return Empty;
            int highest = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                uint v = slots[i].Value;
                if (v > highest) highest = (int)v;
            }
            if (highest == 0) return Empty;
            var pages = new ulong[(highest >> PageShift) + 1][];
            int count = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                int v = (int)slots[i].Value;
                if (v == 0) continue;
                ulong[] page = pages[v >> PageShift];
                if (page == null)
                {
                    page = new ulong[WordsPerPage];
                    pages[v >> PageShift] = page;
                }
                int word = (v >> 6) & (WordsPerPage - 1);
                ulong bit = 1UL << (v & 63);
                if ((page[word] & bit) != 0) continue;
                if (count >= maxCount)
                {
                    dropped++;
                    continue;
                }
                page[word] |= bit;
                count++;
            }
            return count == 0 ? Empty : new SlotSet(pages, count);
        }

        public bool Contains(uint slot)
        {
            int page = (int)(slot >> PageShift);
            ulong[][] pages = _pages;
            if (page >= pages.Length) return false;
            ulong[] words = pages[page];
            if (words == null) return false;
            return (words[(slot >> 6) & (WordsPerPage - 1)] & (1UL << (int)(slot & 63))) != 0;
        }

        /// <summary>The smallest member at or after <paramref name="fromInclusive"/>, or -1.</summary>
        public int NextMember(int fromInclusive)
        {
            if (fromInclusive < 0) fromInclusive = 0;
            ulong[][] pages = _pages;
            int page = fromInclusive >> PageShift;
            if (page >= pages.Length) return -1;
            int word = (fromInclusive >> 6) & (WordsPerPage - 1);
            ulong[] words = pages[page];
            if (words != null)
            {
                ulong bits = words[word] & (~0UL << (fromInclusive & 63));
                while (true)
                {
                    if (bits != 0) return (page << PageShift) | (word << 6) | BitUtil.TrailingZeros(bits);
                    if (++word == WordsPerPage) break;
                    bits = words[word];
                }
            }
            for (page++; page < pages.Length; page++)
            {
                words = pages[page];
                if (words == null) continue;
                for (word = 0; word < WordsPerPage; word++)
                {
                    if (words[word] != 0) return (page << PageShift) | (word << 6) | BitUtil.TrailingZeros(words[word]);
                }
            }
            return -1;
        }
    }
}
