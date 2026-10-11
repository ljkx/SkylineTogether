using System;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// A <c>RepairRequest</c> (client → host; ARCH §8.5): slots of one class that the client found different, with a
    /// level hint; the host decides what to do and throttles instead of disconnecting.
    /// <code>
    /// u8 class | varint n (≤ 512) | n × varint slot (1 ... 0xFFFFFF) | u8 levelHint
    /// </code>
    /// A null slot array is written as empty and read back as an empty array. The static codec methods are thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class RepairRequestMessage
    {
        public ReplicationClass Class;
        public NetSlot[] Slots;
        public RepairLevel LevelHint;

        public static void Write(ByteWriter w, RepairRequestMessage m)
        {
            MessageFields.CheckMessage(w, nameof(w));
            MessageFields.CheckMessage(m, nameof(m));
            int count = m.Slots == null ? 0 : m.Slots.Length;
            if (count > Limits.MaxRepairSlots) throw new ArgumentException("more than 512 slots in one repair request", nameof(m));
            if (m.LevelHint > RepairLevel.Snapshot) throw new ArgumentException("undefined repair level", nameof(m));
            for (int i = 0; i < count; i++)
            {
                if (m.Slots[i].IsNone) throw new ArgumentException("slot 0 in a repair request", nameof(m));
            }
            MessageFields.WriteClass(w, m.Class, nameof(m));
            w.WriteVarUInt32((uint)count);
            for (int i = 0; i < count; i++) w.WriteVarUInt32(m.Slots[i].Value);
            w.WriteByte((byte)m.LevelHint);
        }

        public static RepairRequestMessage Read(ByteReader r)
        {
            MessageFields.CheckMessage(r, nameof(r));
            var m = new RepairRequestMessage { Class = MessageFields.ReadClass(r, "RepairRequest.Class") };
            int count = MessageFields.ReadCount(r, Limits.MaxRepairSlots, 1, "RepairRequest.Slots");
            m.Slots = count == 0 ? Array.Empty<NetSlot>() : new NetSlot[count];
            for (int i = 0; i < count; i++) m.Slots[i] = MessageFields.ReadSlot(r, false, "RepairRequest.Slots");
            byte level = r.ReadByte();
            if (level > (byte)RepairLevel.Snapshot) throw new DecodeException("RepairRequest: undefined level " + level.ToString(System.Globalization.CultureInfo.InvariantCulture));
            m.LevelHint = (RepairLevel)level;
            return m;
        }

        /// <summary>Reads a whole message body: <see cref="Read"/> plus a check that nothing follows.</summary>
        public static RepairRequestMessage Decode(byte[] buffer, int offset, int count)
        {
            var r = new ByteReader(buffer, offset, count);
            RepairRequestMessage m = Read(r);
            MessageFields.EnsureEnd(r, "RepairRequest");
            return m;
        }
    }
}
