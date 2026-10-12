using System;
using System.Collections.Generic;
using System.Globalization;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Fields;

namespace SkylineTogether.Core.Sync
{
    /// <summary>What a <see cref="MessageKind.Terrain"/> message carries (its second byte).</summary>
    public enum TerrainMessageType : byte
    {
        /// <summary>Brush stamps (client → host: a predicted stroke; host → clients: stamps for immediate visuals).</summary>
        Stamps = 1,
        /// <summary>Host → client: authoritative heightmap tiles (absolute texels, bit-exact).</summary>
        Tiles = 2,
        /// <summary>Host → client: tile hashes of a heightmap region (rotating audit).</summary>
        Manifest = 3,
        /// <summary>Client → host: tiles whose hash differed from the manifest.</summary>
        TileRequest = 4,
    }

    /// <summary>
    /// One applied terrain brush stamp (<c>Game.Tools.Brush</c>), as captured on the originator right before
    /// <c>ApplyBrushesSystem</c> consumes it. Prefabs travel as net ids (both sides bind every prefab at session start
    /// and join). <see cref="DeltaTime"/> is the originator's clamped frame time
    /// (<see cref="BrushStrengthCompensation.ClampDelta"/>), so a replaying machine can compensate the strength.
    /// </summary>
    public struct TerrainStamp
    {
        public uint ToolPrefab;
        public uint BrushPrefab;
        public float PositionX, PositionY, PositionZ;
        public float TargetX, TargetY, TargetZ;
        public float StartX, StartY, StartZ;
        public float Angle, Size, Strength, Opacity;
        public float DeltaTime;

        /// <summary>True when every float is finite and the size is positive and plausible (validation of received stamps).</summary>
        public bool IsPlausible(float maxSize)
        {
            return Finite(PositionX) && Finite(PositionY) && Finite(PositionZ) && Finite(TargetX) && Finite(TargetY) && Finite(TargetZ)
                   && Finite(StartX) && Finite(StartY) && Finite(StartZ) && Finite(Angle) && Finite(Strength) && Finite(Opacity)
                   && Finite(DeltaTime) && Size > 0f && Size <= maxSize && ToolPrefab != 0 && BrushPrefab != 0;
        }

        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }

    /// <summary>The grid a tile message refers to (texel size of the texture and tile edge length).</summary>
    public struct TileGridInfo : IEquatable<TileGridInfo>
    {
        public int Width, Height, TileSize;

        public TileGridInfo(int width, int height, int tileSize)
        {
            Width = width;
            Height = height;
            TileSize = tileSize;
        }

        public bool Equals(TileGridInfo o) => Width == o.Width && Height == o.Height && TileSize == o.TileSize;

        public override bool Equals(object obj) => obj is TileGridInfo o && Equals(o);

        public override int GetHashCode() => unchecked(Width * 397 ^ Height * 31 ^ TileSize);

        public override string ToString() => Width.ToString(CultureInfo.InvariantCulture) + "x" + Height.ToString(CultureInfo.InvariantCulture) + "/" + TileSize.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Wire format of <see cref="MessageKind.Terrain"/> (ARCH §4.10.9). Every message starts with
    /// <c>u8 version | u8 type</c>; the rest depends on <see cref="TerrainMessageType"/>:
    /// <code>
    /// Stamps      u8 origin peer | varuint n | n × (varuint tool, varuint brush, 14 × f32: position xyz, target xyz,
    ///             start xyz, angle, size, strength, opacity, clamped frame time)
    /// Tiles       grid | varuint n | n × (u16 x, u16 y, u64 xxHash64, HeightTileCodec encoding)
    /// Manifest    grid | varuint n | n × (u16 x, u16 y, u64 xxHash64)
    /// TileRequest grid | varuint n | n × (u16 x, u16 y)
    /// grid        varuint width | varuint height | varuint tileSize
    /// </code>
    /// Heightmap tiles are absolute texels, so a tile can be applied any number of times and in any order relative to
    /// stamps (a later tile always wins). Decoders throw <see cref="DecodeException"/> for malformed input, including a
    /// tile whose decoded texels do not match its hash.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class TerrainMessage
    {
        public const byte Version = 1;

        /// <summary>Stamps in one message (a client frame produces a few dozen at most).</summary>
        public const int MaxStamps = 8192;

        /// <summary>Tiles in one tile message (1024 × 8 KiB raw = 8 MiB, the body limit).</summary>
        public const int MaxTiles = 960;

        /// <summary>Entries of one manifest or tile request.</summary>
        public const int MaxListEntries = 1 << 16;

        /// <summary>Largest texture edge accepted on the wire.</summary>
        public const int MaxGridEdge = 1 << 15;

        private const string What = "terrain message";

        /// <summary>Reads the header and returns the message type; the reader is left after the header.</summary>
        public static TerrainMessageType ReadHeader(ByteReader r)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            byte version = r.ReadByte();
            if (version != Version) throw new DecodeException(What + " version " + version.ToString(CultureInfo.InvariantCulture) + " (expected " + Version + ")");
            byte type = r.ReadByte();
            if (type < (byte)TerrainMessageType.Stamps || type > (byte)TerrainMessageType.TileRequest)
            {
                throw new DecodeException(What + ": unknown type " + type.ToString(CultureInfo.InvariantCulture));
            }
            return (TerrainMessageType)type;
        }

        // ------------------------------------------------------------------ stamps

        public static void WriteStamps(ByteWriter w, PeerId origin, IReadOnlyList<TerrainStamp> stamps)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            if (stamps == null) throw new ArgumentNullException(nameof(stamps));
            if (stamps.Count > MaxStamps) throw new ArgumentException("more than " + MaxStamps + " stamps", nameof(stamps));
            WriteHeader(w, TerrainMessageType.Stamps);
            w.WriteByte(origin.Value);
            w.WriteVarUInt32((uint)stamps.Count);
            for (int i = 0; i < stamps.Count; i++)
            {
                TerrainStamp s = stamps[i];
                w.WriteVarUInt32(s.ToolPrefab);
                w.WriteVarUInt32(s.BrushPrefab);
                w.WriteSingle(s.PositionX);
                w.WriteSingle(s.PositionY);
                w.WriteSingle(s.PositionZ);
                w.WriteSingle(s.TargetX);
                w.WriteSingle(s.TargetY);
                w.WriteSingle(s.TargetZ);
                w.WriteSingle(s.StartX);
                w.WriteSingle(s.StartY);
                w.WriteSingle(s.StartZ);
                w.WriteSingle(s.Angle);
                w.WriteSingle(s.Size);
                w.WriteSingle(s.Strength);
                w.WriteSingle(s.Opacity);
                w.WriteSingle(s.DeltaTime);
            }
        }

        /// <summary>Reads the body of a <see cref="TerrainMessageType.Stamps"/> message (after <see cref="ReadHeader"/>).</summary>
        public static PeerId ReadStamps(ByteReader r, List<TerrainStamp> output)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            if (output == null) throw new ArgumentNullException(nameof(output));
            var origin = new PeerId(r.ReadByte());
            uint n = r.ReadVarUInt32();
            if (n > MaxStamps) throw new DecodeException(What + " with " + n + " stamps");
            for (uint i = 0; i < n; i++)
            {
                output.Add(new TerrainStamp
                {
                    ToolPrefab = r.ReadVarUInt32(),
                    BrushPrefab = r.ReadVarUInt32(),
                    PositionX = r.ReadSingle(),
                    PositionY = r.ReadSingle(),
                    PositionZ = r.ReadSingle(),
                    TargetX = r.ReadSingle(),
                    TargetY = r.ReadSingle(),
                    TargetZ = r.ReadSingle(),
                    StartX = r.ReadSingle(),
                    StartY = r.ReadSingle(),
                    StartZ = r.ReadSingle(),
                    Angle = r.ReadSingle(),
                    Size = r.ReadSingle(),
                    Strength = r.ReadSingle(),
                    Opacity = r.ReadSingle(),
                    DeltaTime = r.ReadSingle(),
                });
            }
            EnsureEnd(r);
            return origin;
        }

        // ------------------------------------------------------------------ tiles

        /// <summary>
        /// Starts a <see cref="TerrainMessageType.Tiles"/> message; add tiles with <see cref="AddTile"/> and finish with
        /// <see cref="EndTiles"/>. Returns the position of the count to patch.
        /// </summary>
        public static int BeginTiles(ByteWriter w, TileGridInfo grid)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            WriteHeader(w, TerrainMessageType.Tiles);
            WriteGrid(w, grid);
            return w.Reserve(4);
        }

        /// <summary>
        /// Appends one tile (texels row-major, <c>w × h</c> of the tile's rectangle) and returns its hash. The body is
        /// zero-run coded; the envelope's LZ4 compresses the message (no compressor here, so nothing is compressed twice).
        /// </summary>
        public static ulong AddTile(ByteWriter w, TileId tile, ushort[] texels, int count)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            ulong hash = HeightTileCodec.Hash(texels, count);
            w.WriteUInt16(tile.X);
            w.WriteUInt16(tile.Y);
            w.WriteUInt64(hash);
            HeightTileCodec.Encode(texels, count, w, null);
            return hash;
        }

        /// <summary>Patches the tile count written by <see cref="BeginTiles"/>.</summary>
        public static void EndTiles(ByteWriter w, int countPosition, int count)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            if (count < 0 || count > MaxTiles) throw new ArgumentOutOfRangeException(nameof(count), count, "0 to " + MaxTiles + " tiles");
            w.PatchUInt32(countPosition, (uint)count);
        }

        /// <summary>Reads the grid and tile count of a <see cref="TerrainMessageType.Tiles"/> message.</summary>
        public static int ReadTilesHeader(ByteReader r, out TileGridInfo grid)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            grid = ReadGrid(r);
            uint n = r.ReadUInt32();
            if (n > MaxTiles) throw new DecodeException(What + " with " + n + " tiles");
            return (int)n;
        }

        /// <summary>
        /// Reads the next tile into <paramref name="texels"/> (at least <c>tileSize²</c> long). Validates the tile id
        /// against <paramref name="grid"/> and the decoded texels against the hash. Returns the texel count
        /// (<c>w × h</c> of <see cref="TileGrid.GetRect"/>).
        /// </summary>
        public static int ReadTile(ByteReader r, TileGrid grid, ushort[] texels, out TileId tile, out ulong hash)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            if (grid == null) throw new ArgumentNullException(nameof(grid));
            tile = ReadTileId(r, grid);
            hash = r.ReadUInt64();
            grid.GetRect(tile, out _, out _, out int w, out int h);
            int count = w * h;
            if (texels == null || texels.Length < count) throw new ArgumentException("the texel buffer is too small", nameof(texels));
            HeightTileCodec.Decode(r, texels, count, null);
            if (HeightTileCodec.Hash(texels, count) != hash) throw new DecodeException(What + ": " + tile + " does not match its hash");
            return count;
        }

        // ------------------------------------------------------------------ manifest and requests

        public static void WriteManifest(ByteWriter w, TileGridInfo grid, IReadOnlyList<KeyValuePair<TileId, ulong>> hashes)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            if (hashes == null) throw new ArgumentNullException(nameof(hashes));
            if (hashes.Count > MaxListEntries) throw new ArgumentException("more than " + MaxListEntries + " entries", nameof(hashes));
            WriteHeader(w, TerrainMessageType.Manifest);
            WriteGrid(w, grid);
            w.WriteVarUInt32((uint)hashes.Count);
            foreach (KeyValuePair<TileId, ulong> e in hashes)
            {
                w.WriteUInt16(e.Key.X);
                w.WriteUInt16(e.Key.Y);
                w.WriteUInt64(e.Value);
            }
        }

        /// <summary>Reads a manifest body; tile ids are validated against the grid it names.</summary>
        public static TileGridInfo ReadManifest(ByteReader r, List<KeyValuePair<TileId, ulong>> output)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            if (output == null) throw new ArgumentNullException(nameof(output));
            TileGridInfo info = ReadGrid(r);
            TileGrid grid = ToGrid(info);
            uint n = r.ReadVarUInt32();
            if (n > MaxListEntries) throw new DecodeException(What + ": manifest with " + n + " entries");
            for (uint i = 0; i < n; i++)
            {
                TileId tile = ReadTileId(r, grid);
                output.Add(new KeyValuePair<TileId, ulong>(tile, r.ReadUInt64()));
            }
            EnsureEnd(r);
            return info;
        }

        public static void WriteTileRequest(ByteWriter w, TileGridInfo grid, IReadOnlyList<TileId> tiles)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            if (tiles == null) throw new ArgumentNullException(nameof(tiles));
            if (tiles.Count > MaxListEntries) throw new ArgumentException("more than " + MaxListEntries + " entries", nameof(tiles));
            WriteHeader(w, TerrainMessageType.TileRequest);
            WriteGrid(w, grid);
            w.WriteVarUInt32((uint)tiles.Count);
            for (int i = 0; i < tiles.Count; i++)
            {
                w.WriteUInt16(tiles[i].X);
                w.WriteUInt16(tiles[i].Y);
            }
        }

        public static TileGridInfo ReadTileRequest(ByteReader r, List<TileId> output)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            if (output == null) throw new ArgumentNullException(nameof(output));
            TileGridInfo info = ReadGrid(r);
            TileGrid grid = ToGrid(info);
            uint n = r.ReadVarUInt32();
            if (n > MaxListEntries) throw new DecodeException(What + ": request with " + n + " entries");
            for (uint i = 0; i < n; i++) output.Add(ReadTileId(r, grid));
            EnsureEnd(r);
            return info;
        }

        // ------------------------------------------------------------------ shared

        /// <summary>Writes a grid (also used by <see cref="WaterMessage"/>).</summary>
        internal static void WriteGrid(ByteWriter w, TileGridInfo grid)
        {
            CheckGrid(grid);
            w.WriteVarUInt32((uint)grid.Width);
            w.WriteVarUInt32((uint)grid.Height);
            w.WriteVarUInt32((uint)grid.TileSize);
        }

        internal static TileGridInfo ReadGrid(ByteReader r)
        {
            uint width = r.ReadVarUInt32(), height = r.ReadVarUInt32(), tileSize = r.ReadVarUInt32();
            if (width == 0 || height == 0 || tileSize == 0 || width > MaxGridEdge || height > MaxGridEdge || tileSize > MaxGridEdge)
            {
                throw new DecodeException("tile grid " + width + "x" + height + "/" + tileSize + " is out of range");
            }
            return new TileGridInfo((int)width, (int)height, (int)tileSize);
        }

        /// <summary>A <see cref="TileGrid"/> for a (validated) grid description.</summary>
        public static TileGrid ToGrid(TileGridInfo info)
        {
            try
            {
                return new TileGrid(info.Width, info.Height, info.TileSize);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                throw new DecodeException("tile grid " + info + ": " + ex.Message);
            }
        }

        internal static TileId ReadTileId(ByteReader r, TileGrid grid)
        {
            var tile = new TileId(r.ReadUInt16(), r.ReadUInt16());
            if (!grid.Contains(tile)) throw new DecodeException(tile + " lies outside the grid " + grid);
            return tile;
        }

        /// <summary>Throws <see cref="DecodeException"/> when bytes remain after the last tile.</summary>
        public static void EnsureEnd(ByteReader r)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            if (!r.IsAtEnd) throw new DecodeException(What + " has " + r.Remaining + " trailing bytes");
        }

        private static void CheckGrid(TileGridInfo grid)
        {
            if (grid.Width <= 0 || grid.Height <= 0 || grid.TileSize <= 0 || grid.Width > MaxGridEdge || grid.Height > MaxGridEdge || grid.TileSize > MaxGridEdge)
            {
                throw new ArgumentOutOfRangeException(nameof(grid), grid.ToString(), "grid out of range");
            }
        }

        private static void WriteHeader(ByteWriter w, TerrainMessageType type)
        {
            w.WriteByte(Version);
            w.WriteByte((byte)type);
        }
    }
}
