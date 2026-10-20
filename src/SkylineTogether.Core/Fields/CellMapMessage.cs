using System;
using System.Globalization;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Fields
{
    /// <summary>
    /// Wire format of <see cref="MessageKind.CellMaps"/> (ARCHITECTURE.md#zone-cells-cell-maps-terrain-and-water):
    /// <code>
    /// header = u8 version | u8 type | u32 mapId | u16 width | u16 height | u8 cellBytes | u8 tileCells
    /// type 1 (tiles, host → client):  varuint n | n × (varuint tile | body)
    /// type 2 (patch, client → host):  u16 x0 | u16 y0 | u16 w | u16 h | body
    /// type 3 (digest, host → client): u64 digest of the tiles the host sent this peer (see Digest)
    /// type 4 (resync, client → host): nothing more, "send me this whole map again"
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

        /// <summary>Host → client, the digest of the tiles sent to that peer (<see cref="WriteDigest"/>).</summary>
        public const byte TypeDigest = 3;

        /// <summary>Client → host, a request to send one whole map again (its digest differed).</summary>
        public const byte TypeResync = 4;

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
            if (h.Type < TypeTiles || h.Type > TypeResync) throw new DecodeException(What + ": unknown type " + h.Type.ToString(CultureInfo.InvariantCulture));
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

        // ------------------------------------------------------------------ digests and resync requests

        /// <summary>
        /// The digest of a whole map from its tile hashes (<see cref="CellMapLayout.HashTiles"/>, or the hashes a host sent
        /// one peer, <see cref="CellMapPeerState.TryDigest"/>): order-dependent, so equal digests mean equal tiles.
        /// </summary>
        public static ulong Digest(ulong[] tileHashes, int count)
        {
            if (tileHashes == null) throw new ArgumentNullException(nameof(tileHashes));
            if (count < 0 || count > tileHashes.Length) throw new ArgumentOutOfRangeException(nameof(count));
            ulong h = 0x243F6A8885A308D3UL ^ (ulong)count;
            for (int i = 0; i < count; i++) h = unchecked(Hashing.HashMix.Mix64(h ^ tileHashes[i]) + (ulong)i);
            return h;
        }

        /// <summary>Host → client: the digest of the tiles sent to the peer, so it can ask for a resync when its map differs.</summary>
        public static void WriteDigest(ByteWriter w, uint mapId, CellMapLayout layout, ulong digest)
        {
            WriteHeader(w, TypeDigest, mapId, layout);
            w.WriteUInt64(digest);
        }

        /// <summary>Reads a digest after <see cref="ReadHeader"/>.</summary>
        public static ulong ReadDigest(ByteReader r)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            ulong digest = r.ReadUInt64();
            if (!r.IsAtEnd) throw new DecodeException(What + ": digest with " + r.Remaining + " trailing bytes");
            return digest;
        }

        /// <summary>Client → host: send this whole map again.</summary>
        public static void WriteResync(ByteWriter w, uint mapId, CellMapLayout layout) => WriteHeader(w, TypeResync, mapId, layout);

        // ------------------------------------------------------------------ patches

        /// <summary>The bytes of a patch's painted-cell mask: one bit per cell of the rectangle, row-major.</summary>
        public static int PaintedMaskBytes(int width, int height) => (width * height + 7) / 8;

        /// <summary>Whether cell <paramref name="index"/> (row-major in the patch rectangle) is marked in a painted mask (null: all are).</summary>
        public static bool IsPainted(byte[] painted, int index) => painted == null || (painted[index >> 3] & (1 << (index & 7))) != 0;

        /// <summary>
        /// A patch: the cell rectangle [x0, x0 + w) × [y0, y0 + h) of the map, raw cells row-major, then the mask of the
        /// cells the player painted (<paramref name="painted"/>, <see cref="PaintedMaskBytes"/>; null: every cell). Only
        /// painted cells are merged: the other cells of the rectangle carry the client's possibly older copy of the
        /// host's map.
        /// </summary>
        public static void WritePatch(ByteWriter w, uint mapId, CellMapLayout layout, int x0, int y0, int width, int height, byte[] map, byte[] scratch, byte[] painted = null)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            if (width < 1 || height < 1 || width > MaxPatchCells || height > MaxPatchCells) throw new ArgumentOutOfRangeException(nameof(width), "1 to " + MaxPatchCells + " cells per edge");
            int maskBytes = PaintedMaskBytes(width, height);
            if (painted != null && painted.Length < maskBytes) throw new ArgumentException("the painted mask is too small", nameof(painted));
            int n = layout.ExtractRect(map, x0, y0, width, height, scratch, 0);
            WriteHeader(w, TypePatch, mapId, layout);
            w.WriteUInt16((ushort)x0);
            w.WriteUInt16((ushort)y0);
            w.WriteUInt16((ushort)width);
            w.WriteUInt16((ushort)height);
            TileBodyCodec.Write(scratch, n, null, w, null, What);
            for (int i = 0; i < maskBytes; i++)
            {
                int bits = painted != null ? painted[i] : 0xFF;
                int last = width * height - i * 8;
                if (last < 8) bits &= (1 << last) - 1; // no bits beyond the rectangle
                w.WriteByte((byte)bits);
            }
        }

        /// <summary>Reads a patch after <see cref="ReadHeader"/>, dropping its painted mask (see the overload).</summary>
        public static void ReadPatch(ByteReader r, CellMapLayout layout, out int x0, out int y0, out int width, out int height, ref byte[] cells)
        {
            byte[] painted = null;
            ReadPatch(r, layout, out x0, out y0, out width, out height, ref cells, ref painted);
        }

        /// <summary>
        /// Reads a patch after <see cref="ReadHeader"/>; the raw cells go to <paramref name="cells"/> and the painted mask to
        /// <paramref name="painted"/> (both resized when small). Bits beyond the rectangle are malformed.
        /// </summary>
        public static void ReadPatch(ByteReader r, CellMapLayout layout, out int x0, out int y0, out int width, out int height, ref byte[] cells, ref byte[] painted)
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
            int maskBytes = PaintedMaskBytes(width, height);
            if (painted == null || painted.Length < maskBytes) painted = new byte[maskBytes];
            r.ReadBytes(painted, 0, maskBytes);
            int last = width * height - (maskBytes - 1) * 8;
            if (last < 8 && (painted[maskBytes - 1] >> last) != 0) throw new DecodeException(What + ": painted mask bits beyond the patch");
        }

        /// <summary>
        /// Client: marks in <paramref name="painted"/> (<see cref="PaintedMaskBytes"/>) the cells of the rectangle the player
        /// painted: cells <paramref name="touched"/> by a local stamp (one flag per map cell) whose painted fields
        /// (<paramref name="fieldMask"/>) differ from <paramref name="baseline"/>, the copy taken before the first stamp touched
        /// them. Returns the number of painted cells.
        /// </summary>
        public static int MarkPainted(CellMapLayout layout, byte[] map, byte[] baseline, bool[] touched, byte[] fieldMask, int x0, int y0, int width, int height, byte[] painted)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            if (map == null || baseline == null || map.Length < layout.MapBytes || baseline.Length < layout.MapBytes) throw new ArgumentException("the map is smaller than the layout", nameof(map));
            if (touched == null || touched.Length < layout.Width * layout.Height) throw new ArgumentException("one flag per cell", nameof(touched));
            if (fieldMask == null || fieldMask.Length != layout.CellBytes) throw new ArgumentException("one mask entry per cell byte", nameof(fieldMask));
            if (x0 < 0 || y0 < 0 || width < 1 || height < 1 || x0 > layout.Width - width || y0 > layout.Height - height) throw new ArgumentOutOfRangeException(nameof(width));
            int maskBytes = PaintedMaskBytes(width, height);
            if (painted == null || painted.Length < maskBytes) throw new ArgumentException("the painted mask is too small", nameof(painted));
            Array.Clear(painted, 0, maskBytes);
            int cellBytes = layout.CellBytes, count = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int cell = (y0 + y) * layout.Width + x0 + x;
                    if (!touched[cell]) continue;
                    int at = cell * cellBytes;
                    bool differs = false;
                    for (int b = 0; b < cellBytes && !differs; b++) differs = fieldMask[b] != 0 && map[at + b] != baseline[at + b];
                    if (!differs) continue;
                    int index = y * width + x;
                    painted[index >> 3] |= (byte)(1 << (index & 7));
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Keeps three signed 16-bit fields of every cell of a rectangle in order, <c>low &lt;= mid &lt;= high</c> (groundwater:
        /// polluted, amount, maximum, an invariant the game's groundwater simulation relies on): a mid above high raises
        /// high, a low above mid lowers low. Returns the number of cells fixed.
        /// </summary>
        public static int EnforceOrder(CellMapLayout layout, byte[] map, int x0, int y0, int width, int height, int lowOffset, int midOffset, int highOffset)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            if (map == null || map.Length < layout.MapBytes) throw new ArgumentException("the map is smaller than the layout", nameof(map));
            if (x0 < 0 || y0 < 0 || width < 0 || height < 0 || x0 > layout.Width - width || y0 > layout.Height - height) throw new ArgumentOutOfRangeException(nameof(width));
            int cellBytes = layout.CellBytes;
            foreach (int o in new[] { lowOffset, midOffset, highOffset })
            {
                if (o < 0 || o + 2 > cellBytes) throw new ArgumentOutOfRangeException(nameof(lowOffset));
            }
            int fixedCells = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int at = ((y0 + y) * layout.Width + x0 + x) * cellBytes;
                    short low = Short(map, at + lowOffset), mid = Short(map, at + midOffset), high = Short(map, at + highOffset);
                    bool changed = false;
                    if (mid > high)
                    {
                        PutShort(map, at + highOffset, mid);
                        changed = true;
                    }
                    if (low > mid)
                    {
                        PutShort(map, at + lowOffset, mid);
                        changed = true;
                    }
                    if (changed) fixedCells++;
                }
            }
            return fixedCells;
        }

        private static short Short(byte[] b, int at) => (short)(b[at] | (b[at + 1] << 8));

        private static void PutShort(byte[] b, int at, short v)
        {
            b[at] = (byte)v;
            b[at + 1] = (byte)(v >> 8);
        }

        /// <summary>
        /// Clamps the 16-bit little-endian fields of every cell of a client's patch to their valid range
        /// before the host merges it (<see cref="MergeMasked"/>), so a painted resource or groundwater value outside what
        /// the game's brushes can produce never reaches the host's map. Returns the number of values changed.
        /// </summary>
        public static int ClampFields(byte[] cells, int cellCount, int cellBytes, CellFieldClamp[] rules)
        {
            if (cells == null) throw new ArgumentNullException(nameof(cells));
            if (cellBytes < 1 || cellCount < 0 || (long)cellCount * cellBytes > cells.Length) throw new ArgumentOutOfRangeException(nameof(cellCount));
            if (rules == null || rules.Length == 0) return 0;
            foreach (CellFieldClamp rule in rules)
            {
                if (rule.Offset < 0 || rule.Offset + 2 > cellBytes || rule.Min > rule.Max) throw new ArgumentException("a clamp rule outside the cell or with min > max", nameof(rules));
            }
            int clamped = 0;
            for (int c = 0; c < cellCount; c++)
            {
                foreach (CellFieldClamp rule in rules)
                {
                    if (ClampField(cells, c * cellBytes + rule.Offset, rule)) clamped++;
                }
            }
            return clamped;
        }

        /// <summary>
        /// <see cref="ClampFields"/> for a patch about to be merged into <paramref name="map"/>, limited
        /// to the fields the patch changes: a cell of the patch rectangle the player did not paint carries the host's own
        /// value back, which stays as it is even when it lies outside the brush range (map-generated values).
        /// </summary>
        public static int ClampPatch(CellMapLayout layout, byte[] map, int x0, int y0, int width, int height, byte[] patch, CellFieldClamp[] rules)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            if (map == null || map.Length < layout.MapBytes) throw new ArgumentException("the map is smaller than the layout", nameof(map));
            if (x0 < 0 || y0 < 0 || width < 0 || height < 0 || x0 > layout.Width - width || y0 > layout.Height - height) throw new ArgumentOutOfRangeException(nameof(width));
            int cellBytes = layout.CellBytes;
            if (patch == null || patch.Length < width * height * cellBytes) throw new ArgumentException("the patch is too small", nameof(patch));
            if (rules == null || rules.Length == 0) return 0;
            foreach (CellFieldClamp rule in rules)
            {
                if (rule.Offset < 0 || rule.Offset + 2 > cellBytes || rule.Min > rule.Max) throw new ArgumentException("a clamp rule outside the cell or with min > max", nameof(rules));
            }
            int clamped = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int src = (y * width + x) * cellBytes;
                    int dst = ((y0 + y) * layout.Width + x0 + x) * cellBytes;
                    foreach (CellFieldClamp rule in rules)
                    {
                        int a = src + rule.Offset, b = dst + rule.Offset;
                        if (patch[a] == map[b] && patch[a + 1] == map[b + 1]) continue; // unchanged: the host's value
                        if (ClampField(patch, a, rule)) clamped++;
                    }
                }
            }
            return clamped;
        }

        private static bool ClampField(byte[] cells, int at, CellFieldClamp rule)
        {
            int raw = cells[at] | (cells[at + 1] << 8);
            int v = rule.Signed ? (short)raw : raw;
            int fixedValue = v < rule.Min ? rule.Min : v > rule.Max ? rule.Max : v;
            if (fixedValue == v) return false;
            cells[at] = (byte)fixedValue;
            cells[at + 1] = (byte)(fixedValue >> 8);
            return true;
        }

        /// <summary>
        /// Merges a patch into the map: for every cell of the rectangle, only the bytes whose <paramref name="fieldMask"/>
        /// entry is non-zero are taken from the patch (the painted fields); the others keep the map's value (for example
        /// the host's consumed amounts). <paramref name="fieldMask"/> has one entry per cell byte. Returns the number of
        /// cells that changed.
        /// </summary>
        /// <remarks>Only the cells marked in <paramref name="painted"/> are merged (null: every cell).</remarks>
        public static int MergeMasked(CellMapLayout layout, byte[] map, int x0, int y0, int width, int height, byte[] patch, byte[] fieldMask, byte[] painted = null)
        {
            if (painted != null && painted.Length < PaintedMaskBytes(width, height)) throw new ArgumentException("the painted mask is too small", nameof(painted));
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
                    if (!IsPainted(painted, y * width + x)) continue;
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
