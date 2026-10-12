using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Fields;
using SkylineTogether.Core.Sync;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Sync
{
    /// <summary>TerrainMessage and WaterMessage: round trips of every message type and rejection of malformed input.</summary>
    public sealed class EnvironmentMessageTests
    {
        private static readonly TileGridInfo HeightGrid = new TileGridInfo(4096, 4096, 64);

        private static TerrainStamp Stamp(int i)
        {
            return new TerrainStamp
            {
                ToolPrefab = 1000u + (uint)i, BrushPrefab = 7, PositionX = -1234.5f + i, PositionY = 512.25f, PositionZ = 77.125f,
                TargetX = 1, TargetY = 2, TargetZ = 3, StartX = -4, StartY = -5, StartZ = -6, Angle = 0.75f, Size = 150f,
                Strength = -0.0015f, Opacity = 0.25f, DeltaTime = 0.05f
            };
        }

        private static ushort[] Hill(int seed)
        {
            var texels = new ushort[64 * 64];
            for (int y = 0; y < 64; y++)
            {
                for (int x = 0; x < 64; x++) texels[y * 64 + x] = (ushort)(8191 + seed * 13 + x * 3 + (int)(200 * Math.Sin(y * 0.1)));
            }
            return texels;
        }

        private static ByteReader Reader(ByteWriter w) => new ByteReader(w.ToArray(), 0, w.Length);

        [Test]
        public void StampsRoundTripBitForBit()
        {
            var stamps = new List<TerrainStamp>();
            for (int i = 0; i < 5; i++) stamps.Add(Stamp(i));
            var w = new ByteWriter();
            TerrainMessage.WriteStamps(w, new PeerId(3), stamps);

            ByteReader r = Reader(w);
            Assert.Equal(TerrainMessageType.Stamps, TerrainMessage.ReadHeader(r));
            var read = new List<TerrainStamp>();
            PeerId origin = TerrainMessage.ReadStamps(r, read);
            Assert.Equal(new PeerId(3), origin);
            Assert.Equal(5, read.Count);
            for (int i = 0; i < 5; i++)
            {
                Assert.Equal(stamps[i].ToolPrefab, read[i].ToolPrefab);
                Assert.Equal(stamps[i].BrushPrefab, read[i].BrushPrefab);
                Assert.Equal(stamps[i].PositionX, read[i].PositionX);
                Assert.Equal(stamps[i].PositionZ, read[i].PositionZ);
                Assert.Equal(stamps[i].StartZ, read[i].StartZ);
                Assert.Equal(stamps[i].Strength, read[i].Strength);
                Assert.Equal(stamps[i].Opacity, read[i].Opacity);
                Assert.Equal(stamps[i].DeltaTime, read[i].DeltaTime);
                Assert.True(read[i].IsPlausible(10000f));
            }
        }

        [Test]
        public void StampPlausibilityRejectsGarbage()
        {
            TerrainStamp s = Stamp(0);
            s.Strength = float.NaN;
            Assert.False(s.IsPlausible(10000f), "NaN strength");
            s = Stamp(0);
            s.Size = 0f;
            Assert.False(s.IsPlausible(10000f), "zero size");
            s = Stamp(0);
            s.Size = 20000f;
            Assert.False(s.IsPlausible(10000f), "oversized");
            s = Stamp(0);
            s.ToolPrefab = 0;
            Assert.False(s.IsPlausible(10000f), "no tool prefab");
        }

        [Test]
        public void TilesRoundTripWithHashes()
        {
            var w = new ByteWriter();
            int countAt = TerrainMessage.BeginTiles(w, HeightGrid);
            ushort[] a = Hill(1), b = Hill(2);
            ulong ha = TerrainMessage.AddTile(w, new TileId(0, 0), a, a.Length);
            ulong hb = TerrainMessage.AddTile(w, new TileId(63, 17), b, b.Length);
            TerrainMessage.EndTiles(w, countAt, 2);
            Assert.Equal(HeightTileCodec.Hash(a, a.Length), ha);

            ByteReader r = Reader(w);
            Assert.Equal(TerrainMessageType.Tiles, TerrainMessage.ReadHeader(r));
            int n = TerrainMessage.ReadTilesHeader(r, out TileGridInfo info);
            Assert.Equal(2, n);
            Assert.Equal(HeightGrid, info);
            TileGrid grid = TerrainMessage.ToGrid(info);
            var texels = new ushort[64 * 64];
            Assert.Equal(4096, TerrainMessage.ReadTile(r, grid, texels, out TileId t0, out ulong h0));
            Assert.Equal(new TileId(0, 0), t0);
            Assert.Equal(ha, h0);
            Assert.SequenceEqual(a, texels);
            TerrainMessage.ReadTile(r, grid, texels, out TileId t1, out ulong h1);
            Assert.Equal(new TileId(63, 17), t1);
            Assert.Equal(hb, h1);
            Assert.SequenceEqual(b, texels);
            Assert.True(r.IsAtEnd);
        }

        [Test]
        public void EdgeTilesOfAnUnevenGridUseTheirClippedSize()
        {
            var info = new TileGridInfo(100, 70, 64);
            TileGrid grid = TerrainMessage.ToGrid(info);
            grid.GetRect(new TileId(1, 1), out _, out _, out int tw, out int th);
            var texels = new ushort[tw * th];
            for (int i = 0; i < texels.Length; i++) texels[i] = (ushort)(i * 7);
            var w = new ByteWriter();
            int at = TerrainMessage.BeginTiles(w, info);
            TerrainMessage.AddTile(w, new TileId(1, 1), texels, texels.Length);
            TerrainMessage.EndTiles(w, at, 1);

            ByteReader r = Reader(w);
            TerrainMessage.ReadHeader(r);
            TerrainMessage.ReadTilesHeader(r, out _);
            var output = new ushort[64 * 64];
            Assert.Equal(36 * 6, TerrainMessage.ReadTile(r, grid, output, out _, out _));
            for (int i = 0; i < texels.Length; i++) Assert.Equal(texels[i], output[i]);
        }

        [Test]
        public void ACorruptTileFailsItsHash()
        {
            var w = new ByteWriter();
            int at = TerrainMessage.BeginTiles(w, HeightGrid);
            ushort[] a = Hill(5);
            TerrainMessage.AddTile(w, new TileId(2, 3), a, a.Length);
            TerrainMessage.EndTiles(w, at, 1);
            byte[] bytes = w.ToArray();
            // the hash starts after header (2), grid (3 varuints: 4096 → 2 bytes each, 64 → 1), count (4) and the tile id (4)
            int hashAt = 2 + 2 + 2 + 1 + 4 + 4;
            bytes[hashAt] ^= 0x5A;
            var r = new ByteReader(bytes, 0, bytes.Length);
            TerrainMessage.ReadHeader(r);
            TerrainMessage.ReadTilesHeader(r, out TileGridInfo info);
            Assert.Throws<DecodeException>(() => TerrainMessage.ReadTile(r, TerrainMessage.ToGrid(info), new ushort[4096], out _, out _));
        }

        [Test]
        public void ManifestAndRequestRoundTrip()
        {
            var hashes = new List<KeyValuePair<TileId, ulong>>
            {
                new KeyValuePair<TileId, ulong>(new TileId(0, 5), 0x0123456789ABCDEFUL),
                new KeyValuePair<TileId, ulong>(new TileId(63, 5), ulong.MaxValue),
            };
            var w = new ByteWriter();
            TerrainMessage.WriteManifest(w, HeightGrid, hashes);
            ByteReader r = Reader(w);
            Assert.Equal(TerrainMessageType.Manifest, TerrainMessage.ReadHeader(r));
            var read = new List<KeyValuePair<TileId, ulong>>();
            Assert.Equal(HeightGrid, TerrainMessage.ReadManifest(r, read));
            Assert.Equal(2, read.Count);
            Assert.Equal(hashes[1].Key, read[1].Key);
            Assert.Equal(hashes[1].Value, read[1].Value);

            w.Reset();
            TerrainMessage.WriteTileRequest(w, HeightGrid, new[] { new TileId(1, 2), new TileId(3, 4) });
            r = Reader(w);
            Assert.Equal(TerrainMessageType.TileRequest, TerrainMessage.ReadHeader(r));
            var tiles = new List<TileId>();
            Assert.Equal(HeightGrid, TerrainMessage.ReadTileRequest(r, tiles));
            Assert.SequenceEqual(new[] { new TileId(1, 2), new TileId(3, 4) }, tiles);
        }

        [Test]
        public void MalformedTerrainMessagesThrowDecodeException()
        {
            Assert.Throws<DecodeException>(() => TerrainMessage.ReadHeader(new ByteReader(new byte[] { 2, 1 }, 0, 2)), "version");
            Assert.Throws<DecodeException>(() => TerrainMessage.ReadHeader(new ByteReader(new byte[] { 1, 9 }, 0, 2)), "type");

            // a tile outside the grid
            var w = new ByteWriter();
            TerrainMessage.WriteTileRequest(w, new TileGridInfo(128, 128, 64), new[] { new TileId(1, 1) });
            byte[] bytes = w.ToArray();
            bytes[bytes.Length - 4] = 2; // x = 2 of a 2×2 grid
            var r = new ByteReader(bytes, 0, bytes.Length);
            TerrainMessage.ReadHeader(r);
            Assert.Throws<DecodeException>(() => TerrainMessage.ReadTileRequest(r, new List<TileId>()), "tile outside");

            // truncated stamps
            w.Reset();
            TerrainMessage.WriteStamps(w, PeerId.Host, new[] { Stamp(1) });
            bytes = w.ToArray();
            r = new ByteReader(bytes, 0, bytes.Length - 3);
            TerrainMessage.ReadHeader(r);
            Assert.Throws<DecodeException>(() => TerrainMessage.ReadStamps(r, new List<TerrainStamp>()), "truncated");

            // trailing bytes
            w.Reset();
            TerrainMessage.WriteStamps(w, PeerId.Host, new[] { Stamp(1) });
            w.WriteByte(0);
            r = Reader(w);
            TerrainMessage.ReadHeader(r);
            Assert.Throws<DecodeException>(() => TerrainMessage.ReadStamps(r, new List<TerrainStamp>()), "trailing");

            // a zero grid
            Assert.Throws<DecodeException>(() => TerrainMessage.ReadGrid(new ByteReader(new byte[] { 0, 1, 1 }, 0, 3)), "grid");
        }

        [Test]
        public void TheLargestTileMessageFitsTheBodyLimit()
        {
            var random = new DeterministicRandom(42);
            var noise = new ushort[64 * 64];
            for (int i = 0; i < noise.Length; i++) noise[i] = (ushort)random.Next(65536);
            var w = new ByteWriter(1 << 20);
            int at = TerrainMessage.BeginTiles(w, HeightGrid);
            for (int i = 0; i < TerrainMessage.MaxTiles; i++) TerrainMessage.AddTile(w, new TileId((ushort)(i % 64), (ushort)(i / 64)), noise, noise.Length);
            TerrainMessage.EndTiles(w, at, TerrainMessage.MaxTiles);
            Assert.True(w.Length <= SkylineTogether.Core.Protocol.Limits.MaxBodyBytes(MessageKind.Terrain), "worst-case tiles: " + w.Length);
        }

        [Test]
        public void WaterTilesRoundTripWithSeaLevel()
        {
            var info = new TileGridInfo(2048, 2048, 256);
            TileGrid grid = TerrainMessage.ToGrid(info);
            const int texels = 256 * 256;
            var rgba = new float[texels * 4];
            for (int i = 0; i < texels; i++)
            {
                rgba[i * 4] = i % 7 == 0 ? 0f : 12.5f + (i % 100) * 0.01f;
                rgba[i * 4 + 1] = 0.5f;
                rgba[i * 4 + 2] = -0.25f;
                rgba[i * 4 + 3] = (i % 256) / 255f;
            }
            var w = new ByteWriter(1 << 20);
            int at = WaterMessage.BeginTiles(w, 511.7f, info);
            WaterMessage.AddTile(w, new TileId(7, 3), rgba, texels, WaterTileLevel.DepthPollutionVelocity);
            WaterMessage.EndTiles(w, at, 1);

            ByteReader r = Reader(w);
            int n = WaterMessage.ReadHeader(r, out float sea, out TileGridInfo readInfo);
            Assert.Equal(1, n);
            Assert.Equal(511.7f, sea);
            Assert.Equal(info, readInfo);
            var output = new float[texels * 4];
            Assert.Equal(texels, WaterMessage.ReadTile(r, grid, output, out TileId tile));
            Assert.Equal(new TileId(7, 3), tile);
            Assert.True(r.IsAtEnd);
            for (int i = 0; i < texels; i += 97)
            {
                Assert.Near(rgba[i * 4], output[i * 4], 0.0005, "depth " + i);
                Assert.Equal(0.5f, output[i * 4 + 1]);
                Assert.Equal(-0.25f, output[i * 4 + 2]);
                Assert.Near(rgba[i * 4 + 3], output[i * 4 + 3], 1.0 / 255, "pollution " + i);
            }
        }

        [Test]
        public void MalformedWaterMessagesThrowDecodeException()
        {
            var w = new ByteWriter();
            int at = WaterMessage.BeginTiles(w, float.NaN, new TileGridInfo(2048, 2048, 256));
            WaterMessage.EndTiles(w, at, 0);
            Assert.Throws<DecodeException>(() => WaterMessage.ReadHeader(Reader(w), out _, out _), "NaN sea level");

            w.Reset();
            at = WaterMessage.BeginTiles(w, 500f, new TileGridInfo(2048, 2048, 256));
            WaterMessage.EndTiles(w, at, 0);
            byte[] bytes = w.ToArray();
            bytes[1] = 2;
            Assert.Throws<DecodeException>(() => WaterMessage.ReadHeader(new ByteReader(bytes, 0, bytes.Length), out _, out _), "type");
            bytes[1] = 1;
            bytes[bytes.Length - 1] = 0x7F; // tile count far above the limit
            Assert.Throws<DecodeException>(() => WaterMessage.ReadHeader(new ByteReader(bytes, 0, bytes.Length), out _, out _), "count");
        }

        [Test]
        public void JoinersDropStrokeReplaysButKeepTilesAndManifests()
        {
            // polish: stamps that reach a joiner before it is live are in its snapshot already (or covered by the host's
            // settled tiles); tiles and manifests are absolute and kept
            var w = new ByteWriter();
            TerrainMessage.WriteStamps(w, new PeerId(2), new[] { Stamp(1) });
            byte[] stamps = w.ToArray();
            Assert.False(TerrainMessage.KeepBeforeLive(stamps, 0, stamps.Length));
            w.Reset();
            var grid = new TileGridInfo(128, 128, 64);
            TerrainMessage.WriteManifest(w, grid, new List<KeyValuePair<TileId, ulong>>());
            byte[] manifest = w.ToArray();
            Assert.True(TerrainMessage.KeepBeforeLive(manifest, 0, manifest.Length));
            w.Reset();
            int at = TerrainMessage.BeginTiles(w, grid);
            TerrainMessage.EndTiles(w, at, 0);
            byte[] tiles = w.ToArray();
            Assert.True(TerrainMessage.KeepBeforeLive(tiles, 0, tiles.Length));
            Assert.True(TerrainMessage.KeepBeforeLive(new byte[] { 99, 1 }, 0, 2), "malformed: left to the normal decode");
            Assert.True(TerrainMessage.KeepBeforeLive(null, 0, 0));
        }
    }
}
