using System;
using System.Globalization;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// A <c>HashDetail</c> (host → client, repair level 1; docs/history/ARCHITECTURE-original.md §8.5): the per-slot combined hashes of one bucket of one
    /// class as of an epoch. <see cref="Slots"/> and <see cref="Hashes"/> are parallel arrays; every slot belongs to the
    /// bucket (<c>slot &gt;&gt; 10 == bucket</c>, docs/history/ARCHITECTURE-original.md §8.2), so a bucket detail holds at most 1024 entries.
    /// <code>
    /// u8 class | varint bucket (≤ 16383) | varint atEpoch | varint n (≤ 1024) | n × varint slot (1 ... 0xFFFFFF, in the bucket) | n × u32 hash
    /// </code>
    /// Null arrays are written as empty and read back as empty arrays. The static codec methods are thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class HashDetailMessage
    {
        public ReplicationClass Class;
        public int Bucket;
        public EpochId AtEpoch;
        public NetSlot[] Slots;
        public uint[] Hashes;

        public static void Write(ByteWriter w, HashDetailMessage m)
        {
            MessageFields.CheckMessage(w, nameof(w));
            MessageFields.CheckMessage(m, nameof(m));
            int count = m.Slots == null ? 0 : m.Slots.Length;
            int hashes = m.Hashes == null ? 0 : m.Hashes.Length;
            if (count != hashes) throw new ArgumentException("Slots and Hashes must have the same length", nameof(m));
            if (count > Limits.MaxHashDetailSlots) throw new ArgumentException("more than 1024 slots in one bucket detail", nameof(m));
            for (int i = 0; i < count; i++)
            {
                if (m.Slots[i].IsNone) throw new ArgumentException("slot 0 in a bucket detail", nameof(m));
                if (m.Slots[i].Bucket != m.Bucket) throw new ArgumentException(m.Slots[i] + " is not in bucket " + m.Bucket.ToString(CultureInfo.InvariantCulture), nameof(m));
            }
            MessageFields.WriteClass(w, m.Class, nameof(m));
            MessageFields.WriteBucket(w, m.Bucket, nameof(m));
            w.WriteVarUInt32(m.AtEpoch.Value);
            w.WriteVarUInt32((uint)count);
            for (int i = 0; i < count; i++) w.WriteVarUInt32(m.Slots[i].Value);
            for (int i = 0; i < count; i++) w.WriteUInt32(m.Hashes[i]);
        }

        public static HashDetailMessage Read(ByteReader r)
        {
            MessageFields.CheckMessage(r, nameof(r));
            var m = new HashDetailMessage
            {
                Class = MessageFields.ReadClass(r, "HashDetail.Class"),
                Bucket = MessageFields.ReadBucket(r, "HashDetail.Bucket"),
                AtEpoch = new EpochId(r.ReadVarUInt32())
            };
            int count = MessageFields.ReadCount(r, Limits.MaxHashDetailSlots, 5, "HashDetail.Slots");
            m.Slots = count == 0 ? Array.Empty<NetSlot>() : new NetSlot[count];
            m.Hashes = count == 0 ? Array.Empty<uint>() : new uint[count];
            for (int i = 0; i < count; i++)
            {
                NetSlot slot = MessageFields.ReadSlot(r, false, "HashDetail.Slots");
                if (slot.Bucket != m.Bucket)
                {
                    throw new DecodeException("HashDetail: " + slot + " is not in bucket " + m.Bucket.ToString(CultureInfo.InvariantCulture));
                }
                m.Slots[i] = slot;
            }
            for (int i = 0; i < count; i++) m.Hashes[i] = r.ReadUInt32();
            return m;
        }

        /// <summary>Reads a whole message body: <see cref="Read"/> plus a check that nothing follows.</summary>
        public static HashDetailMessage Decode(byte[] buffer, int offset, int count)
        {
            var r = new ByteReader(buffer, offset, count);
            HashDetailMessage m = Read(r);
            MessageFields.EnsureEnd(r, "HashDetail");
            return m;
        }
    }
}
