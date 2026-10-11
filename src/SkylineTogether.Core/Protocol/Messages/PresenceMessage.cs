using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// A <c>Presence</c> message (client → host → others, unreliable; ARCH §11.4): the player's cursor and tool ghost.
    /// The host relays it and sets <see cref="From"/> itself.
    /// <code>
    /// u8 from | f32 cursorX | f32 cursorY | f32 cursorZ | u8 toolKind | varint prefabRank+1 (0 = none) | u32 color
    /// | varint n (≤ 192) | n × f32 curveControlPoints | varint m (≤ 1024) | m × f32 footprints | f32 brushRadius
    /// </code>
    /// Floats are written as 0 when not finite and rejected when read; null arrays are written as empty and read back
    /// as empty arrays. The static codec methods are thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class PresenceMessage
    {
        /// <summary>16 cubic Bézier curves of 4 control points × 3 coordinates.</summary>
        public const int MaxCurveFloats = 192;

        /// <summary>Footprint polygons as x, z pairs (for example 128 building outlines of 4 corners).</summary>
        public const int MaxFootprintFloats = 1024;

        public PeerId From;
        public float CursorX, CursorY, CursorZ;
        public byte ToolKind;
        public PrefabRank Prefab;
        public uint Color;
        public float[] CurveControlPoints;
        public float[] Footprints;
        public float BrushRadius;

        public static void Write(ByteWriter w, PresenceMessage m)
        {
            MessageFields.CheckMessage(w, nameof(w));
            MessageFields.CheckMessage(m, nameof(m));
            w.WriteByte(m.From.Value);
            MessageFields.WriteFloat(w, m.CursorX);
            MessageFields.WriteFloat(w, m.CursorY);
            MessageFields.WriteFloat(w, m.CursorZ);
            w.WriteByte(m.ToolKind);
            MessageFields.WritePrefab(w, m.Prefab);
            w.WriteUInt32(m.Color);
            MessageFields.WriteFloats(w, m.CurveControlPoints, MaxCurveFloats, "CurveControlPoints");
            MessageFields.WriteFloats(w, m.Footprints, MaxFootprintFloats, "Footprints");
            MessageFields.WriteFloat(w, m.BrushRadius);
        }

        public static PresenceMessage Read(ByteReader r)
        {
            var m = new PresenceMessage();
            ReadInto(r, m);
            return m;
        }

        /// <summary>
        /// Reads into an existing instance (allocation-free for the 20 Hz relay path, ARCH §4.6): the arrays of
        /// <paramref name="into"/> are reused when their length matches the received count. On a
        /// <see cref="DecodeException"/> the contents of <paramref name="into"/> are undefined.
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
            into.CurveControlPoints = MessageFields.ReadFloats(r, MaxCurveFloats, "Presence.CurveControlPoints", into.CurveControlPoints);
            into.Footprints = MessageFields.ReadFloats(r, MaxFootprintFloats, "Presence.Footprints", into.Footprints);
            into.BrushRadius = MessageFields.ReadFloat(r, "Presence.BrushRadius");
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
