using System;
using System.Globalization;
using System.Threading;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// Splits an encoded message larger than <see cref="Limits.FragmentThreshold"/> into fragments that fit Steam's
    /// 512 KiB message cap (ARCHITECTURE.md#fragmentation-and-compression; R06 §4.2). The input is a complete envelope from <see cref="EnvelopeCodec.Encode"/>
    /// (compressed or not); fragmentation sits below compression. Each fragment is an envelope of the same kind with
    /// the <see cref="EnvelopeFlags.Fragment"/> flag:
    /// <code>
    /// u8 kind | u8 flags = Fragment | varint messageId | varint index | varint fragmentCount | varint totalLength | chunk
    /// </code>
    /// Fragments of one message are emitted consecutively in index order; <see cref="Reassembler"/> restores the
    /// original envelope, which the receiver decodes again with <see cref="EnvelopeCodec.Decode"/>.
    /// <para>
    /// Thread-safe. Message ids are unique per instance, so use one fragmenter per connection: fragments of messages
    /// sent from different threads may interleave on a lane, and the reassembler keeps them apart by id.
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class Fragmenter
    {
        /// <summary>Largest fragment header: kind, flags, id (5), index (1), count (1), total length (4), rounded up.</summary>
        public const int MaxFragmentHeaderBytes = 16;

        /// <summary>Message bytes per fragment; every fragment stays at or below <see cref="Limits.FragmentThreshold"/>.</summary>
        public const int ChunkBytes = Limits.FragmentThreshold - MaxFragmentHeaderBytes;

        private int _nextMessageId;

        /// <summary>
        /// Emits <c>body[offset .. offset+count-1]</c>, an encoded envelope of <paramref name="kind"/>: unchanged when it
        /// fits <see cref="Limits.FragmentThreshold"/>, otherwise as fragments built in <paramref name="scratch"/> (reset
        /// for each fragment). The emitted array is valid only during the callback. Throws
        /// <see cref="ArgumentException"/> when the bytes are not an unfragmented envelope of that kind or exceed the
        /// kind's limit.
        /// </summary>
        public void Split(MessageKind kind, byte[] body, int offset, int count, ByteWriter scratch, Action<byte[], int, int> emit)
        {
            WireFormat.CheckRange(body, offset, count, nameof(body));
            if (scratch == null) throw new ArgumentNullException(nameof(scratch));
            if (emit == null) throw new ArgumentNullException(nameof(emit));
            if (count < 2 || body[offset] != (byte)kind || (body[offset + 1] & (byte)EnvelopeFlags.Fragment) != 0)
            {
                throw new ArgumentException("the bytes must be an encoded, unfragmented " + kind + " envelope", nameof(body));
            }
            if (count <= Limits.FragmentThreshold)
            {
                emit(body, offset, count);
                return;
            }
            int max = Limits.MaxBodyBytes(kind) + Limits.EnvelopeOverheadBytes;
            if (count > max)
            {
                throw new ArgumentException(kind + " message of " + count.ToString(CultureInfo.InvariantCulture)
                                            + " bytes exceeds the limit of " + max.ToString(CultureInfo.InvariantCulture), nameof(count));
            }
            int fragments = (count + ChunkBytes - 1) / ChunkBytes;
            uint messageId = unchecked((uint)Interlocked.Increment(ref _nextMessageId));
            int position = offset;
            for (int index = 0; index < fragments; index++)
            {
                int chunk = Math.Min(ChunkBytes, offset + count - position);
                scratch.Reset();
                scratch.WriteByte((byte)kind);
                scratch.WriteByte((byte)EnvelopeFlags.Fragment);
                scratch.WriteVarUInt32(messageId);
                scratch.WriteVarUInt32((uint)index);
                scratch.WriteVarUInt32((uint)fragments);
                scratch.WriteVarUInt32((uint)count);
                scratch.WriteBytes(body, position, chunk);
                position += chunk;
                emit(scratch.Buffer, 0, scratch.Length);
            }
        }
    }
}
