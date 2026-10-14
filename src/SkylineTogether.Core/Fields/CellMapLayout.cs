using System;
using System.Collections.Generic;
using SkylineTogether.Core.Hashing;

namespace SkylineTogether.Core.Fields
{
    /// <summary>
    /// Geometry of one cell map as raw bytes (ARCHITECTURE.md#zone-cells-cell-maps-terrain-and-water): <see cref="Width"/> × <see cref="Height"/> cells of
    /// <see cref="CellBytes"/> bytes each, row-major (the layout of <c>CellMapSystem&lt;T&gt;.m_Map</c>), cut into square
    /// tiles of <see cref="TileCells"/> cells for region diffing. Only tiles whose bytes changed travel; a tile is the
    /// unit of hashing, of the per-peer "already sent" state and of the wire. Immutable and thread-safe.
    /// </summary>
    public sealed class CellMapLayout : IEquatable<CellMapLayout>
    {
        /// <summary>Largest width or height in cells.</summary>
        public const int MaxDimension = 4096;

        /// <summary>Largest cell in bytes.</summary>
        public const int MaxCellBytes = 64;

        /// <summary>Smallest and largest tile edge in cells.</summary>
        public const int MinTileCells = 4, MaxTileCells = 64;

        public CellMapLayout(int width, int height, int cellBytes, int tileCells)
        {
            if (width < 1 || width > MaxDimension) throw new ArgumentOutOfRangeException(nameof(width), width, "1 to " + MaxDimension + " cells");
            if (height < 1 || height > MaxDimension) throw new ArgumentOutOfRangeException(nameof(height), height, "1 to " + MaxDimension + " cells");
            if (cellBytes < 1 || cellBytes > MaxCellBytes) throw new ArgumentOutOfRangeException(nameof(cellBytes), cellBytes, "1 to " + MaxCellBytes + " bytes");
            if (tileCells < MinTileCells || tileCells > MaxTileCells) throw new ArgumentOutOfRangeException(nameof(tileCells), tileCells, MinTileCells + " to " + MaxTileCells + " cells");
            Width = width;
            Height = height;
            CellBytes = cellBytes;
            TileCells = tileCells;
            TilesX = (width + tileCells - 1) / tileCells;
            TilesY = (height + tileCells - 1) / tileCells;
        }

        public int Width { get; }

        public int Height { get; }

        public int CellBytes { get; }

        public int TileCells { get; }

        public int TilesX { get; }

        public int TilesY { get; }

        public int TileCount => TilesX * TilesY;

        /// <summary>Bytes of the whole map.</summary>
        public int MapBytes => Width * Height * CellBytes;

        /// <summary>Bytes of the largest tile.</summary>
        public int MaxTileBytes => TileCells * TileCells * CellBytes;

        /// <summary>The cell rectangle of a tile (edge tiles may be smaller).</summary>
        public void GetTileRect(int tile, out int x0, out int y0, out int width, out int height)
        {
            if (tile < 0 || tile >= TileCount) throw new ArgumentOutOfRangeException(nameof(tile), tile, "0 to " + (TileCount - 1));
            x0 = tile % TilesX * TileCells;
            y0 = tile / TilesX * TileCells;
            width = Math.Min(TileCells, Width - x0);
            height = Math.Min(TileCells, Height - y0);
        }

        public int TileBytes(int tile)
        {
            GetTileRect(tile, out _, out _, out int w, out int h);
            return w * h * CellBytes;
        }

        /// <summary>Copies a cell rectangle of <paramref name="map"/> row by row into <paramref name="dst"/>; returns the bytes written.</summary>
        public int ExtractRect(byte[] map, int x0, int y0, int width, int height, byte[] dst, int dstOffset)
        {
            CheckRect(x0, y0, width, height);
            if (map == null || map.Length < MapBytes) throw new ArgumentException("the map is smaller than the layout", nameof(map));
            int row = width * CellBytes;
            if (dst == null || dstOffset < 0 || dstOffset > dst.Length - row * height) throw new ArgumentException("the destination is too small", nameof(dst));
            for (int y = 0; y < height; y++)
            {
                Buffer.BlockCopy(map, ((y0 + y) * Width + x0) * CellBytes, dst, dstOffset + y * row, row);
            }
            return row * height;
        }

        /// <summary>Writes a row-major cell rectangle from <paramref name="src"/> into <paramref name="map"/>.</summary>
        public void WriteRect(byte[] src, int srcOffset, int x0, int y0, int width, int height, byte[] map)
        {
            CheckRect(x0, y0, width, height);
            if (map == null || map.Length < MapBytes) throw new ArgumentException("the map is smaller than the layout", nameof(map));
            int row = width * CellBytes;
            if (src == null || srcOffset < 0 || srcOffset > src.Length - row * height) throw new ArgumentException("the source is too small", nameof(src));
            for (int y = 0; y < height; y++)
            {
                Buffer.BlockCopy(src, srcOffset + y * row, map, ((y0 + y) * Width + x0) * CellBytes, row);
            }
        }

        public int ExtractTile(byte[] map, int tile, byte[] dst)
        {
            GetTileRect(tile, out int x0, out int y0, out int w, out int h);
            return ExtractRect(map, x0, y0, w, h, dst, 0);
        }

        public void WriteTile(byte[] src, int tile, byte[] map)
        {
            GetTileRect(tile, out int x0, out int y0, out int w, out int h);
            WriteRect(src, 0, x0, y0, w, h, map);
        }

        /// <summary>
        /// xxHash64 of every tile of <paramref name="map"/> into <paramref name="hashes"/> (length ≥ <see cref="TileCount"/>);
        /// <paramref name="scratch"/> needs <see cref="MaxTileBytes"/> bytes. Returns the number of tiles whose hash changed.
        /// </summary>
        public int HashTiles(byte[] map, ulong[] hashes, byte[] scratch)
        {
            if (hashes == null || hashes.Length < TileCount) throw new ArgumentException("one hash per tile is needed", nameof(hashes));
            if (scratch == null || scratch.Length < MaxTileBytes) throw new ArgumentException("the scratch buffer is too small", nameof(scratch));
            int changed = 0;
            for (int t = 0; t < TileCount; t++)
            {
                int n = ExtractTile(map, t, scratch);
                // never 0: 0 means "nothing sent yet" in the per-peer state
                ulong h = XxHash64.Hash(scratch, 0, n, 0x9E3779B97F4A7C15UL) | 1UL;
                if (hashes[t] != h) changed++;
                hashes[t] = h;
            }
            return changed;
        }

        /// <summary>Adds the tiles that intersect the cell rectangle [x0, x1] × [y0, y1] (clamped to the map).</summary>
        public int TilesInRect(int x0, int y0, int x1, int y1, ICollection<int> output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            x0 = Math.Max(0, x0);
            y0 = Math.Max(0, y0);
            x1 = Math.Min(Width - 1, x1);
            y1 = Math.Min(Height - 1, y1);
            if (x1 < x0 || y1 < y0) return 0;
            int n = 0;
            for (int ty = y0 / TileCells; ty <= y1 / TileCells; ty++)
            {
                for (int tx = x0 / TileCells; tx <= x1 / TileCells; tx++)
                {
                    output.Add(ty * TilesX + tx);
                    n++;
                }
            }
            return n;
        }

        private void CheckRect(int x0, int y0, int width, int height)
        {
            if (x0 < 0 || y0 < 0 || width < 0 || height < 0 || x0 > Width - width || y0 > Height - height)
            {
                throw new ArgumentOutOfRangeException(nameof(width), "rectangle " + x0 + "," + y0 + " " + width + "x" + height + " outside the " + Width + "x" + Height + " map");
            }
        }

        public bool Equals(CellMapLayout other) => other != null && Width == other.Width && Height == other.Height && CellBytes == other.CellBytes && TileCells == other.TileCells;

        public override bool Equals(object obj) => obj is CellMapLayout o && Equals(o);

        public override int GetHashCode() => unchecked(((Width * 31 + Height) * 31 + CellBytes) * 31 + TileCells);

        public override string ToString() => Width + "x" + Height + "x" + CellBytes + "B/" + TileCells;
    }

    /// <summary>
    /// What one peer holds of one cell map: the hash of every tile last sent to it (0 = never sent). Region diffing:
    /// <see cref="NextChanged"/> walks the tiles round-robin from a cursor, so a budget-limited sender eventually reaches
    /// every changed tile. Not thread-safe.
    /// </summary>
    public sealed class CellMapPeerState
    {
        private ulong[] _sent;
        private int _cursor;

        public CellMapPeerState(int tileCount)
        {
            if (tileCount < 1) throw new ArgumentOutOfRangeException(nameof(tileCount));
            _sent = new ulong[tileCount];
        }

        public int TileCount => _sent.Length;

        /// <summary>Forgets everything sent (the next pass sends every tile).</summary>
        public void Reset(int tileCount)
        {
            if (tileCount < 1) throw new ArgumentOutOfRangeException(nameof(tileCount));
            if (_sent.Length != tileCount) _sent = new ulong[tileCount];
            else Array.Clear(_sent, 0, _sent.Length);
            _cursor = 0;
        }

        /// <summary>Tiles whose current hash differs from what was sent.</summary>
        public int CountChanged(ulong[] current)
        {
            int n = 0;
            int count = Math.Min(current.Length, _sent.Length);
            for (int i = 0; i < count; i++)
            {
                if (current[i] != 0 && current[i] != _sent[i]) n++;
            }
            return n;
        }

        /// <summary>
        /// The next tile (round-robin from the cursor) whose <paramref name="current"/> hash differs from the sent one;
        /// false when the peer is up to date. Hash 0 means "unknown" and is never offered.
        /// </summary>
        public bool NextChanged(ulong[] current, out int tile)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            int count = Math.Min(current.Length, _sent.Length);
            for (int i = 0; i < count; i++)
            {
                int t = (_cursor + i) % count;
                if (current[t] != 0 && current[t] != _sent[t])
                {
                    tile = t;
                    _cursor = (t + 1) % count;
                    return true;
                }
            }
            tile = -1;
            return false;
        }

        public void MarkSent(int tile, ulong hash) => _sent[tile] = hash;

        public ulong SentHash(int tile) => _sent[tile];

        /// <summary>
        /// The digest of what was sent (<see cref="CellMapMessage.Digest"/>); false while some tile was never sent
        /// or <paramref name="current"/> still has tiles to send (a digest is only comparable once the peer is up to date).
        /// </summary>
        public bool TryDigest(ulong[] current, out ulong digest)
        {
            digest = 0;
            if (current == null || current.Length < _sent.Length) return false;
            for (int i = 0; i < _sent.Length; i++)
            {
                if (_sent[i] == 0 || _sent[i] != current[i]) return false;
            }
            digest = CellMapMessage.Digest(_sent, _sent.Length);
            return true;
        }
    }
}
