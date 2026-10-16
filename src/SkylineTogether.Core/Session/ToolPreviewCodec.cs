using System;
using System.Globalization;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;

namespace SkylineTogether.Core.Session
{
    /// <summary>
    /// The wire form of a <see cref="ToolPreview"/>, carried as the opaque preview bytes of a
    /// <see cref="PresenceMessage"/>. Every message holds the whole preview (a keyframe, never a delta), so one received
    /// message heals any number of lost ones. An empty preview is zero bytes.
    /// <code>
    /// u8 version (2) | u8 flags (bit 0: truncated, bit 1: tool elevation follows) | u8 quant (bits 0-3: horizontal
    /// shift h, bits 4-7: vertical shift v, each 0..4) | f32 anchorX | f32 anchorY | f32 anchorZ
    /// | [i16 tool elevation in 1/16 m, when flag bit 1] | varint count (≤ 128) | count × shape
    /// shape = u8 kind | u8 flags (ToolPreviewFlags, bits 0-5) | payload
    ///   Curve     4 × point | u16 width
    ///   Segment   2 × point | u16 width
    ///   Circle    point | u16 diameter
    ///   Rect      point | u16 sizeX | u16 sizeZ | u16 yaw
    ///   Polyline  u8 n (2..64) | u16 width | n × point
    ///   ZoneBlock point | u16 yaw | u8 (cellsX-1) | (cellsZ-1) &lt;&lt; 4 (cellsX × cellsZ ≤ 64) | varint64 cells
    /// point = 3 × i16, (coordinate - anchor) / q with q = 2^shift / 16 m: x and z with the horizontal shift h, y (the
    /// real height) with its own vertical shift v (1/16 m within ±2 km of the anchor, 1 m within ±32 km), so heights
    /// keep 1/16 m in a preview that spans the map; u16 width/size/diameter in 1/16 m (≤ 4 km); u16 yaw in 1/65536 turn
    /// </code>
    /// Version 2 added the vertical shift, the tool elevation and the <see cref="ToolPreviewFlags.Elevated"/> and
    /// <see cref="ToolPreviewFlags.Underground"/> shape flags; it adds no byte to a preview without a tool elevation.
    /// <see cref="Encode"/> writes the shapes in order and stops before the first one that would exceed the byte
    /// budget (setting the truncated flag), so the sender lists the most important shapes first. A version other than
    /// <see cref="Version"/> is ignored by <see cref="Decode"/> (not an error); a malformed preview throws
    /// <see cref="DecodeException"/>. Thread-safe (no state).
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class ToolPreviewCodec
    {
        public const byte Version = 2;

        /// <summary>The largest preview, chosen so a full presence message stays one datagram
        /// (<see cref="Limits.MaxDatagramBytes"/>, never fragmented).</summary>
        public const int MaxBytes = PresenceMessage.MaxPreviewBytes;

        public const int MaxQuantShift = 4;

        /// <summary>The largest tool elevation the wire carries (metres, either sign); larger ones are clamped.</summary>
        public const float MaxElevation = short.MaxValue / 16f;

        private const byte FlagTruncated = 0x01;
        private const byte FlagElevation = 0x02;
        private const byte KnownFlags = FlagTruncated | FlagElevation;
        private const byte KnownShapeFlags = 0x3F;
        private const int HeaderBytes = 3 + 12 + 2 + 2; // the tool elevation; count as a 2-byte varint at most (≤ 128)
        private const int PointBytes = 6;
        private const float SizeScale = 16f;
        private const float TwoPi = (float)(2.0 * Math.PI);

        [ThreadStatic]
        private static ByteReader _reader; // allocation-free decoding on every thread

        /// <summary>Quantisation step of the points for <paramref name="shift"/> (metres).</summary>
        public static float Quantum(int shift) => (1 << shift) / 16f;

        /// <summary>
        /// Appends the preview to <paramref name="w"/>, at most <paramref name="maxBytes"/> bytes; returns the bytes
        /// written (0 for an empty preview, or when not even the first shape fits).
        /// </summary>
        public static int Encode(ToolPreview preview, ByteWriter w, int maxBytes = MaxBytes)
        {
            if (preview == null) throw new ArgumentNullException(nameof(preview));
            if (w == null) throw new ArgumentNullException(nameof(w));
            if (preview.Count == 0) return 0;
            int budget = Math.Min(maxBytes, MaxBytes) - HeaderBytes;
            int fit = 0, used = 0;
            for (int i = 0; i < preview.Count; i++)
            {
                int size = ShapeBytes(preview, i);
                if (used + size > budget) break;
                used += size;
                fit++;
            }
            if (fit == 0) return 0;
            bool truncated = preview.Truncated || fit < preview.Count;

            float[] d = preview.Data;
            PreviewPoint first = preview.PointAt(0, 0);
            float ax = WireFormat.Finite(first.X), ay = WireFormat.Finite(first.Y), az = WireFormat.Finite(first.Z);
            float extent = 0f, extentY = 0f;
            for (int i = 0; i < fit; i++)
            {
                int points = PointCount(preview, i);
                for (int k = 0; k < points; k++)
                {
                    PreviewPoint p = preview.PointAt(i, k);
                    extent = Math.Max(extent, Math.Max(Dist(p.X, ax), Dist(p.Z, az)));
                    extentY = Math.Max(extentY, Dist(p.Y, ay));
                }
            }
            int shift = ShiftFor(extent), shiftY = ShiftFor(extentY);
            float q = Quantum(shift), qy = Quantum(shiftY);
            short elevation = ElevationSteps(preview.Elevation);

            int start = w.Length;
            w.WriteByte(Version);
            w.WriteByte((byte)((truncated ? FlagTruncated : 0) | (elevation != 0 ? FlagElevation : 0)));
            w.WriteByte((byte)(shift | (shiftY << 4)));
            w.WriteSingle(ax);
            w.WriteSingle(ay);
            w.WriteSingle(az);
            if (elevation != 0) w.WriteUInt16(unchecked((ushort)elevation));
            w.WriteVarUInt32((uint)fit);
            for (int i = 0; i < fit; i++)
            {
                ToolPreviewShape shape = preview.ShapeAt(i);
                int o = preview.StartOf(i);
                w.WriteByte((byte)shape);
                w.WriteByte((byte)((byte)preview.FlagsAt(i) & KnownShapeFlags));
                switch (shape)
                {
                    case ToolPreviewShape.Curve:
                        for (int k = 0; k < 4; k++) WritePoint(w, d, o + 3 * k, ax, ay, az, q, qy);
                        WriteSize(w, d[o + 12]);
                        break;
                    case ToolPreviewShape.Segment:
                        WritePoint(w, d, o, ax, ay, az, q, qy);
                        WritePoint(w, d, o + 3, ax, ay, az, q, qy);
                        WriteSize(w, d[o + 6]);
                        break;
                    case ToolPreviewShape.Circle:
                        WritePoint(w, d, o, ax, ay, az, q, qy);
                        WriteSize(w, d[o + 3]);
                        break;
                    case ToolPreviewShape.Rect:
                        WritePoint(w, d, o, ax, ay, az, q, qy);
                        WriteSize(w, d[o + 3]);
                        WriteSize(w, d[o + 4]);
                        WriteYaw(w, d[o + 5]);
                        break;
                    case ToolPreviewShape.Polyline:
                    {
                        int n = preview.PolylinePointCount(i);
                        w.WriteByte((byte)n);
                        WriteSize(w, d[o]);
                        for (int k = 0; k < n; k++) WritePoint(w, d, o + 1 + 3 * k, ax, ay, az, q, qy);
                        break;
                    }
                    case ToolPreviewShape.ZoneBlock:
                    {
                        int cx = (int)d[o + 4], cz = (int)d[o + 5];
                        WritePoint(w, d, o, ax, ay, az, q, qy);
                        WriteYaw(w, d[o + 3]);
                        w.WriteByte((byte)((cx - 1) | ((cz - 1) << 4)));
                        w.WriteVarUInt64(preview.BitsAt(i));
                        break;
                    }
                    default:
                        throw new InvalidOperationException("unknown preview shape " + shape);
                }
            }
            return w.Length - start;
        }

        /// <summary>
        /// Reads a preview into <paramref name="into"/> (cleared first). An empty range is an empty preview. Returns false
        /// (with <paramref name="into"/> empty) for another <see cref="Version"/>; throws <see cref="DecodeException"/>
        /// for a malformed preview (<paramref name="into"/> is then cleared).
        /// </summary>
        public static bool Decode(byte[] buffer, int offset, int count, ToolPreview into)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            into.Clear();
            if (count == 0) return true;
            if (count > MaxBytes) throw new DecodeException("ToolPreview: " + count.ToString(CultureInfo.InvariantCulture) + " bytes exceed the limit of " + MaxBytes.ToString(CultureInfo.InvariantCulture));
            try
            {
                ByteReader r = _reader ?? (_reader = new ByteReader());
                r.Reset(buffer, offset, count);
                byte version = r.ReadByte();
                if (version != Version) return false;
                byte flags = MessageFields.ReadFlags(r, KnownFlags, "ToolPreview.Flags");
                int quant = r.ReadByte();
                int shift = quant & 0x0F, shiftY = quant >> 4;
                if (shift > MaxQuantShift || shiftY > MaxQuantShift) throw new DecodeException("ToolPreview: quantisation shift " + Math.Max(shift, shiftY).ToString(CultureInfo.InvariantCulture) + " exceeds " + MaxQuantShift.ToString(CultureInfo.InvariantCulture));
                float q = Quantum(shift), qy = Quantum(shiftY);
                float ax = MessageFields.ReadFloat(r, "ToolPreview.AnchorX");
                float ay = MessageFields.ReadFloat(r, "ToolPreview.AnchorY");
                float az = MessageFields.ReadFloat(r, "ToolPreview.AnchorZ");
                float elevation = 0f;
                if ((flags & FlagElevation) != 0)
                {
                    short steps = unchecked((short)r.ReadUInt16());
                    if (steps == 0) throw new DecodeException("ToolPreview: a tool elevation of 0 is never written");
                    elevation = steps / 16f;
                }
                int shapes = MessageFields.ReadCount(r, ToolPreview.MaxShapes, 2 + PointBytes + 2, "ToolPreview.Count");
                for (int i = 0; i < shapes; i++)
                {
                    byte kind = r.ReadByte();
                    var shapeFlags = (ToolPreviewFlags)MessageFields.ReadFlags(r, KnownShapeFlags, "ToolPreview.ShapeFlags");
                    bool added;
                    switch ((ToolPreviewShape)kind)
                    {
                        case ToolPreviewShape.Curve:
                        {
                            PreviewPoint a = ReadPoint(r, ax, ay, az, q, qy), b = ReadPoint(r, ax, ay, az, q, qy), c = ReadPoint(r, ax, ay, az, q, qy), e = ReadPoint(r, ax, ay, az, q, qy);
                            added = into.AddCurve(shapeFlags, a, b, c, e, ReadSize(r));
                            break;
                        }
                        case ToolPreviewShape.Segment:
                        {
                            PreviewPoint a = ReadPoint(r, ax, ay, az, q, qy), b = ReadPoint(r, ax, ay, az, q, qy);
                            added = into.AddSegment(shapeFlags, a, b, ReadSize(r));
                            break;
                        }
                        case ToolPreviewShape.Circle:
                        {
                            PreviewPoint p = ReadPoint(r, ax, ay, az, q, qy);
                            added = into.AddCircle(shapeFlags, p, ReadSize(r));
                            break;
                        }
                        case ToolPreviewShape.Rect:
                        {
                            PreviewPoint p = ReadPoint(r, ax, ay, az, q, qy);
                            float sx = ReadSize(r), sz = ReadSize(r);
                            added = into.AddRect(shapeFlags, p, sx, sz, ReadYaw(r));
                            break;
                        }
                        case ToolPreviewShape.Polyline:
                        {
                            int n = r.ReadByte();
                            if (n < 2 || n > ToolPreview.MaxPolylinePoints) throw new DecodeException("ToolPreview: polyline of " + n.ToString(CultureInfo.InvariantCulture) + " points");
                            added = into.BeginPolyline(shapeFlags, ReadSize(r));
                            for (int k = 0; k < n && added; k++) added = into.AddPolylinePoint(ReadPoint(r, ax, ay, az, q, qy));
                            if (added) added = into.EndPolyline();
                            break;
                        }
                        case ToolPreviewShape.ZoneBlock:
                        {
                            PreviewPoint p = ReadPoint(r, ax, ay, az, q, qy);
                            float yaw = ReadYaw(r);
                            byte cells = r.ReadByte();
                            int cx = (cells & 0x0F) + 1, cz = (cells >> 4) + 1;
                            if (cx * cz > ToolPreview.MaxZoneCells) throw new DecodeException("ToolPreview: zone block of " + cx.ToString(CultureInfo.InvariantCulture) + " x " + cz.ToString(CultureInfo.InvariantCulture) + " cells");
                            ulong bits = r.ReadVarUInt64();
                            if ((bits & ~ToolPreview.ZoneMask(cx * cz)) != 0) throw new DecodeException("ToolPreview: zone cell bits beyond the block");
                            added = into.AddZoneBlock(shapeFlags, p, yaw, cx, cz, bits);
                            break;
                        }
                        default:
                            throw new DecodeException("ToolPreview: unknown shape kind " + kind.ToString(CultureInfo.InvariantCulture));
                    }
                    if (!added) throw new DecodeException("ToolPreview: more shapes than a preview holds");
                }
                MessageFields.EnsureEnd(r, "ToolPreview");
                into.Truncated = (flags & FlagTruncated) != 0;
                into.Elevation = elevation;
                return true;
            }
            catch (DecodeException)
            {
                into.Clear();
                throw;
            }
        }

        /// <summary>True when an encoded preview carries the truncated flag (some shapes did not fit).</summary>
        public static bool Truncated(byte[] buffer, int offset, int count)
        {
            return buffer != null && count >= 2 && offset >= 0 && offset + count <= buffer.Length && buffer[offset] == Version && (buffer[offset + 1] & FlagTruncated) != 0;
        }

        /// <summary>Bytes shape <paramref name="i"/> takes on the wire.</summary>
        public static int ShapeBytes(ToolPreview preview, int i)
        {
            switch (preview.ShapeAt(i))
            {
                case ToolPreviewShape.Curve: return 2 + 4 * PointBytes + 2;
                case ToolPreviewShape.Segment: return 2 + 2 * PointBytes + 2;
                case ToolPreviewShape.Circle: return 2 + PointBytes + 2;
                case ToolPreviewShape.Rect: return 2 + PointBytes + 6;
                case ToolPreviewShape.Polyline: return 2 + 1 + 2 + preview.PolylinePointCount(i) * PointBytes;
                case ToolPreviewShape.ZoneBlock: return 2 + PointBytes + 2 + 1 + WireFormat.VarUInt64Size(preview.BitsAt(i));
                default: return int.MaxValue / 4;
            }
        }

        private static int PointCount(ToolPreview preview, int i)
        {
            switch (preview.ShapeAt(i))
            {
                case ToolPreviewShape.Curve: return 4;
                case ToolPreviewShape.Segment: return 2;
                case ToolPreviewShape.Polyline: return preview.PolylinePointCount(i);
                default: return 1;
            }
        }

        private static float Dist(float v, float anchor) => WireFormat.IsFinite(v) ? Math.Abs(v - anchor) : 0f;

        /// <summary>The smallest shift whose quantum reaches <paramref name="extent"/> metres from the anchor in an i16.</summary>
        private static int ShiftFor(float extent)
        {
            int shift = 0;
            while (shift < MaxQuantShift && extent / Quantum(shift) > short.MaxValue) shift++;
            return shift;
        }

        /// <summary>The tool elevation in 1/16 m steps, clamped (0 for none or a non-finite value).</summary>
        private static short ElevationSteps(float elevation)
        {
            if (!WireFormat.IsFinite(elevation)) return 0;
            double steps = Math.Round(elevation * 16.0);
            if (steps > short.MaxValue) steps = short.MaxValue;
            if (steps < -short.MaxValue) steps = -short.MaxValue;
            return (short)steps;
        }

        private static void WritePoint(ByteWriter w, float[] d, int o, float ax, float ay, float az, float q, float qy)
        {
            WriteCoord(w, d[o], ax, q);
            WriteCoord(w, d[o + 1], ay, qy);
            WriteCoord(w, d[o + 2], az, q);
        }

        private static void WriteCoord(ByteWriter w, float v, float anchor, float q)
        {
            double steps = WireFormat.IsFinite(v) ? Math.Round((v - anchor) / q) : 0d;
            if (steps > short.MaxValue) steps = short.MaxValue;
            if (steps < -short.MaxValue) steps = -short.MaxValue;
            w.WriteUInt16(unchecked((ushort)(short)steps));
        }

        private static PreviewPoint ReadPoint(ByteReader r, float ax, float ay, float az, float q, float qy)
        {
            float x = ax + unchecked((short)r.ReadUInt16()) * q;
            float y = ay + unchecked((short)r.ReadUInt16()) * qy;
            float z = az + unchecked((short)r.ReadUInt16()) * q;
            return new PreviewPoint(x, y, z);
        }

        private static void WriteSize(ByteWriter w, float size)
        {
            double v = WireFormat.IsFinite(size) ? Math.Round(size * SizeScale) : 0d;
            if (v < 0) v = 0;
            if (v > ushort.MaxValue) v = ushort.MaxValue;
            w.WriteUInt16((ushort)v);
        }

        private static float ReadSize(ByteReader r) => r.ReadUInt16() / SizeScale;

        private static void WriteYaw(ByteWriter w, float yaw)
        {
            double turns = WireFormat.IsFinite(yaw) ? yaw / (2.0 * Math.PI) : 0d;
            turns -= Math.Floor(turns);
            w.WriteUInt16(unchecked((ushort)((int)Math.Round(turns * 65536.0) & 0xFFFF)));
        }

        private static float ReadYaw(ByteReader r) => r.ReadUInt16() * (TwoPi / 65536f);
    }
}
