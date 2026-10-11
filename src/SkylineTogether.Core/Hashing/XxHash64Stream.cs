using System;

namespace SkylineTogether.Core.Hashing
{
    /// <summary>
    /// Incremental xxHash64: appending data in any split gives the same result as <see cref="XxHash64.Hash(byte[], int, int, ulong)"/>
    /// over the concatenation. <see cref="Finish"/> does not change the state, so appending may continue afterwards.
    /// Not thread-safe.
    /// </summary>
    public sealed class XxHash64Stream
    {
        private readonly ulong _seed;
        private readonly byte[] _buffer = new byte[32];
        private ulong _v1, _v2, _v3, _v4;
        private int _bufferLength;
        private ulong _totalLength;

        public XxHash64Stream(ulong seed = 0)
        {
            _seed = seed;
            Reset();
        }

        /// <summary>Total number of bytes appended since the last reset.</summary>
        public ulong Length => _totalLength;

        public unsafe void Append(byte[] data, int offset, int count)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (offset < 0 || count < 0 || offset > data.Length - count)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "the range lies outside the array");
            }
            if (count == 0) return;
            _totalLength += (ulong)count;
            fixed (byte* src = &data[offset])
            fixed (byte* buf = _buffer)
            {
                byte* p = src;
                byte* end = src + count;
                if (_bufferLength > 0)
                {
                    int take = Math.Min(32 - _bufferLength, count);
                    System.Buffer.MemoryCopy(p, buf + _bufferLength, 32 - _bufferLength, take);
                    _bufferLength += take;
                    p += take;
                    if (_bufferLength < 32) return;
                    ConsumeStripe(buf);
                    _bufferLength = 0;
                }
                while (end - p >= 32)
                {
                    ConsumeStripe(p);
                    p += 32;
                }
                int rest = (int)(end - p);
                if (rest > 0)
                {
                    System.Buffer.MemoryCopy(p, buf, 32, rest);
                    _bufferLength = rest;
                }
            }
        }

        /// <summary>The hash of everything appended so far.</summary>
        public unsafe ulong Finish()
        {
            ulong h = _totalLength >= 32
                ? XxHash64.MergeAccumulators(_v1, _v2, _v3, _v4)
                : unchecked(_seed + XxHash64.Prime5);
            h = unchecked(h + _totalLength);
            fixed (byte* buf = _buffer)
            {
                return XxHash64.Finalize(h, buf, _bufferLength);
            }
        }

        /// <summary>Starts over with the same seed.</summary>
        public void Reset()
        {
            unchecked
            {
                _v1 = _seed + XxHash64.Prime1 + XxHash64.Prime2;
                _v2 = _seed + XxHash64.Prime2;
                _v3 = _seed;
                _v4 = _seed - XxHash64.Prime1;
            }
            _bufferLength = 0;
            _totalLength = 0;
        }

        private unsafe void ConsumeStripe(byte* p)
        {
            _v1 = XxHash64.Round(_v1, XxHash64.ReadLE64(p));
            _v2 = XxHash64.Round(_v2, XxHash64.ReadLE64(p + 8));
            _v3 = XxHash64.Round(_v3, XxHash64.ReadLE64(p + 16));
            _v4 = XxHash64.Round(_v4, XxHash64.ReadLE64(p + 24));
        }
    }
}
