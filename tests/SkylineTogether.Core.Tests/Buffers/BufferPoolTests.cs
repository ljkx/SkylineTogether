using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Buffers
{
    /// <summary>BufferPool sizes, reuse, misuse detection and thread safety.</summary>
    public sealed class BufferPoolTests
    {
        [Test]
        public void RentReturnsPowerOfTwoArraysOfAtLeastTheRequestedSize()
        {
            foreach (int request in new[] { 1, 255, 256, 257, 1000, 4096, 65537, 1 << 20, (1 << 20) + 1, BufferPool.MaxPooledSize })
            {
                byte[] array = BufferPool.Rent(request);
                Assert.True(array.Length >= request, "rented array too short for " + request);
                Assert.True(array.Length >= BufferPool.MinPooledSize);
                Assert.Equal(0, array.Length & (array.Length - 1), "pooled sizes are powers of two");
                BufferPool.Return(array);
            }
            Assert.Equal(0, BufferPool.Rent(0).Length);
            Assert.Throws<ArgumentOutOfRangeException>(() => BufferPool.Rent(-1));
        }

        [Test]
        public void ReturnedArraysAreReused()
        {
            // a size class no other test uses concurrently
            byte[] first = BufferPool.Rent(3000);
            BufferPool.Return(first);
            byte[] second = BufferPool.Rent(2500);
            Assert.Same(first, second);
            BufferPool.Return(second);
        }

        [Test]
        public void DoubleReturnThrows()
        {
            byte[] array = BufferPool.Rent(600);
            BufferPool.Return(array);
            Assert.Throws<InvalidOperationException>(() => BufferPool.Return(array));
            byte[] again = BufferPool.Rent(600);   // leave the pool consistent: take it out once more
            Assert.Same(array, again);
            BufferPool.Return(again);
        }

        [Test]
        public void ForeignAndOversizedArraysAreIgnored()
        {
            BufferPool.Return(new byte[1000]);                       // not a power of two
            BufferPool.Return(new byte[128]);                        // below the smallest class
            BufferPool.Return(Array.Empty<byte>());
            byte[] big = BufferPool.Rent(BufferPool.MaxPooledSize + 1);
            Assert.Equal(BufferPool.MaxPooledSize + 1, big.Length, "oversized requests are exact and unpooled");
            BufferPool.Return(big);
            Assert.Throws<ArgumentNullException>(() => BufferPool.Return(null));
        }

        [Test]
        public void ConcurrentRentersNeverShareAnArray()
        {
            var owners = new ConcurrentDictionary<byte[], int>(new ReferenceComparer());
            int failures = 0;
            Parallel.For(0, 8, worker =>
            {
                var random = new Random(worker);
                for (int i = 0; i < 20000; i++)
                {
                    byte[] array = BufferPool.Rent(256 << random.Next(6));
                    if (!owners.TryAdd(array, worker)) Interlocked.Increment(ref failures);
                    array[0] = (byte)worker;
                    if (array[0] != (byte)worker) Interlocked.Increment(ref failures);
                    owners.TryRemove(array, out _);
                    BufferPool.Return(array);
                }
            });
            Assert.Equal(0, failures, "an array was handed to two renters at once");
            Assert.True(BufferPool.RetainedBytes >= 0);
        }

        [Test]
        public void ClearReleasesRetainedArrays()
        {
            byte[] array = BufferPool.Rent(1 << 22);
            BufferPool.Return(array);
            Assert.True(BufferPool.RetainedBytes >= 1 << 22);
            BufferPool.Clear();
            byte[] fresh = BufferPool.Rent(1 << 22);
            Assert.NotSame(array, fresh);
            BufferPool.Return(fresh);
        }

        private sealed class ReferenceComparer : System.Collections.Generic.IEqualityComparer<byte[]>
        {
            public bool Equals(byte[] x, byte[] y) => ReferenceEquals(x, y);

            public int GetHashCode(byte[] obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
