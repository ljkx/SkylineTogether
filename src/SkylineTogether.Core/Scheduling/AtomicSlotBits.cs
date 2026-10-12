using System.Threading;

namespace SkylineTogether.Core.Scheduling
{
    /// <summary>
    /// A mutable paged bitset over slots that readers on other threads may query and iterate while a writer changes it:
    /// bits are flipped with compare-and-swap on 64-bit words, pages are allocated on first use and published with a
    /// volatile write, and the page directory grows by copy-on-write. A reader therefore sees, per bit, either the old
    /// or the new state, never a torn word. Used for the per-peer motion-interest membership, which game-motion changes
    /// mover by mover (ARCH §4.9.3) while the scheduler reads it on a worker.
    /// </summary>
    internal sealed class AtomicSlotBits
    {
        private const int PageShift = SlotSet.PageShift;
        private const int WordsPerPage = SlotSet.WordsPerPage;

        private readonly object _growLock = new object();
        private long[][] _pages = new long[0][];
        private int _count;

        /// <summary>Members (exact once concurrent writers have finished).</summary>
        public int Count => Volatile.Read(ref _count);

        /// <summary>Sets or clears a slot's bit; returns true when the bit changed.</summary>
        public bool Set(uint slot, bool value)
        {
            if (slot == 0 || slot > NetSlot.MaxValue) return false;
            long[] page = GetPage((int)(slot >> PageShift), value);
            if (page == null) return false;                  // clearing a bit on an absent page
            int word = (int)((slot >> 6) & (WordsPerPage - 1));
            long bit = 1L << (int)(slot & 63);
            while (true)
            {
                long current = Volatile.Read(ref page[word]);
                long next = value ? current | bit : current & ~bit;
                if (next == current) return false;
                if (Interlocked.CompareExchange(ref page[word], next, current) == current)
                {
                    if (value) Interlocked.Increment(ref _count);
                    else Interlocked.Decrement(ref _count);
                    return true;
                }
            }
        }

        public bool Contains(uint slot)
        {
            long[][] pages = Volatile.Read(ref _pages);
            int index = (int)(slot >> PageShift);
            if (index >= pages.Length) return false;
            long[] page = Volatile.Read(ref pages[index]);
            if (page == null) return false;
            return (Volatile.Read(ref page[(slot >> 6) & (WordsPerPage - 1)]) & (1L << (int)(slot & 63))) != 0;
        }

        /// <summary>The smallest member at or after <paramref name="fromInclusive"/>, or -1.</summary>
        public int NextMember(int fromInclusive)
        {
            if (fromInclusive < 0) fromInclusive = 0;
            long[][] pages = Volatile.Read(ref _pages);
            int index = fromInclusive >> PageShift;
            if (index >= pages.Length) return -1;
            int word = (fromInclusive >> 6) & (WordsPerPage - 1);
            long[] page = Volatile.Read(ref pages[index]);
            if (page != null)
            {
                ulong bits = (ulong)Volatile.Read(ref page[word]) & (~0UL << (fromInclusive & 63));
                while (true)
                {
                    if (bits != 0) return (index << PageShift) | (word << 6) | BitUtil.TrailingZeros(bits);
                    if (++word == WordsPerPage) break;
                    bits = (ulong)Volatile.Read(ref page[word]);
                }
            }
            for (index++; index < pages.Length; index++)
            {
                page = Volatile.Read(ref pages[index]);
                if (page == null) continue;
                for (word = 0; word < WordsPerPage; word++)
                {
                    ulong bits = (ulong)Volatile.Read(ref page[word]);
                    if (bits != 0) return (index << PageShift) | (word << 6) | BitUtil.TrailingZeros(bits);
                }
            }
            return -1;
        }

        /// <summary>Removes every member (pages stay allocated for reuse).</summary>
        public void Clear()
        {
            long[][] pages = Volatile.Read(ref _pages);
            for (int i = 0; i < pages.Length; i++)
            {
                long[] page = Volatile.Read(ref pages[i]);
                if (page == null) continue;
                for (int w = 0; w < WordsPerPage; w++)
                {
                    long old = Interlocked.Exchange(ref page[w], 0);
                    if (old != 0) Interlocked.Add(ref _count, -BitUtil.PopCount((ulong)old));
                }
            }
        }

        private long[] GetPage(int index, bool create)
        {
            long[][] pages = Volatile.Read(ref _pages);
            if (index < pages.Length)
            {
                long[] existing = Volatile.Read(ref pages[index]);
                if (existing != null || !create) return existing;
            }
            else if (!create)
            {
                return null;
            }
            lock (_growLock)
            {
                pages = _pages;
                if (index >= pages.Length)
                {
                    int length = pages.Length == 0 ? 16 : pages.Length;
                    while (length <= index) length *= 2;
                    var grown = new long[length][];
                    for (int i = 0; i < pages.Length; i++) grown[i] = Volatile.Read(ref pages[i]);
                    Volatile.Write(ref _pages, grown);
                    pages = grown;
                }
                long[] page = Volatile.Read(ref pages[index]);
                if (page == null)
                {
                    page = new long[WordsPerPage];
                    Volatile.Write(ref pages[index], page);
                }
                return page;
            }
        }
    }
}
