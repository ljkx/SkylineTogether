using System;
using System.Collections.Generic;
using System.Globalization;

namespace SkylineTogether.Core.Fields
{
    /// <summary>
    /// Splits a texel grid (the 4096² terrain heightmap into 64×64 tiles, the 2048² water texture into 256×256 tiles;
    /// ARCH §4.10.9-4.10.10) into square tiles, numbered row-major from the top-left texel. When the grid size is not a
    /// multiple of the tile size, the tiles of the last column and row are smaller (<see cref="GetRect"/>).
    /// <para>
    /// Texel rectangles are given as <b>inclusive</b> bounds <c>[minX..maxX] × [minY..maxY]</c>, the convention of the
    /// texel rectangle in <c>TerrainSystem.ApplyToTerrain</c> (<c>TerrainSystem.cs:3794</c> [V]) and of
    /// <see cref="StrokeSettleDetector"/>. Immutable and thread-safe.
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public sealed class TileGrid
    {
        /// <summary>Tiles per axis are numbered with a <see cref="ushort"/>.</summary>
        public const int MaxTilesPerAxis = ushort.MaxValue + 1;

        public TileGrid(int width, int height, int tileSize)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "the grid width must be positive");
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "the grid height must be positive");
            if (tileSize <= 0) throw new ArgumentOutOfRangeException(nameof(tileSize), tileSize, "the tile size must be positive");
            long tilesX = ((long)width + tileSize - 1) / tileSize;
            long tilesY = ((long)height + tileSize - 1) / tileSize;
            if (tilesX > MaxTilesPerAxis || tilesY > MaxTilesPerAxis)
            {
                throw new ArgumentOutOfRangeException(nameof(tileSize), tileSize, "more than " + MaxTilesPerAxis + " tiles per axis");
            }
            if (tilesX * tilesY > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(tileSize), tileSize, "more than int.MaxValue tiles");
            }
            Width = width;
            Height = height;
            TileSize = tileSize;
            TilesX = (int)tilesX;
            TilesY = (int)tilesY;
        }

        /// <summary>Grid width in texels.</summary>
        public int Width { get; }

        /// <summary>Grid height in texels.</summary>
        public int Height { get; }

        /// <summary>Edge length of a full tile in texels.</summary>
        public int TileSize { get; }

        /// <summary>Number of tile columns (<c>ceil(Width / TileSize)</c>).</summary>
        public int TilesX { get; }

        /// <summary>Number of tile rows (<c>ceil(Height / TileSize)</c>).</summary>
        public int TilesY { get; }

        /// <summary>Total number of tiles.</summary>
        public int TileCount => TilesX * TilesY;

        /// <summary>True when <paramref name="tile"/> lies inside the grid (decoders validate received ids with it).</summary>
        public bool Contains(TileId tile) => tile.X < TilesX && tile.Y < TilesY;

        /// <summary>Row-major index of <paramref name="tile"/> in [0, <see cref="TileCount"/>) (round-robin schedules).</summary>
        public int IndexOf(TileId tile)
        {
            if (!Contains(tile)) throw new ArgumentOutOfRangeException(nameof(tile), tile.ToString() + " lies outside the grid " + Describe());
            return tile.Y * TilesX + tile.X;
        }

        /// <summary>The tile with row-major index <paramref name="index"/>.</summary>
        public TileId TileAt(int index)
        {
            if (index < 0 || index >= TileCount) throw new ArgumentOutOfRangeException(nameof(index), index, "the grid has " + TileCount + " tiles");
            return new TileId((ushort)(index % TilesX), (ushort)(index / TilesX));
        }

        /// <summary>
        /// The tiles that overlap the inclusive texel rectangle, row by row from the top-left. The rectangle is clipped
        /// to the grid first; an empty rectangle (max below min) or one entirely outside the grid yields no tile.
        /// </summary>
        public IEnumerable<TileId> TilesIn(int minX, int minY, int maxX, int maxY)
        {
            if (minX < 0) minX = 0;
            if (minY < 0) minY = 0;
            if (maxX > Width - 1) maxX = Width - 1;
            if (maxY > Height - 1) maxY = Height - 1;
            if (maxX < minX || maxY < minY) yield break;
            int tx0 = minX / TileSize, tx1 = maxX / TileSize;
            int ty0 = minY / TileSize, ty1 = maxY / TileSize;
            for (int ty = ty0; ty <= ty1; ty++)
            {
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    yield return new TileId((ushort)tx, (ushort)ty);
                }
            }
        }

        /// <summary>
        /// The texel rectangle of <paramref name="tile"/>: origin (<paramref name="x"/>, <paramref name="y"/>) and size
        /// <paramref name="w"/> × <paramref name="h"/>; tiles of the last column or row are clipped to the grid.
        /// </summary>
        public void GetRect(TileId tile, out int x, out int y, out int w, out int h)
        {
            if (!Contains(tile)) throw new ArgumentOutOfRangeException(nameof(tile), tile.ToString() + " lies outside the grid " + Describe());
            x = tile.X * TileSize;
            y = tile.Y * TileSize;
            w = Math.Min(TileSize, Width - x);
            h = Math.Min(TileSize, Height - y);
        }

        /// <summary>
        /// The texels a terrain brush stamp writes, computed exactly like <c>TerrainSystem.ApplyToTerrain</c>
        /// (<c>TerrainSystem.cs:3790-3814</c> [V]): the world-space bounds (from <c>ToolUtils.GetBounds(Brush)</c>) are
        /// moved into texture space with the playable offset and size, floored/ceiled to texels and clipped to the
        /// texture, then grown by <paramref name="margin"/> texels on each side (the game uses 2, which also covers the
        /// soften blur) and clipped again. The float arithmetic, including the NaN behaviour of
        /// <c>Unity.Mathematics.math.min/max</c>, mirrors the game, so host and clients derive the same dirty rectangle
        /// for <see cref="StrokeSettleDetector.OnStamp"/>. Returns false when the game would write nothing (the stamp
        /// lies outside the texture).
        /// </summary>
        public static bool WorldToTexelRect(float worldMinX, float worldMinZ, float worldMaxX, float worldMaxZ,
                                            float originX, float originZ, float sizeX, float sizeZ, int width, int height, int margin,
                                            out int texelMinX, out int texelMinY, out int texelMaxX, out int texelMaxY)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "the texture width must be positive");
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "the texture height must be positive");
            if (margin < 0) throw new ArgumentOutOfRangeException(nameof(margin), margin, "the margin must not be negative");
            texelMinX = texelMinY = 0;
            texelMaxX = texelMaxY = -1;
            float nMinX = (worldMinX - originX) / sizeX;
            float nMinZ = (worldMinZ - originZ) / sizeZ;
            float nMaxX = (worldMaxX - originX) / sizeX;
            float nMaxZ = (worldMaxZ - originZ) / sizeZ;
            int x0 = ToInt(UnityMax(Floor(nMinX * width), 0f));
            int y0 = ToInt(UnityMax(Floor(nMinZ * height), 0f));
            int x1 = ToInt(UnityMin(Ceil(nMaxX * width), width - 1));
            int y1 = ToInt(UnityMin(Ceil(nMaxZ * height), height - 1));
            long w = (long)x1 - x0 + 1;
            long h = (long)y1 - y0 + 1;
            long rx = Math.Max((long)x0 - margin, 0);
            long ry = Math.Max((long)y0 - margin, 0);
            long rw = w + 2L * margin;
            long rh = h + 2L * margin;
            if (rx + rw < 0 || rx > width || ry + rh < 0 || ry > height || w <= 0 || h <= 0) return false;
            if (rx + rw > width) rw = width - rx;
            if (ry + rh > height) rh = height - ry;
            if (rw <= 0 || rh <= 0) return false;
            texelMinX = (int)rx;
            texelMinY = (int)ry;
            texelMaxX = (int)(rx + rw - 1);
            texelMaxY = (int)(ry + rh - 1);
            return true;
        }

        public override string ToString() => Describe();

        private string Describe()
        {
            return Width.ToString(CultureInfo.InvariantCulture) + "x" + Height.ToString(CultureInfo.InvariantCulture) + " texels in "
                   + TilesX.ToString(CultureInfo.InvariantCulture) + "x" + TilesY.ToString(CultureInfo.InvariantCulture) + " tiles of "
                   + TileSize.ToString(CultureInfo.InvariantCulture);
        }

        // Unity.Mathematics semantics (verified in the IL of Unity.Mathematics.dll): floor/ceil go through double,
        // min(x, y) = IsNaN(y) || x < y ? x : y, max(x, y) = IsNaN(y) || x > y ? x : y.
        private static float Floor(float x) => (float)Math.Floor(x);

        private static float Ceil(float x) => (float)Math.Ceiling(x);

        private static float UnityMin(float x, float y) => float.IsNaN(y) || x < y ? x : y;

        private static float UnityMax(float x, float y) => float.IsNaN(y) || x > y ? x : y;

        /// <summary>(int) truncation like the game, with out-of-range values clamped instead of undefined.</summary>
        private static int ToInt(float value)
        {
            if (float.IsNaN(value)) return 0;
            if (value >= 1073741824f) return 1 << 30;
            if (value <= -1073741824f) return -(1 << 30);
            return (int)value;
        }
    }
}
