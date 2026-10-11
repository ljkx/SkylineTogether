using System;
using System.Collections.Generic;
using SkylineTogether.Core.Fields;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Fields
{
    /// <summary>TileGrid and TileId: tile counts, edge tiles, enumeration at borders, indices, the brush texel rectangle.</summary>
    public sealed class TileGridTests
    {
        [Test]
        public void TerrainAndWaterGridsHaveTheDesignedTileCounts()
        {
            var terrain = new TileGrid(4096, 4096, 64);
            Assert.Equal(64, terrain.TilesX);
            Assert.Equal(64, terrain.TilesY);
            Assert.Equal(4096, terrain.TileCount);
            var water = new TileGrid(2048, 2048, 256);
            Assert.Equal(8, water.TilesX);
            Assert.Equal(64, water.TileCount);
            water.GetRect(new TileId(7, 7), out int x, out int y, out int w, out int h);
            Assert.Equal(1792, x);
            Assert.Equal(1792, y);
            Assert.Equal(256, w);
            Assert.Equal(256, h);
        }

        [Test]
        public void EdgeTilesAreClippedToTheGrid()
        {
            var grid = new TileGrid(100, 70, 32);   // 4 × 3 tiles; the last column is 4 wide, the last row 6 high
            Assert.Equal(4, grid.TilesX);
            Assert.Equal(3, grid.TilesY);
            grid.GetRect(new TileId(0, 0), out int x, out int y, out int w, out int h);
            Assert.SequenceEqual(new[] { 0, 0, 32, 32 }, new[] { x, y, w, h });
            grid.GetRect(new TileId(3, 2), out x, out y, out w, out h);
            Assert.SequenceEqual(new[] { 96, 64, 4, 6 }, new[] { x, y, w, h });
            grid.GetRect(new TileId(3, 0), out x, out y, out w, out h);
            Assert.SequenceEqual(new[] { 96, 0, 4, 32 }, new[] { x, y, w, h });
            Assert.Throws<ArgumentOutOfRangeException>(() => grid.GetRect(new TileId(4, 0), out _, out _, out _, out _));
            Assert.Throws<ArgumentOutOfRangeException>(() => grid.GetRect(new TileId(0, 3), out _, out _, out _, out _));
        }

        [Test]
        public void TilesInEnumeratesRowMajor()
        {
            var grid = new TileGrid(256, 256, 64);
            var all = new List<TileId>(grid.TilesIn(0, 0, 255, 255));
            Assert.Equal(16, all.Count);
            for (int i = 0; i < all.Count; i++) Assert.Equal(grid.TileAt(i), all[i], "tile " + i);
            Assert.SequenceEqual(new[] { new TileId(0, 0), new TileId(1, 0), new TileId(0, 1), new TileId(1, 1) },
                                 grid.TilesIn(63, 63, 64, 64), "a rectangle across a tile corner");
        }

        [Test]
        public void TilesInHandlesBordersAndEmptyRectangles()
        {
            var grid = new TileGrid(256, 256, 64);
            Assert.SequenceEqual(new[] { new TileId(1, 0) }, grid.TilesIn(64, 0, 127, 63), "exactly one tile");
            Assert.SequenceEqual(new[] { new TileId(0, 0) }, grid.TilesIn(0, 0, 0, 0), "first texel");
            Assert.SequenceEqual(new[] { new TileId(3, 3) }, grid.TilesIn(255, 255, 255, 255), "last texel");
            Assert.SequenceEqual(new[] { new TileId(0, 0) }, grid.TilesIn(-100, -5, 10, 10), "clipped at the top-left");
            Assert.SequenceEqual(new[] { new TileId(3, 3) }, grid.TilesIn(250, 250, 1000, 1000), "clipped at the bottom-right");
            Assert.SequenceEqual(new TileId[0], grid.TilesIn(256, 0, 300, 10), "right of the grid");
            Assert.SequenceEqual(new TileId[0], grid.TilesIn(-10, -10, -1, -1), "above and left of the grid");
            Assert.SequenceEqual(new TileId[0], grid.TilesIn(10, 10, 9, 20), "max below min");
            Assert.SequenceEqual(new TileId[0], grid.TilesIn(10, 10, 20, 9), "max below min (y)");
            Assert.Equal(16, new List<TileId>(grid.TilesIn(int.MinValue, int.MinValue, int.MaxValue, int.MaxValue)).Count, "extreme bounds");
        }

        [Test]
        public void TilesInMatchesBruteForceOnRandomGrids()
        {
            var random = new DeterministicRandom(4108);
            for (int round = 0; round < 300; round++)
            {
                var grid = new TileGrid(1 + random.Next(300), 1 + random.Next(300), 1 + random.Next(80));
                int minX = random.Next(-50, 350), maxX = random.Next(-50, 350);
                int minY = random.Next(-50, 350), maxY = random.Next(-50, 350);
                var expected = new List<TileId>();
                int cx0 = Math.Max(minX, 0), cy0 = Math.Max(minY, 0), cx1 = Math.Min(maxX, grid.Width - 1), cy1 = Math.Min(maxY, grid.Height - 1);
                for (int i = 0; i < grid.TileCount; i++)
                {
                    TileId tile = grid.TileAt(i);
                    grid.GetRect(tile, out int x, out int y, out int w, out int h);
                    bool overlaps = cx0 <= cx1 && cy0 <= cy1 && x <= cx1 && x + w - 1 >= cx0 && y <= cy1 && y + h - 1 >= cy0;
                    if (overlaps) expected.Add(tile);
                }
                Assert.SequenceEqual(expected, grid.TilesIn(minX, minY, maxX, maxY), "round " + round + " grid " + grid + " rect " + minX + "," + minY + ".." + maxX + "," + maxY);
            }
        }

        [Test]
        public void IndicesAndContainment()
        {
            var grid = new TileGrid(100, 70, 32);
            for (int i = 0; i < grid.TileCount; i++)
            {
                TileId tile = grid.TileAt(i);
                Assert.True(grid.Contains(tile));
                Assert.Equal(i, grid.IndexOf(tile));
            }
            Assert.False(grid.Contains(new TileId(4, 0)));
            Assert.False(grid.Contains(new TileId(0, 3)));
            Assert.False(grid.Contains(new TileId(ushort.MaxValue, ushort.MaxValue)));
            Assert.Throws<ArgumentOutOfRangeException>(() => grid.IndexOf(new TileId(4, 0)));
            Assert.Throws<ArgumentOutOfRangeException>(() => grid.TileAt(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => grid.TileAt(grid.TileCount));
        }

        [Test]
        public void ConstructorRejectsInvalidSizes()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new TileGrid(0, 10, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TileGrid(10, -1, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TileGrid(10, 10, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TileGrid(70000, 10, 1), "more than 65536 tiles per axis");
            var largest = new TileGrid(65536, 1, 1);
            Assert.Equal(65536, largest.TilesX);
            Assert.True(largest.Contains(new TileId(ushort.MaxValue, 0)));
            var single = new TileGrid(5, 5, 64);
            Assert.Equal(1, single.TileCount);
            single.GetRect(new TileId(0, 0), out _, out _, out int w, out int h);
            Assert.Equal(5, w);
            Assert.Equal(5, h);
        }

        [Test]
        public void TileIdHasValueSemantics()
        {
            var a = new TileId(3, 4);
            Assert.True(a == new TileId(3, 4));
            Assert.True(a != new TileId(4, 3));
            Assert.True(a.Equals((object)new TileId(3, 4)));
            Assert.False(a.Equals("tile"));
            Assert.Equal(new TileId(3, 4).GetHashCode(), a.GetHashCode());
            Assert.NotEqual(new TileId(4, 3).GetHashCode(), a.GetHashCode());
            Assert.Equal("tile(3, 4)", a.ToString());
            Assert.Equal(default(TileId), new TileId(0, 0));
        }

        [Test]
        public void BrushTexelRectangleOfAStampAtTheMapCentre()
        {
            // a 100 m brush at the centre of the 14336 m map on the 4096² heightmap: texels 2033..2063, plus 2 on each side
            Assert.True(TileGrid.WorldToTexelRect(-50f, -50f, 50f, 50f, -7168f, -7168f, 14336f, 14336f, 4096, 4096, 2,
                                                  out int minX, out int minY, out int maxX, out int maxY));
            Assert.SequenceEqual(new[] { 2031, 2031, 2065, 2065 }, new[] { minX, minY, maxX, maxY });
            Assert.False(TileGrid.WorldToTexelRect(8000f, 0f, 8100f, 50f, -7168f, -7168f, 14336f, 14336f, 4096, 4096, 2, out _, out _, out _, out _),
                         "a stamp right of the playable area writes nothing");
            Assert.False(TileGrid.WorldToTexelRect(0f, -9000f, 50f, -8000f, -7168f, -7168f, 14336f, 14336f, 4096, 4096, 2, out _, out _, out _, out _),
                         "a stamp below the playable area writes nothing");
        }

        [Test]
        public void BrushTexelRectangleMirrorsApplyToTerrain()
        {
            var random = new DeterministicRandom(3790);
            for (int round = 0; round < 5000; round++)
            {
                float cx = (float)(random.NextDouble() * 16000 - 8000), cz = (float)(random.NextDouble() * 16000 - 8000);
                float size = (float)(random.NextDouble() * (round % 10 == 0 ? 3000 : 300));
                float x0 = cx - size, z0 = cz - size, x1 = cx + size, z1 = cz + size;
                int margin = round % 7 == 0 ? 0 : 2;
                bool expected = ReferenceTexelRect(x0, z0, x1, z1, -7168f, -7168f, 14336f, 14336f, 4096, 4096, margin, out int[] rect);
                bool actual = TileGrid.WorldToTexelRect(x0, z0, x1, z1, -7168f, -7168f, 14336f, 14336f, 4096, 4096, margin,
                                                        out int minX, out int minY, out int maxX, out int maxY);
                Assert.Equal(expected, actual, "round " + round);
                if (expected) Assert.SequenceEqual(rect, new[] { minX, minY, maxX, maxY }, "round " + round);
                else Assert.True(maxX < minX && maxY < minY, "an empty rectangle when nothing is written");
            }
            Assert.Throws<ArgumentOutOfRangeException>(() => TileGrid.WorldToTexelRect(0, 0, 1, 1, 0, 0, 1, 1, 0, 10, 2, out _, out _, out _, out _));
            Assert.Throws<ArgumentOutOfRangeException>(() => TileGrid.WorldToTexelRect(0, 0, 1, 1, 0, 0, 1, 1, 10, 10, -1, out _, out _, out _, out _));
        }

        /// <summary>
        /// Transcription of <c>TerrainSystem.ApplyToTerrain</c> (decompiled <c>TerrainSystem.cs:3790-3814</c>) with the
        /// Unity.Mathematics semantics: the rectangle <c>int2 = (x, y, w, h)</c> that the game marks as written.
        /// </summary>
        private static bool ReferenceTexelRect(float minX, float minZ, float maxX, float maxZ, float offsetX, float offsetZ, float sizeX, float sizeZ,
                                               int width, int height, int margin, out int[] rect)
        {
            rect = null;
            float aminx = (minX - offsetX) / sizeX, aminy = (minZ - offsetZ) / sizeZ;
            float amaxx = (maxX - offsetX) / sizeX, amaxy = (maxZ - offsetZ) / sizeZ;
            int ix = (int)UMax((float)Math.Floor(aminx * (float)width), 0f);
            int iy = (int)UMax((float)Math.Floor(aminy * (float)height), 0f);
            int iz = (int)UMin((float)Math.Ceiling(amaxx * (float)width), width - 1);
            int iw = (int)UMin((float)Math.Ceiling(amaxy * (float)height), height - 1);
            int num5 = iz - ix + 1;
            int num6 = iw - iy + 1;
            int x = Math.Max(ix - margin, 0), y = Math.Max(iy - margin, 0), z = num5 + 2 * margin, w = num6 + 2 * margin;
            if (x + z < 0 || x > width || y + w < 0 || y > height || num5 <= 0 || num6 <= 0) return false;
            if (x + z > width) z = width - x;
            if (y + w > height) w = height - y;
            rect = new[] { x, y, x + z - 1, y + w - 1 };
            return true;
        }

        private static float UMin(float x, float y) => float.IsNaN(y) || x < y ? x : y;

        private static float UMax(float x, float y) => float.IsNaN(y) || x > y ? x : y;
    }
}
