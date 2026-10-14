using System;
using System.Globalization;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Fields;

namespace SkylineTogether.Core.Sync
{
    /// <summary>
    /// Wire format of <see cref="MessageKind.Water"/> (ARCH §4.10.10): host → client correction tiles of
    /// <c>WaterSystem.WaterTexture</c> plus the host's sea level.
    /// <code>
    /// u8 version | u8 type (1 = tiles) | f32 seaLevel | grid (see TerrainMessage) | u32 n |
    /// n × (u16 x, u16 y, WaterTileCodec encoding)
    /// </code>
    /// Tiles are absolute (quantised) water state; a later tile always wins.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class WaterMessage
    {
        public const byte Version = 1;

        public const byte TypeTiles = 1;

        /// <summary>Tiles in one message (a 256² tile with velocity is 576 KiB raw).</summary>
        public const int MaxTiles = 12;

        private const string What = "water message";

        /// <summary>Starts a tile message; returns the position of the tile count for <see cref="EndTiles"/>.</summary>
        public static int BeginTiles(ByteWriter w, float seaLevel, TileGridInfo grid)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            w.WriteByte(Version);
            w.WriteByte(TypeTiles);
            w.WriteSingle(seaLevel);
            TerrainMessage.WriteGrid(w, grid);
            return w.Reserve(4);
        }

        /// <summary>Appends one tile: <c>rgba</c> holds 4 floats per texel, row-major over the tile's rectangle.</summary>
        public static void AddTile(ByteWriter w, TileId tile, float[] rgba, int texelCount, WaterTileLevel level)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            w.WriteUInt16(tile.X);
            w.WriteUInt16(tile.Y);
            WaterTileCodec.Encode(rgba, texelCount, level, w, null);
        }

        public static void EndTiles(ByteWriter w, int countPosition, int count)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            if (count < 0 || count > MaxTiles) throw new ArgumentOutOfRangeException(nameof(count), count, "0 to " + MaxTiles + " tiles");
            w.PatchUInt32(countPosition, (uint)count);
        }

        /// <summary>Reads the header; returns the tile count.</summary>
        public static int ReadHeader(ByteReader r, out float seaLevel, out TileGridInfo grid)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            byte version = r.ReadByte();
            if (version != Version) throw new DecodeException(What + " version " + version.ToString(CultureInfo.InvariantCulture) + " (expected " + Version + ")");
            byte type = r.ReadByte();
            if (type != TypeTiles) throw new DecodeException(What + ": unknown type " + type.ToString(CultureInfo.InvariantCulture));
            seaLevel = r.ReadSingle();
            if (float.IsNaN(seaLevel) || float.IsInfinity(seaLevel)) throw new DecodeException(What + ": sea level is not finite");
            grid = TerrainMessage.ReadGrid(r);
            uint n = r.ReadUInt32();
            if (n > MaxTiles) throw new DecodeException(What + " with " + n + " tiles");
            return (int)n;
        }

        /// <summary>Reads the next tile into <paramref name="rgba"/>; returns its texel count.</summary>
        public static int ReadTile(ByteReader r, TileGrid grid, float[] rgba, out TileId tile)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            if (grid == null) throw new ArgumentNullException(nameof(grid));
            tile = TerrainMessage.ReadTileId(r, grid);
            grid.GetRect(tile, out _, out _, out int w, out int h);
            long texels = (long)w * h; // no int overflow for tiles near the edge limit
            if (texels <= 0 || texels > int.MaxValue / WaterTileCodec.ChannelsPerTexel) throw new DecodeException("water tile of " + w + "x" + h + " texels");
            int count = (int)texels;
            if (rgba == null || rgba.Length < (long)count * WaterTileCodec.ChannelsPerTexel) throw new ArgumentException("the rgba buffer is too small", nameof(rgba));
            WaterTileCodec.Decode(r, rgba, count, null);
            return count;
        }
    }
}
