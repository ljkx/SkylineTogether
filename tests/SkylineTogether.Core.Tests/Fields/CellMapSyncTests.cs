using System;
using System.Collections.Generic;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Fields;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Fields
{
    /// <summary>w2/simvals: cell-map region diffing (layout, tile hashes, per-peer state), tile and patch codecs, masked merge.</summary>
    public sealed class CellMapSyncTests
    {
        private static byte[] RandomMap(CellMapLayout layout, ulong seed)
        {
            var random = new DeterministicRandom(seed);
            var map = new byte[layout.MapBytes];
            random.NextBytes(map, 0, map.Length);
            return map;
        }

        [Test]
        public void LayoutCoversEveryCellExactlyOnce()
        {
            var layout = new CellMapLayout(70, 45, 6, 16);
            Assert.Equal(5, layout.TilesX);
            Assert.Equal(3, layout.TilesY);
            var covered = new int[70 * 45];
            int bytes = 0;
            for (int t = 0; t < layout.TileCount; t++)
            {
                layout.GetTileRect(t, out int x0, out int y0, out int w, out int h);
                for (int y = y0; y < y0 + h; y++)
                {
                    for (int x = x0; x < x0 + w; x++) covered[y * 70 + x]++;
                }
                bytes += layout.TileBytes(t);
            }
            foreach (int c in covered) Assert.Equal(1, c);
            Assert.Equal(layout.MapBytes, bytes);
        }

        [Test]
        public void TilesRoundTripThroughExtractAndWrite()
        {
            var layout = new CellMapLayout(50, 33, 4, 8);
            byte[] map = RandomMap(layout, 1);
            var copy = new byte[map.Length];
            var scratch = new byte[layout.MaxTileBytes];
            for (int t = 0; t < layout.TileCount; t++)
            {
                layout.ExtractTile(map, t, scratch);
                layout.WriteTile(scratch, t, copy);
            }
            Assert.SequenceEqual(map, copy);
        }

        [Test]
        public void TileHashesFindExactlyTheChangedRegions()
        {
            var layout = new CellMapLayout(256, 256, 2, 16);
            byte[] map = RandomMap(layout, 2);
            var hashes = new ulong[layout.TileCount];
            var scratch = new byte[layout.MaxTileBytes];
            Assert.Equal(layout.TileCount, layout.HashTiles(map, hashes, scratch));
            Assert.Equal(0, layout.HashTiles(map, hashes, scratch));
            // change cell (100, 37) and (255, 255)
            map[(37 * 256 + 100) * 2] ^= 1;
            map[map.Length - 1] ^= 0x80;
            var before = (ulong[])hashes.Clone();
            Assert.Equal(2, layout.HashTiles(map, hashes, scratch));
            for (int t = 0; t < layout.TileCount; t++)
            {
                bool expectChange = t == 2 * 16 + 6 || t == layout.TileCount - 1;
                Assert.Equal(expectChange, before[t] != hashes[t], "tile " + t);
                Assert.True(hashes[t] != 0, "hash 0 is reserved");
            }
        }

        [Test]
        public void PeerStateOffersChangedTilesRoundRobin()
        {
            var state = new CellMapPeerState(4);
            var current = new ulong[] { 11, 0, 33, 44 };
            var offered = new List<int>();
            while (state.NextChanged(current, out int tile))
            {
                offered.Add(tile);
                state.MarkSent(tile, current[tile]);
            }
            Assert.SequenceEqual(new[] { 0, 2, 3 }, offered, "unknown (0) tiles are never offered");
            Assert.Equal(0, state.CountChanged(current));
            current[0] = 12;
            current[3] = 45;
            Assert.Equal(2, state.CountChanged(current));
            Assert.True(state.NextChanged(current, out int next));
            Assert.Equal(0, next, "the cursor wrapped after tile 3");
            state.Reset(4);
            Assert.Equal(3, state.CountChanged(current));
        }

        [Test]
        public void BudgetLimitedSenderEventuallyConverges()
        {
            var layout = new CellMapLayout(64, 64, 4, 16);
            byte[] host = RandomMap(layout, 3);
            byte[] client = new byte[host.Length];
            var hashes = new ulong[layout.TileCount];
            var scratch = new byte[layout.MaxTileBytes];
            var tile = new byte[layout.MaxTileBytes];
            var state = new CellMapPeerState(layout.TileCount);
            var random = new DeterministicRandom(4);
            uint mapId = CellMapMessage.MapId("AirPollution");
            for (int round = 0; round < 200; round++)
            {
                if (round < 150 && random.Next(3) == 0) host[random.Next(host.Length)] ^= (byte)(1 + random.Next(255));
                layout.HashTiles(host, hashes, scratch);
                // at most 2 tiles per round
                var w = new ByteWriter();
                int countPos = CellMapMessage.BeginTiles(w, mapId, layout);
                int n = 0;
                while (n < 2 && state.NextChanged(hashes, out int t))
                {
                    CellMapMessage.AddTile(w, layout, t, host, scratch);
                    state.MarkSent(t, hashes[t]);
                    n++;
                }
                CellMapMessage.EndTiles(w, countPos, n);
                var r = new ByteReader(w.Buffer, 0, w.Length);
                CellMapMessage.Header h = CellMapMessage.ReadHeader(r);
                Assert.Equal(mapId, h.MapId);
                Assert.True(h.Matches(layout));
                int count = CellMapMessage.ReadTileCount(r, layout);
                for (int i = 0; i < count; i++)
                {
                    int t = CellMapMessage.ReadTile(r, layout, tile);
                    layout.WriteTile(tile, t, client);
                }
                Assert.True(r.IsAtEnd);
            }
            Assert.SequenceEqual(host, client);
        }

        [Test]
        public void PatchRoundTripsAndMergesOnlyMaskedFields()
        {
            var layout = new CellMapLayout(32, 32, 4, 16);
            byte[] clientMap = RandomMap(layout, 5);
            byte[] hostMap = RandomMap(layout, 6);
            byte[] original = (byte[])hostMap.Clone();
            var w = new ByteWriter();
            var scratch = new byte[CellMapMessage.MaxPatchCells * CellMapMessage.MaxPatchCells * 4];
            CellMapMessage.WritePatch(w, 7, layout, 3, 4, 5, 6, clientMap, scratch);
            var r = new ByteReader(w.Buffer, 0, w.Length);
            CellMapMessage.Header h = CellMapMessage.ReadHeader(r);
            Assert.Equal(CellMapMessage.TypePatch, h.Type);
            byte[] cells = null;
            CellMapMessage.ReadPatch(r, layout, out int x0, out int y0, out int pw, out int ph, ref cells);
            Assert.True(r.IsAtEnd);
            Assert.Equal(3, x0);
            Assert.Equal(6, ph);
            var mask = new byte[] { 0xFF, 0xFF, 0, 0 };   // first u16 of each cell is the painted field
            CellMapMessage.MergeMasked(layout, hostMap, x0, y0, pw, ph, cells, mask);
            for (int y = 0; y < 32; y++)
            {
                for (int x = 0; x < 32; x++)
                {
                    bool inside = x >= 3 && x < 8 && y >= 4 && y < 10;
                    int i = (y * 32 + x) * 4;
                    for (int b = 0; b < 4; b++)
                    {
                        byte want = inside && b < 2 ? clientMap[i + b] : original[i + b];
                        Assert.Equal(want, hostMap[i + b], "cell " + x + "," + y + " byte " + b);
                    }
                }
            }
        }

        [Test]
        public void MalformedCellMessagesThrowDecodeException()
        {
            var layout = new CellMapLayout(32, 32, 2, 16);
            byte[] map = RandomMap(layout, 8);
            var w = new ByteWriter();
            int c = CellMapMessage.BeginTiles(w, 1, layout);
            CellMapMessage.AddTile(w, layout, 3, map, new byte[layout.MaxTileBytes]);
            CellMapMessage.EndTiles(w, c, 1);
            byte[] good = w.ToArray();
            var tile = new byte[layout.MaxTileBytes];
            for (int cut = 0; cut < good.Length; cut++)
            {
                int length = cut;
                Assert.Throws<DecodeException>(() =>
                {
                    var r = new ByteReader(good, 0, length);
                    CellMapMessage.ReadHeader(r);
                    int n = CellMapMessage.ReadTileCount(r, layout);
                    for (int i = 0; i < n; i++) CellMapMessage.ReadTile(r, layout, tile);
                }, "truncated at " + cut);
            }
            var badTile = (byte[])good.Clone();
            badTile[CellMapHeaderBytes + 4] = 0x7F;   // tile index 127 (the map has 4 tiles)
            Assert.Throws<DecodeException>(() =>
            {
                var r = new ByteReader(badTile, 0, badTile.Length);
                CellMapMessage.ReadHeader(r);
                CellMapMessage.ReadTileCount(r, layout);
                CellMapMessage.ReadTile(r, layout, tile);
            });
            var badLayout = (byte[])good.Clone();
            badLayout[CellMapHeaderBytes - 1] = 200;  // tile edge outside 4..64
            Assert.Throws<DecodeException>(() => CellMapMessage.ReadHeader(new ByteReader(badLayout, 0, badLayout.Length)));
            // a patch outside the map
            var p = new ByteWriter();
            CellMapMessage.WritePatch(p, 1, layout, 30, 30, 2, 2, map, new byte[64]);
            byte[] patch = p.ToArray();
            patch[CellMapHeaderBytes] = 31;           // x0 = 31 with width 2
            byte[] cells = null;
            Assert.Throws<DecodeException>(() =>
            {
                var r = new ByteReader(patch, 0, patch.Length);
                CellMapMessage.ReadHeader(r);
                CellMapMessage.ReadPatch(r, layout, out _, out _, out _, out _, ref cells);
            });
        }

        /// <summary>u8 version, u8 type, u32 id, u16 width, u16 height, u8 cell bytes, u8 tile edge.</summary>
        private const int CellMapHeaderBytes = 12;

        [Test]
        public void MapIdsAreStableAndNonZero()
        {
            Assert.Equal(CellMapMessage.MapId("LandValue"), CellMapMessage.MapId("LandValue"));
            Assert.NotEqual(CellMapMessage.MapId("LandValue"), CellMapMessage.MapId("AirPollution"));
            Assert.True(CellMapMessage.MapId(null) != 0);
        }

        [Test]
        public void InvalidLayoutsAreRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CellMapLayout(0, 10, 4, 16));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CellMapLayout(10, 10, 0, 16));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CellMapLayout(10, 10, 4, 2));
            var layout = new CellMapLayout(10, 10, 1, 4);
            Assert.Throws<ArgumentOutOfRangeException>(() => layout.ExtractRect(new byte[100], 8, 8, 3, 1, new byte[10], 0));
            var tiles = new List<int>();
            Assert.Equal(4, layout.TilesInRect(-5, -5, 5, 5, tiles));
            tiles.Clear();
            Assert.Equal(0, layout.TilesInRect(20, 20, 30, 30, tiles));
        }
    }
}
