using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// The payload of <see cref="ControlKind.ProposalReceived"/>: the host echoes the sequence number of a proposal on
    /// receipt, and the originator's 10 s timeout starts then (ARCH §5.4.2, §5.5.2).
    /// <code>
    /// varint seq
    /// </code>
    /// The static codec methods are thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class ProposalReceivedMessage
    {
        public uint Seq;

        public static void Write(ByteWriter w, ProposalReceivedMessage m)
        {
            MessageFields.CheckMessage(w, nameof(w));
            MessageFields.CheckMessage(m, nameof(m));
            w.WriteVarUInt32(m.Seq);
        }

        public static ProposalReceivedMessage Read(ByteReader r)
        {
            MessageFields.CheckMessage(r, nameof(r));
            return new ProposalReceivedMessage { Seq = r.ReadVarUInt32() };
        }

        /// <summary>Reads a whole payload: <see cref="Read"/> plus a check that nothing follows.</summary>
        public static ProposalReceivedMessage Decode(byte[] buffer, int offset, int count)
        {
            var r = new ByteReader(buffer, offset, count);
            ProposalReceivedMessage m = Read(r);
            MessageFields.EnsureEnd(r, "ProposalReceived");
            return m;
        }
    }
}
