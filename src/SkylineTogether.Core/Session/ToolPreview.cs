using System;

namespace SkylineTogether.Core.Session
{
    /// <summary>The kind of one shape of a <see cref="ToolPreview"/>. Values travel on the wire and never change.</summary>
    public enum ToolPreviewShape : byte
    {
        /// <summary>A cubic Bézier with a width (net edges): 4 points, then the width (<see cref="ToolPreview.CurveFloats"/>).</summary>
        Curve = 1,

        /// <summary>A straight band: 2 points, then the width (<see cref="ToolPreview.SegmentFloats"/>).</summary>
        Segment = 2,

        /// <summary>A disc: centre, then the diameter (<see cref="ToolPreview.CircleFloats"/>; net nodes, round objects, water sources).</summary>
        Circle = 3,

        /// <summary>An oriented rectangle on the ground: centre, size along the local x and z axes, yaw in radians
        /// (<see cref="ToolPreview.RectFloats"/>; building lots, object bounds).</summary>
        Rect = 4,

        /// <summary>A line through 2 ... <see cref="ToolPreview.MaxPolylinePoints"/> points: the width, then the points
        /// (areas with <see cref="ToolPreviewFlags.Closed"/>, transport lines).</summary>
        Polyline = 5,

        /// <summary>A zone block: centre, yaw, cells along the local x axis, cells along the local z axis
        /// (<see cref="ToolPreview.ZoneBlockFloats"/>), and in <see cref="ToolPreview.BitsAt"/> the painted cells
        /// (bit <c>z * cellsX + x</c>).</summary>
        ZoneBlock = 6,
    }

    /// <summary>Flags of one shape of a <see cref="ToolPreview"/>. Values travel on the wire and never change.</summary>
    [Flags]
    public enum ToolPreviewFlags : byte
    {
        None = 0,

        /// <summary>The tool removes this (bulldozing, a replaced piece).</summary>
        Delete = 1,

        /// <summary>The game marks this placement as invalid.</summary>
        Error = 2,

        /// <summary>A <see cref="ToolPreviewShape.Polyline"/> that closes back to its first point.</summary>
        Closed = 4,

        /// <summary>A sub-element (part of a building, a node, a sub-net): drawn fainter.</summary>
        Secondary = 8,

        /// <summary>Above the ground (an elevated road or bridge, a raised node, an object placed at an elevation): its
        /// points carry the real height and the shape is drawn there, with drop lines to the terrain.</summary>
        Elevated = 16,

        /// <summary>Below the ground (a tunnel, a lowered node): drawn on the terrain surface in a dashed, dimmer style.</summary>
        Underground = 32,
    }

    /// <summary>One point of a <see cref="ToolPreview"/> in world coordinates (metres); <see cref="Y"/> is the real height.</summary>
    public struct PreviewPoint
    {
        public float X, Y, Z;

        public PreviewPoint(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }

    /// <summary>
    /// The live preview of a player's tool: what the player sees while building — net curves, object
    /// footprints, area outlines, painted zone cells, line paths — reduced to a few drawable shapes. The sender fills it
    /// from the game's own tool output, <see cref="ToolPreviewCodec"/> carries it inside a presence message, and the
    /// receivers draw it in the player's colour. Fixed capacity, no allocation after construction: every <c>Add</c>
    /// returns false (and sets <see cref="Truncated"/>) when a limit is reached. Main thread only.
    /// </summary>
    [ThreadAffinity(ThreadKind.Main)]
    public sealed class ToolPreview
    {
        public const int MaxShapes = 128;
        public const int MaxFloats = 2048;
        public const int MaxPolylinePoints = 64;

        /// <summary>Cells of one zone block at most (the game's blocks are up to 10 × 6).</summary>
        public const int MaxZoneCells = 64;

        public const int CurveFloats = 13;
        public const int SegmentFloats = 7;
        public const int CircleFloats = 4;
        public const int RectFloats = 6;
        public const int ZoneBlockFloats = 6;

        private readonly ToolPreviewShape[] _shapes = new ToolPreviewShape[MaxShapes];
        private readonly ToolPreviewFlags[] _flags = new ToolPreviewFlags[MaxShapes];
        private readonly int[] _start = new int[MaxShapes];
        private readonly int[] _length = new int[MaxShapes];
        private readonly ulong[] _bits = new ulong[MaxShapes];
        private readonly float[] _data = new float[MaxFloats];
        private bool _openPolyline;

        /// <summary>Shapes held.</summary>
        public int Count { get; private set; }

        /// <summary>Floats used in <see cref="Data"/>.</summary>
        public int FloatCount { get; private set; }

        /// <summary>Something did not fit (here, or in the sender's message): the preview is incomplete.</summary>
        public bool Truncated { get; set; }

        /// <summary>
        /// The tool's elevation setting in metres (the net tool's elevation steps; negative: underground), 0 when the
        /// tool has none. Travels with a non-empty preview and is shown next to the player's name tag.
        /// </summary>
        public float Elevation { get; set; }

        /// <summary>The shapes' floats; shape <c>i</c> uses <c>Data[StartOf(i) .. StartOf(i) + LengthOf(i) - 1]</c>.</summary>
        public float[] Data => _data;

        public bool IsEmpty => Count == 0;

        public ToolPreviewShape ShapeAt(int i) => _shapes[i];

        public ToolPreviewFlags FlagsAt(int i) => _flags[i];

        public int StartOf(int i) => _start[i];

        public int LengthOf(int i) => _length[i];

        public ulong BitsAt(int i) => _bits[i];

        /// <summary>Point <paramref name="k"/> of shape <paramref name="i"/> (for a polyline the points follow the width).</summary>
        public PreviewPoint PointAt(int i, int k)
        {
            int o = _start[i] + (_shapes[i] == ToolPreviewShape.Polyline ? 1 : 0) + 3 * k;
            return new PreviewPoint(_data[o], _data[o + 1], _data[o + 2]);
        }

        /// <summary>Points of a polyline shape.</summary>
        public int PolylinePointCount(int i) => _shapes[i] == ToolPreviewShape.Polyline ? (_length[i] - 1) / 3 : 0;

        public void Clear()
        {
            Count = 0;
            FloatCount = 0;
            Truncated = false;
            Elevation = 0f;
            _openPolyline = false;
        }

        public bool AddCurve(ToolPreviewFlags flags, PreviewPoint a, PreviewPoint b, PreviewPoint c, PreviewPoint d, float width)
        {
            if (!Begin(ToolPreviewShape.Curve, flags, CurveFloats, 0UL, out int o)) return false;
            Put(o, a);
            Put(o + 3, b);
            Put(o + 6, c);
            Put(o + 9, d);
            _data[o + 12] = width;
            return true;
        }

        public bool AddSegment(ToolPreviewFlags flags, PreviewPoint a, PreviewPoint b, float width)
        {
            if (!Begin(ToolPreviewShape.Segment, flags, SegmentFloats, 0UL, out int o)) return false;
            Put(o, a);
            Put(o + 3, b);
            _data[o + 6] = width;
            return true;
        }

        public bool AddCircle(ToolPreviewFlags flags, PreviewPoint center, float diameter)
        {
            if (!Begin(ToolPreviewShape.Circle, flags, CircleFloats, 0UL, out int o)) return false;
            Put(o, center);
            _data[o + 3] = diameter;
            return true;
        }

        public bool AddRect(ToolPreviewFlags flags, PreviewPoint center, float sizeX, float sizeZ, float yaw)
        {
            if (!Begin(ToolPreviewShape.Rect, flags, RectFloats, 0UL, out int o)) return false;
            Put(o, center);
            _data[o + 3] = sizeX;
            _data[o + 4] = sizeZ;
            _data[o + 5] = yaw;
            return true;
        }

        /// <summary>A zone block of <paramref name="cellsX"/> × <paramref name="cellsZ"/> cells (product ≤ <see cref="MaxZoneCells"/>).</summary>
        public bool AddZoneBlock(ToolPreviewFlags flags, PreviewPoint center, float yaw, int cellsX, int cellsZ, ulong cells)
        {
            if (cellsX < 1 || cellsZ < 1 || cellsX * cellsZ > MaxZoneCells) return false;
            if (!Begin(ToolPreviewShape.ZoneBlock, flags, ZoneBlockFloats, cells & ZoneMask(cellsX * cellsZ), out int o)) return false;
            Put(o, center);
            _data[o + 3] = yaw;
            _data[o + 4] = cellsX;
            _data[o + 5] = cellsZ;
            return true;
        }

        /// <summary>The bits of a zone block with <paramref name="cells"/> cells.</summary>
        public static ulong ZoneMask(int cells) => cells >= 64 ? ulong.MaxValue : (1UL << cells) - 1UL;

        /// <summary>Starts a polyline; add its points with <see cref="AddPolylinePoint"/> and finish with <see cref="EndPolyline"/>.</summary>
        public bool BeginPolyline(ToolPreviewFlags flags, float width)
        {
            if (_openPolyline) EndPolyline();
            if (!Begin(ToolPreviewShape.Polyline, flags, 1, 0UL, out int o)) return false;
            _data[o] = width;
            _openPolyline = true;
            return true;
        }

        /// <summary>Adds a point to the open polyline; false when there is none, it is full or the floats ran out.</summary>
        public bool AddPolylinePoint(PreviewPoint p)
        {
            if (!_openPolyline) return false;
            int i = Count - 1;
            if ((_length[i] - 1) / 3 >= MaxPolylinePoints || FloatCount + 3 > MaxFloats)
            {
                Truncated = true;
                return false;
            }
            Put(FloatCount, p);
            FloatCount += 3;
            _length[i] += 3;
            return true;
        }

        /// <summary>Closes the open polyline; one with fewer than 2 points is dropped. False when it was dropped.</summary>
        public bool EndPolyline()
        {
            if (!_openPolyline) return false;
            _openPolyline = false;
            int i = Count - 1;
            if ((_length[i] - 1) / 3 >= 2) return true;
            FloatCount = _start[i];
            Count--;
            return false;
        }

        /// <summary>Replaces this preview with a copy of <paramref name="other"/>.</summary>
        public void CopyFrom(ToolPreview other)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));
            if (ReferenceEquals(other, this)) return;
            int n = other.Count;
            Array.Copy(other._shapes, _shapes, n);
            Array.Copy(other._flags, _flags, n);
            Array.Copy(other._start, _start, n);
            Array.Copy(other._length, _length, n);
            Array.Copy(other._bits, _bits, n);
            Array.Copy(other._data, _data, other.FloatCount);
            Count = n;
            FloatCount = other.FloatCount;
            Truncated = other.Truncated;
            Elevation = other.Elevation;
            _openPolyline = false;
        }

        /// <summary>True when both previews have the same shapes with the same float counts (they can be blended).</summary>
        public bool SameLayout(ToolPreview other)
        {
            if (other == null || other.Count != Count || other.FloatCount != FloatCount) return false;
            for (int i = 0; i < Count; i++)
            {
                if (_shapes[i] != other._shapes[i] || _length[i] != other._length[i]) return false;
            }
            return true;
        }

        /// <summary>Copies the flags, the zone bits and the tool elevation of <paramref name="other"/> (same layout)
        /// without touching the floats.</summary>
        internal void CopyFlagsAndBits(ToolPreview other)
        {
            Array.Copy(other._flags, _flags, Count);
            Array.Copy(other._bits, _bits, Count);
            Truncated = other.Truncated;
            Elevation = other.Elevation;
        }

        private bool Begin(ToolPreviewShape shape, ToolPreviewFlags flags, int floats, ulong bits, out int offset)
        {
            offset = 0;
            if (_openPolyline) EndPolyline();
            if (Count >= MaxShapes || FloatCount + floats > MaxFloats)
            {
                Truncated = true;
                return false;
            }
            offset = FloatCount;
            _shapes[Count] = shape;
            _flags[Count] = flags;
            _start[Count] = offset;
            _length[Count] = floats;
            _bits[Count] = bits;
            Count++;
            FloatCount += floats;
            return true;
        }

        private void Put(int o, PreviewPoint p)
        {
            _data[o] = p.X;
            _data[o + 1] = p.Y;
            _data[o + 2] = p.Z;
        }
    }
}
