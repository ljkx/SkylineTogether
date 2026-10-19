using System;
using System.Collections.Generic;
using System.Globalization;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// Restores messages split by <see cref="Fragmenter"/>, one instance per peer and connection. Fragments arrive on a
    /// reliable ordered lane, so the fragments of one message must arrive in index order; a gap, a reordering, a
    /// duplicate, an inconsistent header or a size above the kind's limit is a protocol violation
    /// (<see cref="DecodeException"/>), after which the partial message is dropped. Fragments of up to
    /// <see cref="Limits.MaxPartialMessagesPerPeer"/> messages may interleave (messages sent from different threads);
    /// a further concurrent message is rejected. Memory per peer is therefore bounded by four messages and by
    /// the bytes actually received (a buffer grows with the fragments instead of being allocated at the declared total,
    /// so a fragment that merely declares 8 MB costs nothing).
    /// <para>Not thread-safe: used on the network thread of its connection.</para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Network)]
    public sealed class Reassembler
    {
        private sealed class Partial
        {
            public uint MessageId;
            public MessageKind Kind;
            public int FragmentCount;
            public int TotalLength;
            public int NextIndex;
            public int Received;
            public byte[] Buffer;
            /// <summary>When the last fragment of this message arrived (the caller's clock).</summary>
            public double LastFragmentAt;
        }

        private readonly List<Partial> _partials = new List<Partial>(Limits.MaxPartialMessagesPerPeer);
        private long _partialBytes;

        /// <summary>Messages with some but not all fragments received.</summary>
        public int PartialMessages => _partials.Count;

        /// <summary>Bytes held for partial messages (their declared total lengths).</summary>
        public long PartialBytes => _partialBytes;

        /// <summary>
        /// Takes one fragment (an envelope with <see cref="EnvelopeFlags.Fragment"/> from <see cref="EnvelopeCodec.Decode"/>).
        /// Returns true when it completed a message: <paramref name="message"/>[0 .. <paramref name="length"/> − 1] is
        /// then the original encoded envelope, in a new array owned by the caller, to be decoded with
        /// <see cref="EnvelopeCodec.Decode"/>. Throws <see cref="ArgumentException"/> for an envelope that is not a
        /// fragment and <see cref="DecodeException"/> for a malformed or out-of-order fragment.
        /// </summary>
        /// <remarks><c>maxTotal</c> is a lower limit on the message's total length than the kind's own; <c>now</c> is the receiver's time, which <see cref="EvictIdle"/> compares against.</remarks>
        public bool Accept(Envelope fragment, out byte[] message, out int length, int maxTotal = int.MaxValue, double now = 0)
        {
            message = null;
            length = 0;
            if ((fragment.Flags & EnvelopeFlags.Fragment) == 0) throw new ArgumentException("the envelope is not a fragment", nameof(fragment));
            WireFormat.CheckRange(fragment.Body, fragment.BodyOffset, fragment.BodyCount, "fragment.Body");

            byte[] body = fragment.Body;
            int position = fragment.BodyOffset;
            int end = fragment.BodyOffset + fragment.BodyCount;
            string what = fragment.Kind + " fragment";
            uint messageId = WireFormat.ReadVarUInt32(body, ref position, end, what + " id");
            uint index = WireFormat.ReadVarUInt32(body, ref position, end, what + " index");
            uint count = WireFormat.ReadVarUInt32(body, ref position, end, what + " count");
            uint total = WireFormat.ReadVarUInt32(body, ref position, end, what + " length");
            int chunk = end - position;
            if (count < 2 || count > Limits.MaxFragmentsPerMessage)
            {
                throw new DecodeException(what + ": fragment count " + WireFormat.Format(count) + " outside 2 ... "
                                          + Limits.MaxFragmentsPerMessage.ToString(CultureInfo.InvariantCulture));
            }
            if (index >= count) throw new DecodeException(what + ": index " + WireFormat.Format(index) + " of " + WireFormat.Format(count));
            long max = Math.Min((long)Limits.MaxBodyBytes(fragment.Kind), Math.Max(0L, maxTotal)) + Limits.EnvelopeOverheadBytes;
            if (total <= Limits.FragmentThreshold || total > max)
            {
                throw new DecodeException(what + ": total length " + WireFormat.Format(total) + " outside "
                                          + (Limits.FragmentThreshold + 1).ToString(CultureInfo.InvariantCulture) + " ... " + WireFormat.Format(max));
            }
            if (chunk <= 0) throw new DecodeException(what + ": empty fragment");

            Partial partial = Find(messageId);
            if (partial == null)
            {
                if (index != 0) throw new DecodeException(what + " " + WireFormat.Format(index) + " of an unknown message (lost or reordered fragment)");
                if (_partials.Count >= Limits.MaxPartialMessagesPerPeer)
                {
                    throw new DecodeException(what + ": more than " + Limits.MaxPartialMessagesPerPeer.ToString(CultureInfo.InvariantCulture) + " partial messages");
                }
                partial = new Partial
                {
                    MessageId = messageId,
                    Kind = fragment.Kind,
                    FragmentCount = (int)count,
                    TotalLength = (int)total,
                    // grown as fragments arrive (Grow below) instead of allocated at the declared total: a
                    // first fragment of a few bytes declaring 8 MiB made the host allocate 8 MiB (32 MiB for four),
                    // even from a peer that had not finished its handshake. Memory now follows the bytes received.
                    Buffer = new byte[Math.Min((int)total, chunk)]
                };
                _partials.Add(partial);
                _partialBytes += total;
            }
            else
            {
                if (partial.Kind != fragment.Kind || partial.FragmentCount != (int)count || partial.TotalLength != (int)total)
                {
                    Drop(partial);
                    throw new DecodeException(what + ": header differs from the first fragment of the message");
                }
                if (index != (uint)partial.NextIndex)
                {
                    Drop(partial);
                    throw new DecodeException(what + ": index " + WireFormat.Format(index) + " where "
                                              + partial.NextIndex.ToString(CultureInfo.InvariantCulture) + " was expected (lost, duplicated or reordered fragment)");
                }
            }

            if (chunk > partial.TotalLength - partial.Received)
            {
                Drop(partial);
                throw new DecodeException(what + ": fragments exceed the declared length " + partial.TotalLength.ToString(CultureInfo.InvariantCulture));
            }
            partial.LastFragmentAt = now;
            Grow(partial, partial.Received + chunk);
            Buffer.BlockCopy(body, position, partial.Buffer, partial.Received, chunk);
            partial.Received += chunk;
            partial.NextIndex++;
            if (partial.NextIndex < partial.FragmentCount) return false;

            Drop(partial);
            if (partial.Received != partial.TotalLength)
            {
                throw new DecodeException(what + ": fragments carry " + partial.Received.ToString(CultureInfo.InvariantCulture)
                                          + " bytes, declared " + partial.TotalLength.ToString(CultureInfo.InvariantCulture));
            }
            byte[] result = partial.Buffer;
            if (result[0] != (byte)partial.Kind || (result[1] & (byte)EnvelopeFlags.Fragment) != 0)
            {
                throw new DecodeException(what + ": the reassembled bytes are not an unfragmented " + partial.Kind + " envelope");
            }
            message = result;
            length = partial.TotalLength;
            return true;
        }

        /// <summary>
        /// Drops the partial messages whose last fragment arrived more than <paramref name="maxIdleSeconds"/> before
        /// <paramref name="now"/>: fragments come in order on a reliable lane, so a message that stopped half-way was
        /// abandoned by its sender (or the sender misbehaves), and its buffer would otherwise stay pinned for the life of
        /// the connection. A later fragment of an evicted message is rejected like any fragment of an unknown message.
        /// Returns the number of messages dropped.
        /// </summary>
        public int EvictIdle(double now, double maxIdleSeconds)
        {
            int dropped = 0;
            for (int i = _partials.Count - 1; i >= 0; i--)
            {
                if (now - _partials[i].LastFragmentAt <= maxIdleSeconds) continue;
                Drop(_partials[i]);
                dropped++;
            }
            return dropped;
        }

        /// <summary>Drops every partial message (connection closed or reset).</summary>
        public void Reset()
        {
            _partials.Clear();
            _partialBytes = 0;
        }

        /// <summary>Makes room for <paramref name="needed"/> bytes (doubling, capped at the declared total).</summary>
        private static void Grow(Partial partial, int needed)
        {
            if (partial.Buffer.Length >= needed) return;
            int capacity = (int)Math.Min(partial.TotalLength, Math.Max(needed, 2L * partial.Buffer.Length));
            var grown = new byte[capacity];
            Buffer.BlockCopy(partial.Buffer, 0, grown, 0, partial.Received);
            partial.Buffer = grown;
        }

        private Partial Find(uint messageId)
        {
            for (int i = 0; i < _partials.Count; i++)
            {
                if (_partials[i].MessageId == messageId) return _partials[i];
            }
            return null;
        }

        private void Drop(Partial partial)
        {
            if (_partials.Remove(partial)) _partialBytes -= partial.TotalLength;
        }
    }
}
