using System;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// A <c>Control</c> message (ARCH Appendix C): a <see cref="ControlKind"/> and its payload.
    /// <code>
    /// u8 kind | bytes payload (≤ 8 MiB − 16)
    /// </code>
    /// Unknown kinds are read without error (<see cref="IsKnownKind"/> tells them apart); the router drops them
    /// (ARCH §14.3). The static codec methods are thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class ControlMessage
    {
        public const int MaxPayloadBytes = Limits.MaxUncompressedBytes - 16;

        public ControlKind Kind;
        public byte[] Payload;

        /// <summary>True for the kinds of <see cref="ControlKind"/>.</summary>
        public static bool IsKnownKind(ControlKind kind) => kind <= ControlKind.LoadProgress;

        public static void Write(ByteWriter w, ControlMessage m)
        {
            MessageFields.CheckMessage(w, nameof(w));
            MessageFields.CheckMessage(m, nameof(m));
            w.WriteByte((byte)m.Kind);
            MessageFields.WriteBytes(w, m.Payload, MaxPayloadBytes, "Payload");
        }

        /// <summary>Writes a control message whose payload is <c>payload[offset .. offset+count-1]</c> (no intermediate array).</summary>
        public static void Write(ByteWriter w, ControlKind kind, byte[] payload, int offset, int count)
        {
            MessageFields.CheckMessage(w, nameof(w));
            WireFormat.CheckOptionalRange(payload, offset, count, nameof(payload));
            if (count > MaxPayloadBytes) throw new ArgumentException("control payload above the limit", nameof(count));
            w.WriteByte((byte)kind);
            w.WriteVarUInt32((uint)count);
            if (count > 0) w.WriteBytes(payload, offset, count);
        }

        public static ControlMessage Read(ByteReader r)
        {
            MessageFields.CheckMessage(r, nameof(r));
            return new ControlMessage
            {
                Kind = (ControlKind)r.ReadByte(),
                Payload = MessageFields.ReadBytes(r, MaxPayloadBytes, "Control.Payload")
            };
        }

        /// <summary>Reads a whole message body: <see cref="Read"/> plus a check that nothing follows.</summary>
        public static ControlMessage Decode(byte[] buffer, int offset, int count)
        {
            var r = new ByteReader(buffer, offset, count);
            ControlMessage m = Read(r);
            MessageFields.EnsureEnd(r, "Control");
            return m;
        }
    }
}
