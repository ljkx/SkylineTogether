using System;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Buffers
{
    /// <summary>Round trips and golden bytes of every ByteWriter/ByteReader member (ARCH §4.6).</summary>
    public sealed class ByteWriterReaderTests
    {
        private static ByteReader ReaderOf(ByteWriter w) => new ByteReader(w.Buffer, 0, w.Length);

        private static byte[] Bytes(Action<ByteWriter> write)
        {
            var w = new ByteWriter(1);
            write(w);
            return w.ToArray();
        }

        [Test]
        public void FixedWidthIntegersAreLittleEndian()
        {
            Assert.SequenceEqual(new byte[] { 0x7F }, Bytes(w => w.WriteByte(0x7F)));
            Assert.SequenceEqual(new byte[] { 0x34, 0x12 }, Bytes(w => w.WriteUInt16(0x1234)));
            Assert.SequenceEqual(new byte[] { 0x04, 0x03, 0x02, 0x01 }, Bytes(w => w.WriteUInt32(0x01020304)));
            Assert.SequenceEqual(new byte[] { 0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01 }, Bytes(w => w.WriteUInt64(0x0102030405060708)));
            Assert.SequenceEqual(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, Bytes(w => w.WriteInt32(-1)));
            Assert.SequenceEqual(new byte[] { 0xFE, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, Bytes(w => w.WriteInt64(-2)));
            Assert.SequenceEqual(new byte[] { 0x00, 0x00, 0x80, 0x3F }, Bytes(w => w.WriteSingle(1f)));
            Assert.SequenceEqual(new byte[] { 0, 0, 0, 0, 0, 0, 0xF0, 0x3F }, Bytes(w => w.WriteDouble(1.0)));
            Assert.SequenceEqual(new byte[] { 1, 0 }, Bytes(w => { w.WriteBool(true); w.WriteBool(false); }));
        }

        [Test]
        public void FixedWidthRoundTrips()
        {
            var w = new ByteWriter(0);
            w.WriteByte(0);
            w.WriteByte(255);
            w.WriteUInt16(ushort.MaxValue);
            w.WriteUInt32(uint.MaxValue);
            w.WriteUInt64(ulong.MaxValue);
            w.WriteInt32(int.MinValue);
            w.WriteInt32(int.MaxValue);
            w.WriteInt64(long.MinValue);
            w.WriteInt64(long.MaxValue);
            w.WriteSingle(-0f);
            w.WriteSingle(float.MaxValue);
            w.WriteSingle(float.Epsilon);
            w.WriteSingle(float.NegativeInfinity);
            w.WriteDouble(double.MinValue);
            w.WriteDouble(-0.0);
            w.WriteDouble(Math.PI);
            w.WriteBool(true);
            ByteReader r = ReaderOf(w);
            Assert.Equal((byte)0, r.ReadByte());
            Assert.Equal((byte)255, r.ReadByte());
            Assert.Equal(ushort.MaxValue, r.ReadUInt16());
            Assert.Equal(uint.MaxValue, r.ReadUInt32());
            Assert.Equal(ulong.MaxValue, r.ReadUInt64());
            Assert.Equal(int.MinValue, r.ReadInt32());
            Assert.Equal(int.MaxValue, r.ReadInt32());
            Assert.Equal(long.MinValue, r.ReadInt64());
            Assert.Equal(long.MaxValue, r.ReadInt64());
            float negativeZero = r.ReadSingle();
            Assert.True(negativeZero == 0f && float.IsNegative(negativeZero), "negative zero survives");
            Assert.Equal(float.MaxValue, r.ReadSingle());
            Assert.Equal(float.Epsilon, r.ReadSingle());
            Assert.Equal(float.NegativeInfinity, r.ReadSingle());
            Assert.Equal(double.MinValue, r.ReadDouble());
            Assert.True(double.IsNegative(r.ReadDouble()));
            Assert.Equal(Math.PI, r.ReadDouble());
            Assert.True(r.ReadBool());
            Assert.True(r.IsAtEnd);
            Assert.Equal(0, r.Remaining);
        }

        [Test]
        public void NaNPayloadsArePreserved()
        {
            float nanF = BitConverter.Int32BitsToSingle(0x7FC12345);
            double nanD = BitConverter.Int64BitsToDouble(0x7FF8DEADBEEF0001);
            var w = new ByteWriter();
            w.WriteSingle(nanF);
            w.WriteDouble(nanD);
            ByteReader r = ReaderOf(w);
            Assert.Equal(0x7FC12345, BitConverter.SingleToInt32Bits(r.ReadSingle()));
            Assert.Equal(0x7FF8DEADBEEF0001, BitConverter.DoubleToInt64Bits(r.ReadDouble()));
        }

        [Test]
        public void VarUInt32GoldenBytes()
        {
            Assert.SequenceEqual(new byte[] { 0x00 }, Bytes(w => w.WriteVarUInt32(0)));
            Assert.SequenceEqual(new byte[] { 0x01 }, Bytes(w => w.WriteVarUInt32(1)));
            Assert.SequenceEqual(new byte[] { 0x7F }, Bytes(w => w.WriteVarUInt32(127)));
            Assert.SequenceEqual(new byte[] { 0x80, 0x01 }, Bytes(w => w.WriteVarUInt32(128)));
            Assert.SequenceEqual(new byte[] { 0xAC, 0x02 }, Bytes(w => w.WriteVarUInt32(300)));
            Assert.SequenceEqual(new byte[] { 0xFF, 0x7F }, Bytes(w => w.WriteVarUInt32(16383)));
            Assert.SequenceEqual(new byte[] { 0x80, 0x80, 0x01 }, Bytes(w => w.WriteVarUInt32(16384)));
            Assert.SequenceEqual(new byte[] { 0xFF, 0xFF, 0xFF, 0x07 }, Bytes(w => w.WriteVarUInt32(NetSlot.MaxValue)));
            Assert.SequenceEqual(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0F }, Bytes(w => w.WriteVarUInt32(uint.MaxValue)));
        }

        [Test]
        public void VarUInt64GoldenBytes()
        {
            Assert.SequenceEqual(new byte[] { 0x00 }, Bytes(w => w.WriteVarUInt64(0)));
            Assert.SequenceEqual(new byte[] { 0x80, 0x01 }, Bytes(w => w.WriteVarUInt64(128)));
            Assert.SequenceEqual(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x10 }, Bytes(w => w.WriteVarUInt64(1UL << 32)));
            Assert.SequenceEqual(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 }, Bytes(w => w.WriteVarUInt64(ulong.MaxValue)));
        }

        [Test]
        public void ZigZagGoldenBytes()
        {
            Assert.SequenceEqual(new byte[] { 0x00 }, Bytes(w => w.WriteVarInt32(0)));
            Assert.SequenceEqual(new byte[] { 0x01 }, Bytes(w => w.WriteVarInt32(-1)));
            Assert.SequenceEqual(new byte[] { 0x02 }, Bytes(w => w.WriteVarInt32(1)));
            Assert.SequenceEqual(new byte[] { 0x03 }, Bytes(w => w.WriteVarInt32(-2)));
            Assert.SequenceEqual(new byte[] { 0x7F }, Bytes(w => w.WriteVarInt32(-64)));
            Assert.SequenceEqual(new byte[] { 0x80, 0x01 }, Bytes(w => w.WriteVarInt32(64)));
            Assert.SequenceEqual(new byte[] { 0xFE, 0xFF, 0xFF, 0xFF, 0x0F }, Bytes(w => w.WriteVarInt32(int.MaxValue)));
            Assert.SequenceEqual(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0F }, Bytes(w => w.WriteVarInt32(int.MinValue)));
            Assert.SequenceEqual(new byte[] { 0x01 }, Bytes(w => w.WriteVarInt64(-1)));
            Assert.SequenceEqual(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 }, Bytes(w => w.WriteVarInt64(long.MinValue)));
            Assert.SequenceEqual(new byte[] { 0xFE, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 }, Bytes(w => w.WriteVarInt64(long.MaxValue)));
        }

        [Test]
        public void VarintEdgesRoundTrip()
        {
            uint[] u32 = { 0, 1, 127, 128, 255, 256, 16383, 16384, (1u << 21) - 1, 1u << 21, (1u << 28) - 1, 1u << 28, NetSlot.MaxValue, int.MaxValue, uint.MaxValue - 1, uint.MaxValue };
            ulong[] u64 = { 0, 127, 128, uint.MaxValue, 1UL << 32, (1UL << 35) - 1, 1UL << 56, (1UL << 63) - 1, 1UL << 63, ulong.MaxValue };
            int[] s32 = { 0, 1, -1, 63, -64, 64, -65, 8191, -8192, int.MaxValue, int.MinValue, int.MinValue + 1 };
            long[] s64 = { 0, -1, 1, long.MaxValue, long.MinValue, int.MinValue, (long)int.MaxValue + 1 };
            var w = new ByteWriter();
            foreach (uint v in u32) w.WriteVarUInt32(v);
            foreach (ulong v in u64) w.WriteVarUInt64(v);
            foreach (int v in s32) w.WriteVarInt32(v);
            foreach (long v in s64) w.WriteVarInt64(v);
            ByteReader r = ReaderOf(w);
            foreach (uint v in u32) Assert.Equal(v, r.ReadVarUInt32());
            foreach (ulong v in u64) Assert.Equal(v, r.ReadVarUInt64());
            foreach (int v in s32) Assert.Equal(v, r.ReadVarInt32());
            foreach (long v in s64) Assert.Equal(v, r.ReadVarInt64());
            Assert.True(r.IsAtEnd);
        }

        [Test]
        public void RandomVarintsRoundTrip()
        {
            var random = new DeterministicRandom(7);
            var values = new ulong[2000];
            var w = new ByteWriter();
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = random.NextULong() >> random.Next(64);
                w.WriteVarUInt64(values[i]);
                w.WriteVarInt64(unchecked((long)values[i]));
                w.WriteVarUInt32((uint)values[i]);
                w.WriteVarInt32(unchecked((int)values[i]));
            }
            ByteReader r = ReaderOf(w);
            foreach (ulong v in values)
            {
                Assert.Equal(v, r.ReadVarUInt64());
                Assert.Equal(unchecked((long)v), r.ReadVarInt64());
                Assert.Equal((uint)v, r.ReadVarUInt32());
                Assert.Equal(unchecked((int)v), r.ReadVarInt32());
            }
            Assert.True(r.IsAtEnd);
        }

        [Test]
        public void StringsRoundTripAsLengthPrefixedUtf8()
        {
            Assert.SequenceEqual(new byte[] { 0x00 }, Bytes(w => w.WriteString(string.Empty, 10)));
            Assert.SequenceEqual(new byte[] { 0x00 }, Bytes(w => w.WriteString(null, 10)));
            Assert.SequenceEqual(new byte[] { 0x03, (byte)'a', (byte)'b', (byte)'c' }, Bytes(w => w.WriteString("abc", 3)));
            Assert.SequenceEqual(new byte[] { 0x02, 0xC3, 0xA9 }, Bytes(w => w.WriteString("é", 2)));

            string[] samples = { "", "Skyline Together", "Straße", "東京", "line\nbreak\ttab", "emoji 🏙 city", new string('x', 1000) };
            var writer = new ByteWriter();
            foreach (string s in samples) writer.WriteString(s, 4096);
            ByteReader r = ReaderOf(writer);
            foreach (string s in samples) Assert.Equal(s, r.ReadString(4096));
            Assert.True(r.IsAtEnd);
        }

        [Test]
        public void StringLimitsAreEnforcedOnBothSides()
        {
            var w = new ByteWriter();
            Assert.Throws<ArgumentException>(() => w.WriteString("abcd", 3));
            Assert.Throws<ArgumentException>(() => w.WriteString("éé", 3), "the limit counts UTF-8 bytes, not characters");
            Assert.Equal(0, w.Length);
            w.WriteString("abcdef", 6);
            Assert.Throws<DecodeException>(() => ReaderOf(w).ReadString(5));
            Assert.Equal("abcdef", ReaderOf(w).ReadString(6));
            Assert.Throws<ArgumentOutOfRangeException>(() => w.WriteString("a", -1));
        }

        [Test]
        public void UnpairedSurrogatesBecomeReplacementCharacters()
        {
            var w = new ByteWriter();
            w.WriteString("a\ud800b", 16);
            Assert.SequenceEqual(new byte[] { 0x05, (byte)'a', 0xEF, 0xBF, 0xBD, (byte)'b' }, w.ToArray());
            Assert.Equal("a�b", ReaderOf(w).ReadString(16));
        }

        [Test]
        public void BytesSegmentsAndSkip()
        {
            var w = new ByteWriter();
            w.WriteBytes(new byte[] { 9, 1, 2, 3, 4, 5, 9 }, 1, 5);
            Assert.SequenceEqual(new byte[] { 1, 2, 3, 4, 5 }, w.ToArray());
            ByteReader r = ReaderOf(w);
            var dst = new byte[4];
            r.ReadBytes(dst, 1, 2);
            Assert.SequenceEqual(new byte[] { 0, 1, 2, 0 }, dst);
            ArraySegment<byte> segment = r.ReadSegment(2);
            Assert.Same(w.Buffer, segment.Array);
            Assert.Equal(2, segment.Offset);
            Assert.SequenceEqual(new byte[] { 3, 4 }, segment);
            Assert.Equal((byte)5, r.PeekByte());
            Assert.Equal(4, r.Position);
            r.Skip(1);
            Assert.True(r.IsAtEnd);
            Assert.Throws<ArgumentNullException>(() => w.WriteBytes(null, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => w.WriteBytes(new byte[2], 1, 2));
        }

        [Test]
        public void ReserveAndPatch()
        {
            var w = new ByteWriter();
            w.WriteByte(0xAA);
            int position = w.Reserve(4);
            w.WriteByte(0xBB);
            Assert.Equal(1, position);
            Assert.SequenceEqual(new byte[] { 0xAA, 0, 0, 0, 0, 0xBB }, w.ToArray());
            w.PatchUInt32(position, 0xDDCCBBAA);
            Assert.SequenceEqual(new byte[] { 0xAA, 0xAA, 0xBB, 0xCC, 0xDD, 0xBB }, w.ToArray());
            Assert.Throws<ArgumentOutOfRangeException>(() => w.PatchUInt32(3, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => w.PatchUInt32(-1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => w.Reserve(-1));
        }

        [Test]
        public void ReservedBytesAreZeroEvenAfterReuse()
        {
            var w = new ByteWriter();
            w.WriteUInt64(ulong.MaxValue);
            w.Reset();
            int position = w.Reserve(8);
            Assert.Equal(0, position);
            Assert.SequenceEqual(new byte[8], w.ToArray());
        }

        [Test]
        public void ResetTruncateGrowthAndToArray()
        {
            var w = new ByteWriter(2);
            for (int i = 0; i < 1000; i++) w.WriteUInt32((uint)i);
            Assert.Equal(4000, w.Length);
            Assert.True(w.Capacity >= 4000);
            ByteReader r = ReaderOf(w);
            for (int i = 0; i < 1000; i++) Assert.Equal((uint)i, r.ReadUInt32());
            w.Truncate(8);
            Assert.Equal(8, w.Length);
            Assert.Throws<ArgumentOutOfRangeException>(() => w.Truncate(9));
            byte[] buffer = w.Buffer;
            w.Reset();
            Assert.Equal(0, w.Length);
            Assert.Same(buffer, w.Buffer);
            Assert.Equal(0, w.ToArray().Length);
            Assert.Throws<ArgumentOutOfRangeException>(() => new ByteWriter(-1));
        }

        [Test]
        public void ReaderWindowIsRespected()
        {
            var data = new byte[] { 0xEE, 0x01, 0x02, 0x03, 0xEE };
            var r = new ByteReader(data, 1, 3);
            Assert.Equal(3, r.Remaining);
            Assert.Equal((byte)1, r.ReadByte());
            Assert.Equal(1, r.Position);
            Assert.Equal((ushort)0x0302, r.ReadUInt16());
            Assert.True(r.IsAtEnd);
            Assert.Throws<DecodeException>(() => r.ReadByte());
            r.Reset(data, 4, 1);
            Assert.Equal((byte)0xEE, r.ReadByte());
            Assert.Throws<ArgumentOutOfRangeException>(() => r.Reset(data, 3, 3));
            Assert.Throws<ArgumentNullException>(() => r.Reset(null, 0, 0));
            var empty = new ByteReader();
            Assert.True(empty.IsAtEnd);
            Assert.Throws<DecodeException>(() => empty.ReadByte());
        }
    }
}
