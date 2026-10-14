using System;
using System.Globalization;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// A <c>Presence</c> message (client → host → others, unreliable; ARCHITECTURE.md#presence-and-tool-previews): the player's cursor, tool and live
    /// tool preview. The host relays it and sets <see cref="From"/> itself.
    /// <code>
    /// u8 from | f32 cursorX | f32 cursorY | f32 cursorZ | u8 toolKind | varint prefabRank+1 (0 = none) | u32 color
    /// | f32 brushRadius | varint n (≤ 1100) | n × u8 preview
    /// </code>
    /// The preview bytes are opaque here (<see cref="Session.ToolPreviewCodec"/>, versioned on its
    /// own; empty = no preview), so a relay never depends on their layout. The largest message stays one datagram
    /// (<see cref="Limits.MaxDatagramBytes"/>). Floats are written as 0 when not finite and rejected when read. The
    /// static codec methods are thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class PresenceMessage
    {
        /// <summary>Preview bytes at most: the whole message with envelope stays below <see cref="Limits.MaxDatagramBytes"/>.</summary>
        public const int MaxPreviewBytes = 1100;

        public PeerId From;
        public float CursorX, CursorY, CursorZ;
        public byte ToolKind;
        public PrefabRank Prefab;
        public uint Color;
        public float BrushRadius;

        /// <summary>The encoded tool preview in <c>Preview[0 .. PreviewLength-1]</c> (the array may be longer; null = none).</summary>
        public byte[] Preview;
        public int PreviewLength;

        public static void Write(ByteWriter w, PresenceMessage m)
        {
            MessageFields.CheckMessage(w, nameof(w));
            MessageFields.CheckMessage(m, nameof(m));
            int preview = m.Preview == null ? 0 : m.PreviewLength;
            if (preview < 0 || preview > MaxPreviewBytes || (m.Preview != null && preview > m.Preview.Length))
            {
                throw new ArgumentException("Preview has " + preview.ToString(CultureInfo.InvariantCulture) + " bytes (limit " + MaxPreviewBytes.ToString(CultureInfo.InvariantCulture) + ")", nameof(m));
            }
            w.WriteByte(m.From.Value);
            MessageFields.WriteFloat(w, m.CursorX);
            MessageFields.WriteFloat(w, m.CursorY);
            MessageFields.WriteFloat(w, m.CursorZ);
            w.WriteByte(m.ToolKind);
            MessageFields.WritePrefab(w, m.Prefab);
            w.WriteUInt32(m.Color);
            MessageFields.WriteFloat(w, m.BrushRadius);
            w.WriteVarUInt32((uint)preview);
            if (preview > 0) w.WriteBytes(m.Preview, 0, preview);
        }

        public static PresenceMessage Read(ByteReader r)
        {
            var m = new PresenceMessage();
            ReadInto(r, m);
            return m;
        }

        /// <summary>
        /// Reads into an existing instance (allocation-free for the 20 Hz relay path, ARCHITECTURE.md#envelope-and-message-kinds): the preview array of
        /// <paramref name="into"/> is reused when it is large enough. On a <see cref="DecodeException"/> the contents of
        /// <paramref name="into"/> are undefined.
        /// </summary>
        public static void ReadInto(ByteReader r, PresenceMessage into)
        {
            MessageFields.CheckMessage(r, nameof(r));
            MessageFields.CheckMessage(into, nameof(into));
            into.From = new PeerId(r.ReadByte());
            into.CursorX = MessageFields.ReadFloat(r, "Presence.CursorX");
            into.CursorY = MessageFields.ReadFloat(r, "Presence.CursorY");
            into.CursorZ = MessageFields.ReadFloat(r, "Presence.CursorZ");
            into.ToolKind = r.ReadByte();
            into.Prefab = MessageFields.ReadPrefab(r, "Presence.Prefab");
            into.Color = r.ReadUInt32();
            into.BrushRadius = MessageFields.ReadFloat(r, "Presence.BrushRadius");
            uint length = r.ReadVarUInt32();
            if (length > MaxPreviewBytes) throw new DecodeException("Presence.Preview: " + WireFormat.Format(length) + " bytes exceed the limit of " + MaxPreviewBytes.ToString(CultureInfo.InvariantCulture));
            if (length > (uint)r.Remaining) throw new DecodeException("Presence.Preview: " + WireFormat.Format(length) + " bytes overrun the message");
            into.PreviewLength = (int)length;
            if (length == 0) return;
            if (into.Preview == null || into.Preview.Length < length) into.Preview = new byte[length];
            r.ReadBytes(into.Preview, 0, (int)length);
        }

        /// <summary>Reads a whole message body: <see cref="Read"/> plus a check that nothing follows.</summary>
        public static PresenceMessage Decode(byte[] buffer, int offset, int count)
        {
            var r = new ByteReader(buffer, offset, count);
            PresenceMessage m = Read(r);
            MessageFields.EnsureEnd(r, "Presence");
            return m;
        }
    }
}
