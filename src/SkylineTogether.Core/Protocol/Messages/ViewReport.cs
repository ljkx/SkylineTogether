using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// A <c>View</c> report (client → host, unreliable, about 10 Hz; ARCHITECTURE.md#simulation-values): camera pivot, zoom, yaw and position,
    /// the active infoview and infomode mask, the selected and followed slots, open-panel flags and the panel's target.
    /// About 40 bytes:
    /// <code>
    /// f32 pivotX | f32 pivotZ | f32 zoom | f32 yaw | f32 camX | f32 camY | f32 camZ | u8 infoview | u32 infomodeMask
    /// | varint selected | varint followed | u16 panelFlags | varint panelTarget            (slots: 0 = none)
    /// </code>
    /// Floats are written as 0 when not finite and rejected when read, so a hostile report cannot poison the host's
    /// attention model. The static codec methods are thread-safe.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class ViewReport
    {
        public float PivotX, PivotZ, Zoom, Yaw;
        public float CamX, CamY, CamZ;
        public byte Infoview;
        public uint InfomodeMask;
        public NetSlot Selected, Followed;
        public ushort PanelFlags;
        public NetSlot PanelTarget;

        public static void Write(ByteWriter w, ViewReport m)
        {
            MessageFields.CheckMessage(w, nameof(w));
            MessageFields.CheckMessage(m, nameof(m));
            MessageFields.WriteFloat(w, m.PivotX);
            MessageFields.WriteFloat(w, m.PivotZ);
            MessageFields.WriteFloat(w, m.Zoom);
            MessageFields.WriteFloat(w, m.Yaw);
            MessageFields.WriteFloat(w, m.CamX);
            MessageFields.WriteFloat(w, m.CamY);
            MessageFields.WriteFloat(w, m.CamZ);
            w.WriteByte(m.Infoview);
            w.WriteUInt32(m.InfomodeMask);
            w.WriteVarUInt32(m.Selected.Value);
            w.WriteVarUInt32(m.Followed.Value);
            w.WriteUInt16(m.PanelFlags);
            w.WriteVarUInt32(m.PanelTarget.Value);
        }

        public static ViewReport Read(ByteReader r)
        {
            var m = new ViewReport();
            ReadInto(r, m);
            return m;
        }

        /// <summary>Reads into an existing instance (no allocation for the 10 Hz stream).</summary>
        public static void ReadInto(ByteReader r, ViewReport into)
        {
            MessageFields.CheckMessage(r, nameof(r));
            MessageFields.CheckMessage(into, nameof(into));
            into.PivotX = MessageFields.ReadFloat(r, "View.PivotX");
            into.PivotZ = MessageFields.ReadFloat(r, "View.PivotZ");
            into.Zoom = MessageFields.ReadFloat(r, "View.Zoom");
            into.Yaw = MessageFields.ReadFloat(r, "View.Yaw");
            into.CamX = MessageFields.ReadFloat(r, "View.CamX");
            into.CamY = MessageFields.ReadFloat(r, "View.CamY");
            into.CamZ = MessageFields.ReadFloat(r, "View.CamZ");
            into.Infoview = r.ReadByte();
            into.InfomodeMask = r.ReadUInt32();
            into.Selected = MessageFields.ReadSlot(r, true, "View.Selected");
            into.Followed = MessageFields.ReadSlot(r, true, "View.Followed");
            into.PanelFlags = r.ReadUInt16();
            into.PanelTarget = MessageFields.ReadSlot(r, true, "View.PanelTarget");
        }

        /// <summary>Reads a whole message body: <see cref="Read"/> plus a check that nothing follows.</summary>
        public static ViewReport Decode(byte[] buffer, int offset, int count)
        {
            var r = new ByteReader(buffer, offset, count);
            ViewReport m = Read(r);
            MessageFields.EnsureEnd(r, "View");
            return m;
        }
    }
}
