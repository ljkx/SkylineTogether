using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// An <c>Ack</c> (client → host, about 10 Hz unreliable and reliable on reconnect; ARCH §4.5.4, §4.9.3): the last
    /// applied epoch and the last applied <c>MotionControl</c> sequence (which confirms interest-slot releases).
    /// <code>
    /// varint lastApplied | varint lastMotionControlSeq
    /// </code>
    /// The static codec methods are thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class AckMessage
    {
        public EpochId LastApplied;
        public uint LastMotionControlSeq;

        public static void Write(ByteWriter w, AckMessage m)
        {
            MessageFields.CheckMessage(w, nameof(w));
            MessageFields.CheckMessage(m, nameof(m));
            w.WriteVarUInt32(m.LastApplied.Value);
            w.WriteVarUInt32(m.LastMotionControlSeq);
        }

        public static AckMessage Read(ByteReader r)
        {
            var m = new AckMessage();
            ReadInto(r, m);
            return m;
        }

        /// <summary>Reads into an existing instance (no allocation per ack).</summary>
        public static void ReadInto(ByteReader r, AckMessage into)
        {
            MessageFields.CheckMessage(r, nameof(r));
            MessageFields.CheckMessage(into, nameof(into));
            into.LastApplied = new EpochId(r.ReadVarUInt32());
            into.LastMotionControlSeq = r.ReadVarUInt32();
        }

        /// <summary>Reads a whole message body: <see cref="Read"/> plus a check that nothing follows.</summary>
        public static AckMessage Decode(byte[] buffer, int offset, int count)
        {
            var r = new ByteReader(buffer, offset, count);
            AckMessage m = Read(r);
            MessageFields.EnsureEnd(r, "Ack");
            return m;
        }
    }
}
