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
    /// type 3 (digest, host → client): u64 digest of the tiles the host sent this peer (w9/perf, see Digest)
    /// type 4 (resync, client → host): nothing more, "send me this whole map again" (w9/perf)
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

        /// <summary>w9/perf: host → client, the digest of the tiles sent to that peer (<see cref="WriteDigest"/>).</summary>
        public const byte TypeDigest = 3;

        /// <summary>w9/perf: client → host, a request to send one whole map again (its digest differed).</summary>
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

        // ------------------------------------------------------------------ digests and resync requests (w9/perf)

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
        /// w9/perf (trust): clamps the 16-bit little-endian fields of every cell of a client's patch to their valid range
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
        /// w9/perf (trust): <see cref="ClampFields"/> for a patch about to be merged into <paramref name="map"/>, limited
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
