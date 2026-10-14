using System;
using System.Globalization;
using System.Text;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Hashing;
using SkylineTogether.Core.Protocol;

namespace SkylineTogether.Core.Transport
{
    /// <summary>
    /// Wire format of the blob transfer messages (ARCHITECTURE.md#blob-transfer, #envelope-and-message-kinds), all little-endian. Every body starts with the
    /// <c>u32 blobId</c>, so a receiver can route it with <see cref="TryPeekBlobId"/> before decoding.
    /// <code>
    /// BlobManifest (lane R) = u32 blobId | varint64 size | varint chunkSize | u64 digestLo | u64 digestHi | varint purposeLength | purpose (UTF-8)
    /// BlobChunk    (lane B) = u32 blobId | u32 index | u32 length | u64 xxHash64(payload) | payload (length bytes)
    /// BlobHave     (lane R) = u32 blobId | varint bitmapLength | bitmap   (bit i = byte i/8, bit i%8 LSB first: chunk i is held)
    /// </code>
    /// Readers throw <see cref="DecodeException"/> for anything outside the limits (blob ≤ 2 GiB, ARCHITECTURE.md#security-model; chunks of
    /// <see cref="MinChunkBytes"/> to <see cref="Limits.MaxBulkChunkBytes"/> bytes; purpose ≤ <see cref="MaxPurposeBytes"/>
    /// bytes); writers throw <see cref="ArgumentException"/> for values a reader would reject.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class BlobCodec
    {
        /// <summary>Bytes of the chunk header (blob id, index, length, hash).</summary>
        public const int ChunkHeaderBytes = 20;

        /// <summary>Largest blob a manifest may announce (ARCHITECTURE.md#blob-transfer: at most 2 GiB).</summary>
        public const long MaxBlobBytes = 2L << 30;

        /// <summary>Smallest chunk size; bounds the chunk count (and the have-bitmap) of the largest blob.</summary>
        public const int MinChunkBytes = 512;

        /// <summary>The chunk size of snapshots and other blobs (ARCHITECTURE.md#blob-transfer).</summary>
        public const int DefaultChunkBytes = Limits.MaxBulkChunkBytes;

        /// <summary>UTF-8 bytes of the purpose label.</summary>
        public const int MaxPurposeBytes = 64;

        /// <summary>Largest have-bitmap: the largest blob in the smallest chunks.</summary>
        public const int MaxHaveBitmapBytes = (int)((MaxBlobBytes / MinChunkBytes + 7) / 8);

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

        /// <summary>Number of chunks of a blob (<paramref name="chunkSize"/> must be positive).</summary>
        public static int ChunkCount(long size, int chunkSize)
        {
            if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
            if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
            long count = (size + chunkSize - 1) / chunkSize;
            if (count > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(size), "too many chunks");
            return (int)count;
        }

        /// <summary>Payload bytes of chunk <paramref name="index"/> (the last chunk holds the remainder).</summary>
        public static int ChunkLength(long size, int chunkSize, int index)
        {
            long start = (long)index * chunkSize;
            if (index < 0 || start >= size) throw new ArgumentOutOfRangeException(nameof(index));
            return (int)Math.Min(chunkSize, size - start);
        }

        /// <summary>Bytes of the have-bitmap of a blob of <paramref name="chunkCount"/> chunks.</summary>
        public static int HaveBitmapBytes(int chunkCount)
        {
            if (chunkCount < 0) throw new ArgumentOutOfRangeException(nameof(chunkCount));
            return (int)(((long)chunkCount + 7) / 8);
        }

        /// <summary>Throws <see cref="ArgumentException"/> when a manifest is outside the limits.</summary>
        public static void Validate(BlobManifestInfo m)
        {
            if (m == null) throw new ArgumentNullException(nameof(m));
            if (m.Size < 0 || m.Size > MaxBlobBytes)
            {
                throw new ArgumentException("blob size " + m.Size.ToString(CultureInfo.InvariantCulture) + " outside 0 ... " + MaxBlobBytes.ToString(CultureInfo.InvariantCulture), nameof(m));
            }
            if (m.ChunkSize < MinChunkBytes || m.ChunkSize > Limits.MaxBulkChunkBytes)
            {
                throw new ArgumentException("chunk size " + m.ChunkSize.ToString(CultureInfo.InvariantCulture) + " outside "
                                            + MinChunkBytes.ToString(CultureInfo.InvariantCulture) + " ... " + Limits.MaxBulkChunkBytes.ToString(CultureInfo.InvariantCulture), nameof(m));
            }
            if (m.Purpose != null && Utf8.GetByteCount(m.Purpose) > MaxPurposeBytes)
            {
                throw new ArgumentException("the purpose label exceeds " + MaxPurposeBytes.ToString(CultureInfo.InvariantCulture) + " UTF-8 bytes", nameof(m));
            }
        }

        public static void WriteManifest(ByteWriter w, BlobManifestInfo m)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            Validate(m);
            w.WriteUInt32(m.BlobId);
            w.WriteVarUInt64((ulong)m.Size);
            w.WriteVarUInt32((uint)m.ChunkSize);
            w.WriteUInt64(m.Digest.Lo);
            w.WriteUInt64(m.Digest.Hi);
            w.WriteString(m.Purpose, MaxPurposeBytes);
        }

        public static BlobManifestInfo ReadManifest(ByteReader r)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            uint blobId = r.ReadUInt32();
            ulong size = r.ReadVarUInt64();
            if (size > (ulong)MaxBlobBytes)
            {
                throw new DecodeException("BlobManifest: size " + size.ToString(CultureInfo.InvariantCulture) + " exceeds the limit of " + MaxBlobBytes.ToString(CultureInfo.InvariantCulture));
            }
            uint chunkSize = r.ReadVarUInt32();
            if (chunkSize < MinChunkBytes || chunkSize > Limits.MaxBulkChunkBytes)
            {
                throw new DecodeException("BlobManifest: chunk size " + chunkSize.ToString(CultureInfo.InvariantCulture) + " outside "
                                          + MinChunkBytes.ToString(CultureInfo.InvariantCulture) + " ... " + Limits.MaxBulkChunkBytes.ToString(CultureInfo.InvariantCulture));
            }
            ulong lo = r.ReadUInt64();
            ulong hi = r.ReadUInt64();
            string purpose;
            try
            {
                purpose = r.ReadString(MaxPurposeBytes);
            }
            catch (DecodeException e)
            {
                throw new DecodeException("BlobManifest.Purpose: " + e.Message, e);
            }
            return new BlobManifestInfo
            {
                BlobId = blobId,
                Size = (long)size,
                ChunkSize = (int)chunkSize,
                Digest = new Digest128(lo, hi),
                Purpose = purpose
            };
        }

        /// <summary>A whole <see cref="MessageKind.BlobManifest"/> body: <see cref="ReadManifest"/> plus a check that nothing follows.</summary>
        public static BlobManifestInfo DecodeManifest(byte[] buffer, int offset, int count)
        {
            var r = new ByteReader(buffer, offset, count);
            BlobManifestInfo m = ReadManifest(r);
            EnsureEnd(r, "BlobManifest");
            return m;
        }

        /// <summary>The 20-byte chunk header; the payload of <paramref name="length"/> bytes follows it.</summary>
        public static void WriteChunkHeader(ByteWriter w, uint blobId, int index, int length, ulong hash)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
            if (length < 0 || length > Limits.MaxBulkChunkBytes) throw new ArgumentOutOfRangeException(nameof(length), length, "0 ... " + Limits.MaxBulkChunkBytes.ToString(CultureInfo.InvariantCulture));
            w.WriteUInt32(blobId);
            w.WriteUInt32((uint)index);
            w.WriteUInt32((uint)length);
            w.WriteUInt64(hash);
        }

        /// <summary>The chunk header written in place at <paramref name="offset"/> (the sender reads the payload first, then fills the header).</summary>
        internal static void WriteChunkHeaderAt(byte[] buffer, int offset, uint blobId, int index, int length, ulong hash)
        {
            WriteUInt32At(buffer, offset, blobId);
            WriteUInt32At(buffer, offset + 4, (uint)index);
            WriteUInt32At(buffer, offset + 8, (uint)length);
            WriteUInt32At(buffer, offset + 12, (uint)hash);
            WriteUInt32At(buffer, offset + 16, (uint)(hash >> 32));
        }

        /// <summary>Parses the chunk header at <paramref name="offset"/> without validation (the receiver validates against its manifest).</summary>
        internal static void ReadChunkHeaderAt(byte[] buffer, int offset, out uint blobId, out uint index, out uint length, out ulong hash)
        {
            blobId = ReadUInt32At(buffer, offset);
            index = ReadUInt32At(buffer, offset + 4);
            length = ReadUInt32At(buffer, offset + 8);
            hash = ReadUInt32At(buffer, offset + 12) | ((ulong)ReadUInt32At(buffer, offset + 16) << 32);
        }

        public static void ReadChunkHeader(ByteReader r, out uint blobId, out int index, out int length, out ulong hash)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            blobId = r.ReadUInt32();
            uint i = r.ReadUInt32();
            if (i > int.MaxValue) throw new DecodeException("BlobChunk: index " + i.ToString(CultureInfo.InvariantCulture) + " exceeds int.MaxValue");
            uint n = r.ReadUInt32();
            if (n > Limits.MaxBulkChunkBytes)
            {
                throw new DecodeException("BlobChunk: length " + n.ToString(CultureInfo.InvariantCulture) + " exceeds " + Limits.MaxBulkChunkBytes.ToString(CultureInfo.InvariantCulture));
            }
            hash = r.ReadUInt64();
            index = (int)i;
            length = (int)n;
        }

        /// <summary>
        /// Appends a complete uncompressed <see cref="MessageKind.BlobChunk"/> envelope (as <see cref="EnvelopeCodec"/>
        /// would encode it without compression; snapshots are already compressed) with the payload's xxHash64.
        /// </summary>
        public static void WriteChunkMessage(ByteWriter w, uint blobId, int index, byte[] payload, int offset, int count)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (offset < 0 || count < 0 || offset > payload.Length - count) throw new ArgumentOutOfRangeException(nameof(count), "the range lies outside the payload");
            ulong hash = XxHash64.Hash(payload, offset, count);
            w.WriteByte((byte)MessageKind.BlobChunk);
            w.WriteByte((byte)EnvelopeFlags.None);
            WriteChunkHeader(w, blobId, index, count, hash);
            w.WriteBytes(payload, offset, count);
        }

        public static void WriteHave(ByteWriter w, uint blobId, byte[] bitmap, int offset, int count)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            if (bitmap == null) throw new ArgumentNullException(nameof(bitmap));
            if (offset < 0 || count < 0 || offset > bitmap.Length - count) throw new ArgumentOutOfRangeException(nameof(count), "the range lies outside the bitmap");
            if (count > MaxHaveBitmapBytes) throw new ArgumentOutOfRangeException(nameof(count), count, "a have-bitmap holds at most " + MaxHaveBitmapBytes.ToString(CultureInfo.InvariantCulture) + " bytes");
            w.WriteUInt32(blobId);
            w.WriteVarUInt32((uint)count);
            w.WriteBytes(bitmap, offset, count);
        }

        /// <summary>Reads a have message; the bitmap is returned as a segment of the reader's buffer (no copy).</summary>
        public static ArraySegment<byte> ReadHave(ByteReader r, out uint blobId)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            blobId = r.ReadUInt32();
            uint count = r.ReadVarUInt32();
            if (count > MaxHaveBitmapBytes)
            {
                throw new DecodeException("BlobHave: a bitmap of " + count.ToString(CultureInfo.InvariantCulture) + " bytes exceeds the limit of " + MaxHaveBitmapBytes.ToString(CultureInfo.InvariantCulture));
            }
            if (count > (uint)r.Remaining) throw new DecodeException("BlobHave: a bitmap of " + count.ToString(CultureInfo.InvariantCulture) + " bytes overruns the message");
            return r.ReadSegment((int)count);
        }

        /// <summary>A whole <see cref="MessageKind.BlobHave"/> body: <see cref="ReadHave"/> plus a check that nothing follows.</summary>
        public static ArraySegment<byte> DecodeHave(byte[] buffer, int offset, int count, out uint blobId)
        {
            var r = new ByteReader(buffer, offset, count);
            ArraySegment<byte> bitmap = ReadHave(r, out blobId);
            EnsureEnd(r, "BlobHave");
            return bitmap;
        }

        /// <summary>The blob id that starts every blob message body; false when the body is shorter than 4 bytes.</summary>
        public static bool TryPeekBlobId(byte[] buffer, int offset, int count, out uint blobId)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count), "the range lies outside the buffer");
            if (count < 4)
            {
                blobId = 0;
                return false;
            }
            blobId = buffer[offset] | ((uint)buffer[offset + 1] << 8) | ((uint)buffer[offset + 2] << 16) | ((uint)buffer[offset + 3] << 24);
            return true;
        }

        private static void EnsureEnd(ByteReader r, string message)
        {
            if (!r.IsAtEnd) throw new DecodeException(message + ": " + r.Remaining.ToString(CultureInfo.InvariantCulture) + " trailing byte(s)");
        }

        private static void WriteUInt32At(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private static uint ReadUInt32At(byte[] buffer, int offset)
        {
            return buffer[offset] | ((uint)buffer[offset + 1] << 8) | ((uint)buffer[offset + 2] << 16) | ((uint)buffer[offset + 3] << 24);
        }
    }
}
