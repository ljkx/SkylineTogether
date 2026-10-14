using System;

namespace SkylineTogether.Core.Hashing
{
    /// <summary>
    /// Incremental XXH3-128 (the <see cref="Digest128"/> algorithm): appending data in any split gives the same digest
    /// as <see cref="Digest128.Compute(byte[], int, int)"/> over the concatenation, with constant memory, so blobs of
    /// hundreds of megabytes are hashed while they stream (ARCHITECTURE.md#blob-transfer). <see cref="Finish"/> does not change the state.
    /// Not thread-safe.
    /// </summary>
    public sealed class XxHash128Stream
    {
        private readonly ulong[] _acc = new ulong[8];
        private readonly byte[] _buffer = new byte[Xxh3.InternalBufferSize];
        private int _bufferedSize;
        private int _stripesSoFar;
        private ulong _totalLength;

        public XxHash128Stream()
        {
            Reset();
        }

        /// <summary>Total number of bytes appended since the last reset.</summary>
        public ulong Length => _totalLength;

        public unsafe void Reset()
        {
            fixed (ulong* acc = _acc)
            {
                Xxh3.InitAccumulators(acc);
            }
            _bufferedSize = 0;
            _stripesSoFar = 0;
            _totalLength = 0;
        }

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
            fixed (byte* secret = Xxh3.DefaultSecret)
            fixed (ulong* acc = _acc)
            {
                byte* input = src;
                byte* end = src + count;
                if (count <= Xxh3.InternalBufferSize - _bufferedSize)
                {
                    System.Buffer.MemoryCopy(input, buf + _bufferedSize, Xxh3.InternalBufferSize - _bufferedSize, count);
                    _bufferedSize += count;
                    return;
                }
                // the internal buffer is partially filled (except at the start): complete it, then consume it
                if (_bufferedSize > 0)
                {
                    int load = Xxh3.InternalBufferSize - _bufferedSize;
                    System.Buffer.MemoryCopy(input, buf + _bufferedSize, load, load);
                    input += load;
                    Xxh3.ConsumeStripes(acc, ref _stripesSoFar, buf, Xxh3.InternalBufferStripes, secret);
                    _bufferedSize = 0;
                }
                // consume whole stripes straight from the input, always keeping at least one byte for the final stripe
                if (end - input > Xxh3.InternalBufferSize)
                {
                    int stripes = (int)((end - 1 - input) / Xxh3.StripeLength);
                    input = Xxh3.ConsumeStripes(acc, ref _stripesSoFar, input, stripes, secret);
                    // keep the last consumed stripe: the final stripe may need its tail
                    System.Buffer.MemoryCopy(input - Xxh3.StripeLength, buf + Xxh3.InternalBufferSize - Xxh3.StripeLength,
                                             Xxh3.StripeLength, Xxh3.StripeLength);
                }
                int rest = (int)(end - input);
                System.Buffer.MemoryCopy(input, buf, Xxh3.InternalBufferSize, rest);
                _bufferedSize = rest;
            }
        }

        /// <summary>The digest of everything appended so far.</summary>
        public unsafe Digest128 Finish()
        {
            fixed (byte* buf = _buffer)
            {
                if (_totalLength <= Xxh3.MidSizeMax)
                {
                    // short inputs never left the buffer
                    return Xxh3.Hash128(buf, (int)_totalLength);
                }
                fixed (byte* secret = Xxh3.DefaultSecret)
                {
                    ulong* acc = stackalloc ulong[8];
                    for (int i = 0; i < 8; i++) acc[i] = _acc[i];
                    byte* lastStripe;
                    if (_bufferedSize >= Xxh3.StripeLength)
                    {
                        int stripes = (_bufferedSize - 1) / Xxh3.StripeLength;
                        int stripesSoFar = _stripesSoFar;
                        Xxh3.ConsumeStripes(acc, ref stripesSoFar, buf, stripes, secret);
                        lastStripe = buf + _bufferedSize - Xxh3.StripeLength;
                    }
                    else
                    {
                        // the final stripe spans the tail of the previously consumed data and the buffered bytes
                        byte* stripe = stackalloc byte[Xxh3.StripeLength];
                        int catchup = Xxh3.StripeLength - _bufferedSize;
                        System.Buffer.MemoryCopy(buf + Xxh3.InternalBufferSize - catchup, stripe, Xxh3.StripeLength, catchup);
                        System.Buffer.MemoryCopy(buf, stripe + catchup, Xxh3.StripeLength - catchup, _bufferedSize);
                        lastStripe = stripe;
                    }
                    Xxh3.AccumulateLastStripe(acc, lastStripe, secret);
                    return Xxh3.MergeLong(acc, secret, _totalLength);
                }
            }
        }
    }
}
