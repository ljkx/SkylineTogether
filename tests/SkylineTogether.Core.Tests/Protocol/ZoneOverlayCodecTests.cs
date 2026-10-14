using System;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Protocol;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;
using static SkylineTogether.Core.Tests.Protocol.ProtocolTestUtil;

namespace SkylineTogether.Core.Tests.Protocol
{
    /// <summary>ZoneOverlayCodec (docs/history/ARCHITECTURE-original.md §4.10.7): golden layout, random cell patterns, exactly one encoding per pattern.</summary>
    public sealed class ZoneOverlayCodecTests
    {
        private static byte[] Encode(ushort[] cells, int count)
        {
            var w = new ByteWriter();
            ZoneOverlayCodec.Encode(cells, count, w);
            return w.ToArray();
        }

        private static void Rejected(string hex, int capacity, string why)
        {
            byte[] bytes = Hex(hex);
            Assert.Throws<DecodeException>(() => ZoneOverlayCodec.Decode(Reader(bytes), new ushort[capacity]), why);
        }

        [Test]
        public void LayoutIsPinned()
        {
            // 6 cells | run 3 of zone 3 | run 2 of zone 0 | run 1 of zone 7
            AssertBytes("06 03 03 02 00 01 07", Encode(new ushort[] { 3, 3, 3, 0, 0, 7 }, 6));
            // 60 cells of one zone (a full vanilla 10 × 6 block): one run; a large rank needs a multi-byte varint
            var block = new ushort[60];
            for (int i = 0; i < block.Length; i++) block[i] = 300;
            AssertBytes("3c 3c ac02", Encode(block, 60));
            AssertBytes("00", Encode(new ushort[4], 0), "no cells");
            AssertBytes("02 01 ffff03 01 00", Encode(new ushort[] { 65535, 0, 9 }, 2), "only the first cellCount cells are coded");

            var decoded = new ushort[8];
            Assert.Equal(6, ZoneOverlayCodec.Decode(Reader(Hex("06 03 03 02 00 01 07")), decoded));
            Assert.SequenceEqual(new ushort[] { 3, 3, 3, 0, 0, 7, 0, 0 }, decoded);
        }

        [Test]
        public void RandomCellPatternsRoundTrip()
        {
            var random = new DeterministicRandom(60);
            for (int round = 0; round < 3000; round++)
            {
                int count = round < 100 ? round % 61 : random.Next(0, random.Next(4) == 0 ? 2000 : 61);
                var cells = new ushort[count + random.Next(3)];
                int alphabet = 1 + random.Next(round % 3 == 0 ? 2 : 12);              // few distinct zones: long runs
                bool wide = random.Next(5) == 0;
                for (int i = 0; i < count; i++)
                {
                    int v = random.Next(alphabet);
                    cells[i] = wide ? (ushort)(65535 - v * 4099) : (ushort)v;
                    if (random.Next(3) == 0 && i > 0) cells[i] = cells[i - 1];
                }
                byte[] encoded = Encode(cells, count);
                var decoded = new ushort[count + 1];
                decoded[count] = 0xBEEF;
                var reader = Reader(encoded);
                Assert.Equal(count, ZoneOverlayCodec.Decode(reader, decoded), "round " + round);
                Assert.True(reader.IsAtEnd, "the decoder consumes exactly the encoding");
                for (int i = 0; i < count; i++) Assert.Equal(cells[i], decoded[i], "round " + round + ", cell " + i);
                Assert.Equal((ushort)0xBEEF, decoded[count], "cells beyond the count are untouched");

                // canonical: the encoding of the decoded cells is the same bytes
                Assert.SequenceEqual(encoded, Encode(decoded, count), "round " + round);
            }
        }

        [Test]
        public void NonCanonicalOrOutOfRangeCodingsAreRejected()
        {
            Rejected("03 01 05 02 05", 8, "adjacent runs with the same value");
            Rejected("03 00 05 03 06", 8, "empty run");
            Rejected("03 04 05", 8, "run beyond the cell count");
            Rejected("03 02 05", 8, "runs end before the cell count");
            Rejected("02 02 808004", 8, "value above 65535");
            Rejected("09 09 01", 8, "more cells than the block holds");
            Rejected("8180 04 01 00", 70000, "more than MaxCells cells");
            Rejected("02 02", 8, "truncated value");
            Rejected("02 82", 8, "truncated run varint");
            Rejected("80 00", 8, "overlong cell count varint");
        }

        [Test]
        public void ArgumentsAreValidated()
        {
            var w = new ByteWriter();
            Assert.Throws<ArgumentNullException>(() => ZoneOverlayCodec.Encode(null, 0, w));
            Assert.Throws<ArgumentNullException>(() => ZoneOverlayCodec.Encode(new ushort[1], 1, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => ZoneOverlayCodec.Encode(new ushort[2], 3, w));
            Assert.Throws<ArgumentOutOfRangeException>(() => ZoneOverlayCodec.Encode(new ushort[2], -1, w));
            Assert.Throws<ArgumentOutOfRangeException>(() => ZoneOverlayCodec.Encode(new ushort[ZoneOverlayCodec.MaxCells + 1], ZoneOverlayCodec.MaxCells + 1, w));
            Assert.Equal(0, w.Length);
            Assert.Throws<ArgumentNullException>(() => ZoneOverlayCodec.Decode(null, new ushort[1]));
            Assert.Throws<ArgumentNullException>(() => ZoneOverlayCodec.Decode(Reader(Hex("00")), null));
        }

        [Test]
        public void TheLargestBlockRoundTrips()
        {
            var cells = new ushort[ZoneOverlayCodec.MaxCells];
            for (int i = 0; i < cells.Length; i++) cells[i] = (ushort)i;                // no runs at all: the worst case
            byte[] encoded = Encode(cells, cells.Length);
            var decoded = new ushort[cells.Length];
            Assert.Equal(cells.Length, ZoneOverlayCodec.Decode(Reader(encoded), decoded));
            Assert.SequenceEqual(cells, decoded);
        }
    }
}
