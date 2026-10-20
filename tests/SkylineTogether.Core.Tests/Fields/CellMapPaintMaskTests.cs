using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Fields;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Fields
{
    /// <summary>
    /// Resource paint patches merge only the cells the player painted (the rest of the rectangle is the client's older
    /// copy of the host's map), and groundwater keeps polluted &lt;= amount &lt;= maximum after a merge.
    /// </summary>
    public sealed class CellMapPaintMaskTests
    {
        private static void Put(byte[] map, int at, short v)
        {
            map[at] = (byte)v;
            map[at + 1] = (byte)(v >> 8);
        }

        private static short Get(byte[] map, int at) => (short)(map[at] | (map[at + 1] << 8));

        [Test]
        public void OnlyPaintedCellsReachTheHostsMap()
        {
            var layout = new CellMapLayout(16, 16, 6, 16);
            var fieldMask = new byte[] { 1, 1, 0, 0, 1, 1 }; // amount and max
            var clientMap = new byte[layout.MapBytes];
            for (int c = 0; c < 256; c++) Put(clientMap, c * 6, 100); // the client's older copy of the amounts
            var baseline = (byte[])clientMap.Clone();
            var touched = new bool[256];
            // one stamp over the cells (2..4, 2..4); the brush changed only (3,3)
            for (int y = 2; y <= 4; y++)
            {
                for (int x = 2; x <= 4; x++) touched[y * 16 + x] = true;
            }
            Put(clientMap, (3 * 16 + 3) * 6, 900);
            Put(clientMap, (3 * 16 + 3) * 6 + 4, 900);
            var painted = new byte[CellMapMessage.PaintedMaskBytes(3, 3)];
            Assert.Equal(1, CellMapMessage.MarkPainted(layout, clientMap, baseline, touched, fieldMask, 2, 2, 3, 3, painted));

            var w = new ByteWriter();
            CellMapMessage.WritePatch(w, 9, layout, 2, 2, 3, 3, clientMap, new byte[64 * 64 * 6], painted);
            var r = new ByteReader(w.Buffer, 0, w.Length);
            CellMapMessage.ReadHeader(r);
            byte[] cells = null, bits = null;
            CellMapMessage.ReadPatch(r, layout, out int x0, out int y0, out int pw, out int ph, ref cells, ref bits);
            Assert.True(r.IsAtEnd);

            var hostMap = new byte[layout.MapBytes];
            for (int c = 0; c < 256; c++)
            {
                Put(hostMap, c * 6, 500); // the host's current amounts (newer than the client's copy)
                Put(hostMap, c * 6 + 4, 500);
            }
            Assert.Equal(1, CellMapMessage.MergeMasked(layout, hostMap, x0, y0, pw, ph, cells, fieldMask, bits));
            Assert.Equal((short)900, Get(hostMap, (3 * 16 + 3) * 6), "the painted cell");
            Assert.Equal((short)500, Get(hostMap, (2 * 16 + 2) * 6), "an unpainted cell of the rectangle keeps the host's value");
            Assert.Equal((short)500, Get(hostMap, (4 * 16 + 4) * 6));
        }

        [Test]
        public void APatchWithoutAMaskMergesEveryCellAndStrayBitsAreMalformed()
        {
            var layout = new CellMapLayout(16, 16, 2, 16);
            var map = new byte[layout.MapBytes];
            var w = new ByteWriter();
            CellMapMessage.WritePatch(w, 1, layout, 0, 0, 3, 1, map, new byte[64]); // 3 cells: mask 0b111
            byte[] bytes = w.ToArray();
            Assert.Equal((byte)0x07, bytes[bytes.Length - 1]);
            bytes[bytes.Length - 1] = 0x0F; // a bit beyond the rectangle
            Assert.Throws<DecodeException>(() =>
            {
                var r = new ByteReader(bytes, 0, bytes.Length);
                CellMapMessage.ReadHeader(r);
                byte[] cells = null, bits = null;
                CellMapMessage.ReadPatch(r, layout, out _, out _, out _, out _, ref cells, ref bits);
            });
        }

        [Test]
        public void GroundwaterStaysOrderedAfterAMerge()
        {
            var layout = new CellMapLayout(4, 4, 6, 4);
            var map = new byte[layout.MapBytes];
            // cell 0: amount 800 above max 500, polluted 900 above amount
            Put(map, 0, 800);
            Put(map, 2, 900);
            Put(map, 4, 500);
            // cell 1: already ordered
            Put(map, 6, 300);
            Put(map, 8, 100);
            Put(map, 10, 400);
            Assert.Equal(1, CellMapMessage.EnforceOrder(layout, map, 0, 0, 2, 1, 2, 0, 4));
            Assert.Equal((short)800, Get(map, 0));
            Assert.Equal((short)800, Get(map, 2), "polluted lowered to the amount");
            Assert.Equal((short)800, Get(map, 4), "max raised to the amount");
            Assert.Equal((short)100, Get(map, 8));
            Assert.Equal((short)400, Get(map, 10));
        }
    }
}
