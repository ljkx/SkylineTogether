using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// <c>Refuse</c> (host → client): the join is refused. <see cref="MismatchReport"/> is the encoded
    /// <c>Session.MismatchReport</c> (core-session) for <see cref="RefuseReason.Mismatch"/>. The layout is stable across
    /// protocol versions, so a peer of another version can read why it was refused:
    /// <code>
    /// u8 reason | text detail (≤ 1024 chars) | bytes mismatchReport (≤ 4 MiB − 8 KiB)
    /// </code>
    /// Unknown reason values are accepted. The static codec methods are thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class RefuseMessage
    {
        public const int MaxDetailChars = 1024;
        public const int MaxMismatchReportBytes = 4 * 1024 * 1024 - 8 * 1024;

        public RefuseReason Reason;
        /// <summary>Free text: truncated to <see cref="MaxDetailChars"/> when written, control characters stripped when read.</summary>
        public string Detail;
        public byte[] MismatchReport;

        public static void Write(ByteWriter w, RefuseMessage m)
        {
            MessageFields.CheckMessage(w, nameof(w));
            MessageFields.CheckMessage(m, nameof(m));
            w.WriteByte((byte)m.Reason);
            MessageFields.WriteText(w, m.Detail, MaxDetailChars);
            MessageFields.WriteBytes(w, m.MismatchReport, MaxMismatchReportBytes, "MismatchReport");
        }

        public static RefuseMessage Read(ByteReader r)
        {
            MessageFields.CheckMessage(r, nameof(r));
            return new RefuseMessage
            {
                Reason = (RefuseReason)r.ReadByte(),
                Detail = MessageFields.ReadText(r, MaxDetailChars, "Refuse.Detail"),
                MismatchReport = MessageFields.ReadBytes(r, MaxMismatchReportBytes, "Refuse.MismatchReport")
            };
        }

        /// <summary>Reads a whole message body: <see cref="Read"/> plus a check that nothing follows.</summary>
        public static RefuseMessage Decode(byte[] buffer, int offset, int count)
        {
            var r = new ByteReader(buffer, offset, count);
            RefuseMessage m = Read(r);
            MessageFields.EnsureEnd(r, "Refuse");
            return m;
        }
    }
}
