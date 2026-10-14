using System;
using System.Globalization;
using System.IO;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Hashing
{
    /// <summary>
    /// A 128-bit content digest: XXH3-128 with seed 0 (ARCHITECTURE.md#blob-transfer, #snapshot-join), used for snapshot blobs and
    /// warm-join blocks. <see cref="Lo"/> and <see cref="Hi"/> are the low and high 64 bits of the XXH128 value.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public struct Digest128 : IEquatable<Digest128>
    {
        public ulong Lo, Hi;

        public Digest128(ulong lo, ulong hi)
        {
            Lo = lo;
            Hi = hi;
        }

        /// <summary>Digest of the stream from its current position to its end, read in 64 KiB chunks (constant memory).</summary>
        public static Digest128 Compute(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            var hasher = new XxHash128Stream();
            byte[] chunk = BufferPool.Rent(64 * 1024);
            try
            {
                int read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    hasher.Append(chunk, 0, read);
                }
            }
            finally
            {
                BufferPool.Return(chunk);
            }
            return hasher.Finish();
        }

        public static unsafe Digest128 Compute(byte[] data, int offset, int count)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (offset < 0 || count < 0 || offset > data.Length - count)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "the range lies outside the array");
            }
            if (count == 0) return Xxh3.Hash128(null, 0);
            fixed (byte* p = &data[offset])
            {
                return Xxh3.Hash128(p, count);
            }
        }

        public bool Equals(Digest128 other) => Lo == other.Lo && Hi == other.Hi;

        public override bool Equals(object obj) => obj is Digest128 other && Equals(other);

        public override int GetHashCode() => unchecked((int)Lo ^ (int)(Lo >> 32) ^ (int)Hi ^ (int)(Hi >> 32));

        /// <summary>32 hex digits, high half first (the canonical XXH128 form).</summary>
        public override string ToString()
        {
            return Hi.ToString("x16", CultureInfo.InvariantCulture) + Lo.ToString("x16", CultureInfo.InvariantCulture);
        }

        public static bool operator ==(Digest128 a, Digest128 b) => a.Equals(b);

        public static bool operator !=(Digest128 a, Digest128 b) => !a.Equals(b);
    }
}
