using System;
using System.Threading;

namespace SkylineTogether.Core.Buffers
{
    /// <summary>
    /// Thread-safe pool of byte arrays (ARCHITECTURE.md#code-rules: no allocation per message on hot paths; the game
    /// runs an incremental GC, R06 §1). Arrays come in power-of-two sizes from 256 bytes to 16 MiB; larger requests are
    /// allocated and not pooled. A rented array may contain old data and is at least as long as requested. Returning
    /// the same array twice throws, because two renters sharing one array would corrupt each other's messages.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class BufferPool
    {
        /// <summary>The smallest pooled size.</summary>
        public const int MinPooledSize = 256;

        /// <summary>The largest pooled size; larger arrays are allocated per request and dropped on return.</summary>
        public const int MaxPooledSize = 16 * 1024 * 1024;

        private const int MinShift = 8;            // 2^8 = 256
        private const int BucketCount = 17;        // 256 B ... 16 MiB

        private static readonly Bucket[] Buckets = CreateBuckets();
        private static long _retainedBytes;

        private sealed class Bucket
        {
            public readonly int Size;
            public readonly byte[][] Items;
            public int Count;

            public Bucket(int size, int capacity)
            {
                Size = size;
                Items = new byte[capacity][];
            }
        }

        /// <summary>Bytes currently held by the pool (diagnostics).</summary>
        public static long RetainedBytes => Interlocked.Read(ref _retainedBytes);

        /// <summary>An array of at least <paramref name="minimumLength"/> bytes (an empty array for 0).</summary>
        public static byte[] Rent(int minimumLength)
        {
            if (minimumLength < 0) throw new ArgumentOutOfRangeException(nameof(minimumLength));
            if (minimumLength == 0) return Array.Empty<byte>();
            if (minimumLength > MaxPooledSize) return new byte[minimumLength];
            Bucket bucket = Buckets[IndexFor(minimumLength)];
            lock (bucket)
            {
                if (bucket.Count > 0)
                {
                    int top = --bucket.Count;
                    byte[] array = bucket.Items[top];
                    bucket.Items[top] = null;
                    Interlocked.Add(ref _retainedBytes, -array.Length);
                    return array;
                }
            }
            return new byte[bucket.Size];
        }

        /// <summary>
        /// Gives an array back. Arrays that did not come from the pool (sizes that are not a pooled power of two) and
        /// empty arrays are ignored; returning an array that is already in the pool throws.
        /// </summary>
        public static void Return(byte[] buffer)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            int length = buffer.Length;
            if (length < MinPooledSize || length > MaxPooledSize || (length & (length - 1)) != 0) return;
            Bucket bucket = Buckets[IndexFor(length)];
            lock (bucket)
            {
                for (int i = 0; i < bucket.Count; i++)
                {
                    if (ReferenceEquals(bucket.Items[i], buffer))
                    {
                        throw new InvalidOperationException("BufferPool: the array was returned twice");
                    }
                }
                if (bucket.Count == bucket.Items.Length) return;   // bucket full: let the GC take it
                bucket.Items[bucket.Count++] = buffer;
                Interlocked.Add(ref _retainedBytes, length);
            }
        }

        /// <summary>Releases every pooled array (for example when a session ends).</summary>
        public static void Clear()
        {
            foreach (Bucket bucket in Buckets)
            {
                lock (bucket)
                {
                    for (int i = 0; i < bucket.Count; i++)
                    {
                        Interlocked.Add(ref _retainedBytes, -bucket.Items[i].Length);
                        bucket.Items[i] = null;
                    }
                    bucket.Count = 0;
                }
            }
        }

        private static int IndexFor(int length)
        {
            int size = MinPooledSize;
            int index = 0;
            while (size < length)
            {
                size <<= 1;
                index++;
            }
            return index;
        }

        private static Bucket[] CreateBuckets()
        {
            var buckets = new Bucket[BucketCount];
            for (int i = 0; i < BucketCount; i++)
            {
                int size = 1 << (MinShift + i);
                // retention bounds the pool's memory: many small arrays, few large ones
                int capacity = size <= 64 * 1024 ? 32 : size <= 1024 * 1024 ? 8 : 2;
                buckets[i] = new Bucket(size, capacity);
            }
            return buckets;
        }
    }
}
