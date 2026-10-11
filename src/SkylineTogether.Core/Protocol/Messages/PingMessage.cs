using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// A <c>Ping</c> (both directions, unreliable): RTT measurement. The receiver answers with the same sequence and
    /// sent time and <see cref="IsReply"/> set.
    /// <code>
    /// varint sequence | f64 sentSeconds | bool isReply
    /// </code>
    /// The time is written as 0 when not finite and rejected when read. The static codec methods are thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class PingMessage
    {
        public uint Sequence;
        public double SentSeconds;
        public bool IsReply;

        public static void Write(ByteWriter w, PingMessage m)
        {
            MessageFields.CheckMessage(w, nameof(w));
            MessageFields.CheckMessage(m, nameof(m));
            w.WriteVarUInt32(m.Sequence);
            MessageFields.WriteDouble(w, m.SentSeconds);
            w.WriteBool(m.IsReply);
        }

        public static PingMessage Read(ByteReader r)
        {
            var m = new PingMessage();
            ReadInto(r, m);
            return m;
        }

        /// <summary>Reads into an existing instance.</summary>
        public static void ReadInto(ByteReader r, PingMessage into)
        {
            MessageFields.CheckMessage(r, nameof(r));
            MessageFields.CheckMessage(into, nameof(into));
            into.Sequence = r.ReadVarUInt32();
            into.SentSeconds = MessageFields.ReadDouble(r, "Ping.SentSeconds");
            into.IsReply = r.ReadBool();
        }

        /// <summary>Reads a whole message body: <see cref="Read"/> plus a check that nothing follows.</summary>
        public static PingMessage Decode(byte[] buffer, int offset, int count)
        {
            var r = new ByteReader(buffer, offset, count);
            PingMessage m = Read(r);
            MessageFields.EnsureEnd(r, "Ping");
            return m;
        }
    }
}
