using System;
using System.Globalization;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Fields
{
    /// <summary>
    /// Wire format of <see cref="MessageKind.CellMaps"/> (ARCH §4.10.8):
    /// <code>
    /// header = u8 version | u8 type | u32 mapId | u16 width | u16 height | u8 cellBytes | u8 tileCells
    /// type 1 (tiles, host → client):  varuint n | n × (varuint tile | body)
    /// type 2 (patch, client → host):  u16 x0 | u16 y0 | u16 w | u16 h | body
    /// body   = the shared tile body framing (zero-run coded raw cells, optionally block-compressed; the message
    ///          envelope compresses again), exactly w × h × cellBytes raw bytes
    /// </code>
    /// The map id is a stable hash of the map's name (<see cref="MapId"/>); a receiver whose map has another layout
    /// skips the message. Tiles are absolute cell bytes, so a later tile always wins. A patch carries the cells a
    /// player painted with a resource brush (ore, oil, fertility, groundwater); the host merges only the painted fields
    /// (<see cref="MergeMasked"/>).
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class CellMapMessage
    {
        public const byte Version = 1;
        public const byte TypeTiles = 1;
        public const byte TypePatch = 2;

        /// <summary>Largest patch edge in cells (a 1000 m brush covers about 20 cells of a 256² map).</summary>
        public const int MaxPatchCells = 256;

        private const string What = "cell-map message";

        /// <summary>The stable id of a map name (FNV-1a, 32 bit; never 0).</summary>
        public static uint MapId(string name)
        {
            uint h = 2166136261u;
            if (name != null)
            {
                foreach (char c in name)
                {
                    h ^= (byte)c;
                    h *= 16777619u;
                    h ^= (byte)(c >> 8);
                    h *= 16777619u;
                }
            }
            return h == 0 ? 1u : h;
        }

        /// <summary>The fixed header of every message.</summary>
        public struct Header
        {
            public byte Type;
            public uint MapId;
            public int Width, Height, CellBytes, TileCells;

            /// <summary>True when the header describes <paramref name="layout"/>.</summary>
            public bool Matches(CellMapLayout layout) => layout != null && Width == layout.Width && Height == layout.Height && CellBytes == layout.CellBytes && TileCells == layout.TileCells;
        }

        private static void WriteHeader(ByteWriter w, byte type, uint mapId, CellMapLayout layout)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            w.WriteByte(Version);
            w.WriteByte(type);
            w.WriteUInt32(mapId);
            w.WriteUInt16((ushort)layout.Width);
            w.WriteUInt16((ushort)layout.Height);
            w.WriteByte((byte)layout.CellBytes);
            w.WriteByte((byte)layout.TileCells);
        }

        /// <summary>Reads and validates the header.</summary>
        public static Header ReadHeader(ByteReader r)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            byte version = r.ReadByte();
            if (version != Version) throw new DecodeException(What + " version " + version.ToString(CultureInfo.InvariantCulture) + " (expected " + Version + ")");
            var h = new Header
            {
                Type = r.ReadByte(),
                MapId = r.ReadUInt32(),
                Width = r.ReadUInt16(),
                Height = r.ReadUInt16(),
                CellBytes = r.ReadByte(),
                TileCells = r.ReadByte(),
            };
            if (h.Type != TypeTiles && h.Type != TypePatch) throw new DecodeException(What + ": unknown type " + h.Type.ToString(CultureInfo.InvariantCulture));
            if (h.Width < 1 || h.Width > CellMapLayout.MaxDimension || h.Height < 1 || h.Height > CellMapLayout.MaxDimension
                || h.CellBytes < 1 || h.CellBytes > CellMapLayout.MaxCellBytes || h.TileCells < CellMapLayout.MinTileCells || h.TileCells > CellMapLayout.MaxTileCells)
            {
                throw new DecodeException(What + ": invalid layout " + h.Width + "x" + h.Height + "x" + h.CellBytes + "/" + h.TileCells);
            }
            return h;
        }

        // ------------------------------------------------------------------ tiles

        /// <summary>Starts a tile message; returns the position of the tile count (patched by <see cref="EndTiles"/>).</summary>
        public static int BeginTiles(ByteWriter w, uint mapId, CellMapLayout layout)
        {
            WriteHeader(w, TypeTiles, mapId, layout);
            return w.Reserve(4);
        }

        /// <summary>Appends one tile: the tile's cells from <paramref name="map"/> (the whole map's raw bytes).</summary>
        public static int AddTile(ByteWriter w, CellMapLayout layout, int tile, byte[] map, byte[] scratch)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            int start = w.Length;
            int n = layout.ExtractTile(map, tile, scratch);
            w.WriteVarUInt32((uint)tile);
            TileBodyCodec.Write(scratch, n, null, w, null, What);
            return w.Length - start;
        }

        public static void EndTiles(ByteWriter w, int countPosition, int tiles)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            if (tiles < 0) throw new ArgumentOutOfRangeException(nameof(tiles));
            w.PatchUInt32(countPosition, (uint)tiles);
        }

        /// <summary>Reads the tile count after <see cref="ReadHeader"/>.</summary>
        public static int ReadTileCount(ByteReader r, CellMapLayout layout)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            uint n = r.ReadUInt32();
            if (n > (uint)layout.TileCount) throw new DecodeException(What + " with " + n + " tiles (the map has " + layout.TileCount + ")");
            return (int)n;
        }

        /// <summary>Reads the next tile's raw cells into <paramref name="tileBytes"/> (≥ <see cref="CellMapLayout.MaxTileBytes"/>); returns the tile index.</summary>
        public static int ReadTile(ByteReader r, CellMapLayout layout, byte[] tileBytes)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            uint tile = r.ReadVarUInt32();
            if (tile >= (uint)layout.TileCount) throw new DecodeException(What + ": tile " + tile + " outside the map");
            int n = layout.TileBytes((int)tile);
            if (tileBytes == null || tileBytes.Length < n) throw new ArgumentException("the tile buffer is too small", nameof(tileBytes));
            TileBodyCodec.Read(r, n, null, tileBytes, What);
            return (int)tile;
        }

        // ------------------------------------------------------------------ patches

        /// <summary>A patch: the cell rectangle [x0, x0 + w) × [y0, y0 + h) of the map, raw cells row-major.</summary>
        public static void WritePatch(ByteWriter w, uint mapId, CellMapLayout layout, int x0, int y0, int width, int height, byte[] map, byte[] scratch)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            if (width < 1 || height < 1 || width > MaxPatchCells || height > MaxPatchCells) throw new ArgumentOutOfRangeException(nameof(width), "1 to " + MaxPatchCells + " cells per edge");
            int n = layout.ExtractRect(map, x0, y0, width, height, scratch, 0);
            WriteHeader(w, TypePatch, mapId, layout);
            w.WriteUInt16((ushort)x0);
            w.WriteUInt16((ushort)y0);
            w.WriteUInt16((ushort)width);
            w.WriteUInt16((ushort)height);
            TileBodyCodec.Write(scratch, n, null, w, null, What);
        }

        /// <summary>Reads a patch after <see cref="ReadHeader"/>; the raw cells go to <paramref name="cells"/> (resized when small).</summary>
        public static void ReadPatch(ByteReader r, CellMapLayout layout, out int x0, out int y0, out int width, out int height, ref byte[] cells)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            x0 = r.ReadUInt16();
            y0 = r.ReadUInt16();
            width = r.ReadUInt16();
            height = r.ReadUInt16();
            if (width < 1 || height < 1 || width > MaxPatchCells || height > MaxPatchCells || x0 > layout.Width - width || y0 > layout.Height - height)
            {
                throw new DecodeException(What + ": patch " + x0 + "," + y0 + " " + width + "x" + height + " outside the " + layout.Width + "x" + layout.Height + " map");
            }
            int n = width * height * layout.CellBytes;
            if (cells == null || cells.Length < n) cells = new byte[n];
            TileBodyCodec.Read(r, n, null, cells, What);
        }

        /// <summary>
        /// Merges a patch into the map: for every cell of the rectangle, only the bytes whose <paramref name="fieldMask"/>
        /// entry is non-zero are taken from the patch (the painted fields); the others keep the map's value (for example
        /// the host's consumed amounts). <paramref name="fieldMask"/> has one entry per cell byte. Returns the number of
        /// cells that changed.
        /// </summary>
        public static int MergeMasked(CellMapLayout layout, byte[] map, int x0, int y0, int width, int height, byte[] patch, byte[] fieldMask)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            if (map == null || map.Length < layout.MapBytes) throw new ArgumentException("the map is smaller than the layout", nameof(map));
            if (fieldMask == null || fieldMask.Length != layout.CellBytes) throw new ArgumentException("one mask entry per cell byte", nameof(fieldMask));
            if (x0 < 0 || y0 < 0 || width < 0 || height < 0 || x0 > layout.Width - width || y0 > layout.Height - height) throw new ArgumentOutOfRangeException(nameof(width));
            int cellBytes = layout.CellBytes;
            if (patch == null || patch.Length < width * height * cellBytes) throw new ArgumentException("the patch is too small", nameof(patch));
            int changed = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int src = (y * width + x) * cellBytes;
                    int dst = ((y0 + y) * layout.Width + x0 + x) * cellBytes;
                    bool any = false;
                    for (int b = 0; b < cellBytes; b++)
                    {
                        if (fieldMask[b] == 0 || map[dst + b] == patch[src + b]) continue;
                        map[dst + b] = patch[src + b];
                        any = true;
                    }
                    if (any) changed++;
                }
            }
            return changed;
        }
    }
}
