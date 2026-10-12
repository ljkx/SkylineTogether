using System;

namespace SkylineTogether.Core.Transport
{
    /// <summary>
    /// A fixed-length bit set with a population count, used for blob chunk states. Bitmaps exchanged on the wire use
    /// the layout of <see cref="ToBitmap"/>: bit <c>i</c> is bit <c>i &amp; 7</c> (least significant first) of byte
    /// <c>i &gt;&gt; 3</c>. Not thread-safe.
    /// </summary>
    internal sealed class ChunkBitSet
    {
        private readonly ulong[] _words;
        private int _count;

        public ChunkBitSet(int length)
        {
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            Length = length;
            _words = new ulong[(length + 63) >> 6];
        }

        /// <summary>Number of bits.</summary>
        public int Length { get; }

        /// <summary>Number of set bits.</summary>
        public int Count => _count;

        public bool IsFull => _count == Length;

        public bool Get(int index)
        {
            CheckIndex(index);
            return (_words[index >> 6] & (1UL << (index & 63))) != 0;
        }

        /// <summary>Sets the bit; true when it was clear.</summary>
        public bool Set(int index)
        {
            CheckIndex(index);
            ulong mask = 1UL << (index & 63);
            ref ulong word = ref _words[index >> 6];
            if ((word & mask) != 0) return false;
            word |= mask;
            _count++;
            return true;
        }

        /// <summary>Clears the bit; true when it was set.</summary>
        public bool Clear(int index)
        {
            CheckIndex(index);
            ulong mask = 1UL << (index & 63);
            ref ulong word = ref _words[index >> 6];
            if ((word & mask) == 0) return false;
            word &= ~mask;
            _count--;
            return true;
        }

        public void SetAll()
        {
            for (int i = 0; i < _words.Length; i++) _words[i] = ulong.MaxValue;
            int tail = Length & 63;
            if (tail != 0) _words[_words.Length - 1] = (1UL << tail) - 1;
            _count = Length;
        }

        public void ClearAll()
        {
            Array.Clear(_words, 0, _words.Length);
            _count = 0;
        }

        /// <summary>The first set bit at or after <paramref name="from"/>, or −1.</summary>
        public int NextSet(int from)
        {
            if (from < 0) from = 0;
            if (from >= Length) return -1;
            int w = from >> 6;
            ulong word = _words[w] & (ulong.MaxValue << (from & 63));
            while (true)
            {
                if (word != 0)
                {
                    int index = (w << 6) + TrailingZeros(word);
                    return index < Length ? index : -1;
                }
                if (++w >= _words.Length) return -1;
                word = _words[w];
            }
        }

        /// <summary>The first clear bit at or after <paramref name="from"/>, or −1.</summary>
        public int NextClear(int from)
        {
            if (from < 0) from = 0;
            if (from >= Length) return -1;
            int w = from >> 6;
            ulong word = ~_words[w] & (ulong.MaxValue << (from & 63));
            while (true)
            {
                if (word != 0)
                {
                    int index = (w << 6) + TrailingZeros(word);
                    return index < Length ? index : -1;
                }
                if (++w >= _words.Length) return -1;
                word = ~_words[w];
            }
        }

        /// <summary>Bytes of the wire bitmap of <paramref name="bits"/> bits.</summary>
        public static int BitmapBytes(int bits) => (bits + 7) >> 3;

        /// <summary>The wire bitmap (see the class summary).</summary>
        public byte[] ToBitmap()
        {
            var bitmap = new byte[BitmapBytes(Length)];
            for (int i = 0; i < bitmap.Length; i++)
            {
                bitmap[i] = (byte)(_words[i >> 3] >> ((i & 7) << 3));
            }
            return bitmap;
        }

        /// <summary>True when bit <paramref name="index"/> is set in a wire bitmap.</summary>
        public static bool BitmapGet(byte[] bitmap, int offset, int index) => (bitmap[offset + (index >> 3)] & (1 << (index & 7))) != 0;

        private void CheckIndex(int index)
        {
            if ((uint)index >= (uint)Length) throw new ArgumentOutOfRangeException(nameof(index));
        }

        private static int TrailingZeros(ulong value)
        {
            // de Bruijn multiplication (no intrinsic in netstandard2.0)
            return DeBruijn[(int)(((value & (0 - value)) * 0x03F79D71B4CB0A89UL) >> 58)];
        }

        private static readonly int[] DeBruijn =
        {
            0, 1, 48, 2, 57, 49, 28, 3, 61, 58, 50, 42, 38, 29, 17, 4,
            62, 55, 59, 36, 53, 51, 43, 22, 45, 39, 33, 30, 24, 18, 12, 5,
            63, 47, 56, 27, 60, 41, 37, 16, 54, 35, 52, 21, 44, 32, 23, 11,
            46, 26, 40, 15, 34, 20, 31, 10, 25, 14, 19, 9, 13, 8, 7, 6
        };
    }
}
