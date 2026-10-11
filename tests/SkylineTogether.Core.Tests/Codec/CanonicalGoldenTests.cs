using System;
using SkylineTogether.Core.Codec;
using SkylineTogether.Core.Tests.Framework;
using static SkylineTogether.Core.Tests.Codec.CodecTestUtil;

namespace SkylineTogether.Core.Tests.Codec
{
    /// <summary>
    /// Golden byte vectors for every member family of the canonical layout (IMPLEMENTATION-PLAN §7.26). Each vector is
    /// checked in both directions: the writer produces exactly these bytes, and the reader decodes them back to the
    /// value and consumes the whole record. The expected bytes follow the stock BinaryWriter layout
    /// (<c>Colossal.Serialization.Entities/BinaryWriter.cs</c>) and ARCH §4.3.3 for references.
    /// </summary>
    public sealed class CanonicalGoldenTests
    {
        private static void Golden(string expectedHex, Action<CanonicalWriter> write, Action<CanonicalReader> read, FakeEntityRefs entities = null)
        {
            entities = entities ?? new FakeEntityRefs();
            Assert.Equal(expectedHex, WriteHex(write, entities), "written bytes");
            CanonicalReader reader = Reader(expectedHex, entities);
            read(reader);
            Assert.True(reader.IsRecordConsumed, "the reader consumed the whole record (position " + reader.Position + " of " + reader.Length + ")");
        }

        [Test]
        public void Booleans()
        {
            Golden("01", w => w.WriteBool(true), r => Assert.True(r.ReadBool()));
            Golden("00", w => w.WriteBool(false), r => Assert.False(r.ReadBool()));
        }

        [Test]
        public void Bytes8()
        {
            Golden("ab", w => w.WriteByte(0xAB), r => Assert.Equal((byte)0xAB, r.ReadByte()));
            Golden("00 ff", w => { w.WriteByte(0); w.WriteByte(255); }, r => { Assert.Equal((byte)0, r.ReadByte()); Assert.Equal((byte)255, r.ReadByte()); });
            Golden("ff 80 7f 00", w => { w.WriteSByte(-1); w.WriteSByte(sbyte.MinValue); w.WriteSByte(sbyte.MaxValue); w.WriteSByte(0); },
                   r =>
                   {
                       Assert.Equal((sbyte)-1, r.ReadSByte());
                       Assert.Equal(sbyte.MinValue, r.ReadSByte());
                       Assert.Equal(sbyte.MaxValue, r.ReadSByte());
                       Assert.Equal((sbyte)0, r.ReadSByte());
                   });
        }

        [Test]
        public void Integers16()
        {
            Golden("fe ff", w => w.WriteShort(-2), r => Assert.Equal((short)-2, r.ReadShort()));
            Golden("00 80", w => w.WriteShort(short.MinValue), r => Assert.Equal(short.MinValue, r.ReadShort()));
            Golden("34 12", w => w.WriteShort(0x1234), r => Assert.Equal((short)0x1234, r.ReadShort()));
            Golden("ef be", w => w.WriteUShort(0xBEEF), r => Assert.Equal((ushort)0xBEEF, r.ReadUShort()));
            Golden("ff ff", w => w.WriteUShort(ushort.MaxValue), r => Assert.Equal(ushort.MaxValue, r.ReadUShort()));
        }

        [Test]
        public void Integers32()
        {
            Golden("78 56 34 12", w => w.WriteInt(0x12345678), r => Assert.Equal(0x12345678, r.ReadInt()));
            Golden("ff ff ff ff", w => w.WriteInt(-1), r => Assert.Equal(-1, r.ReadInt()));
            Golden("00 00 00 80", w => w.WriteInt(int.MinValue), r => Assert.Equal(int.MinValue, r.ReadInt()));
            Golden("ef be ad de", w => w.WriteUInt(0xDEADBEEF), r => Assert.Equal(0xDEADBEEFu, r.ReadUInt()));
            Golden("00 00 00 00", w => w.WriteUInt(0), r => Assert.Equal(0u, r.ReadUInt()));
        }

        [Test]
        public void Integers64()
        {
            Golden("08 07 06 05 04 03 02 01", w => w.WriteLong(0x0102030405060708), r => Assert.Equal(0x0102030405060708L, r.ReadLong()));
            Golden("00 00 00 00 00 00 00 80", w => w.WriteLong(long.MinValue), r => Assert.Equal(long.MinValue, r.ReadLong()));
            Golden("fe ff ff ff ff ff ff ff", w => w.WriteLong(-2), r => Assert.Equal(-2L, r.ReadLong()));
            Golden("ff ff ff ff ff ff ff ff", w => w.WriteULong(ulong.MaxValue), r => Assert.Equal(ulong.MaxValue, r.ReadULong()));
            Golden("01 00 00 00 00 00 00 80", w => w.WriteULong(0x8000000000000001UL), r => Assert.Equal(0x8000000000000001UL, r.ReadULong()));
        }

        [Test]
        public void CharsUseTheStockOneToThreeByteForm()
        {
            // BinaryWriter.Write(char): each UTF-16 code unit on its own (BinaryWriter.cs:275-303)
            (char c, string hex)[] cases =
            {
                ('A', "41"), ('\0', "00"), ('\u007F', "7f"),
                ('\u0080', "c2 80"), ('é', "c3 a9"), ('߿', "df bf"),
                ('ࠀ', "e0 a0 80"), ('€', "e2 82 ac"), ('\uD83D', "ed a0 bd"), ('\uDE80', "ed ba 80"), ('￿', "ef bf bf"),
            };
            foreach ((char c, string hex) in cases)
            {
                Golden(hex, w => w.WriteChar(c), r => Assert.Equal(c, r.ReadChar(), "char U+" + ((int)c).ToString("X4")));
            }
        }

        [Test]
        public void FloatsKeepTheirRawBits()
        {
            Golden("00 00 80 3f", w => w.WriteFloat(1f), r => Assert.Equal(Bits(1f), Bits(r.ReadFloat())));
            Golden("00 00 00 80", w => w.WriteFloat(-0f), r => Assert.Equal(0x80000000u, Bits(r.ReadFloat())));
            Golden("00 00 80 7f", w => w.WriteFloat(float.PositiveInfinity), r => Assert.Equal(float.PositiveInfinity, r.ReadFloat()));
            Golden("01 00 00 00", w => w.WriteFloat(FromBits(1u)), r => Assert.Equal(1u, Bits(r.ReadFloat())), null);
            // quiet NaN with a payload and a signalling NaN: the payload survives the round trip
            Golden("01 00 c0 7f", w => w.WriteFloat(FromBits(0x7FC00001u)), r => Assert.Equal(0x7FC00001u, Bits(r.ReadFloat())));
            Golden("01 00 80 7f", w => w.WriteFloat(FromBits(0x7F800001u)), r => Assert.Equal(0x7F800001u, Bits(r.ReadFloat())));
            Golden("ff ff ff ff", w => w.WriteFloat(FromBits(0xFFFFFFFFu)), r => Assert.Equal(0xFFFFFFFFu, Bits(r.ReadFloat())));
        }

        [Test]
        public void DoublesKeepTheirRawBits()
        {
            Golden("00 00 00 00 00 00 f0 3f", w => w.WriteDouble(1.0), r => Assert.Equal(1.0, r.ReadDouble()));
            Golden("00 00 00 00 00 00 04 c0", w => w.WriteDouble(-2.5), r => Assert.Equal(-2.5, r.ReadDouble()));
            Golden("00 00 00 00 00 00 00 80", w => w.WriteDouble(-0.0), r => Assert.Equal(0x8000000000000000UL, Bits(r.ReadDouble())));
            Golden("23 01 00 00 00 00 f8 7f", w => w.WriteDouble(FromBits(0x7FF8000000000123UL)), r => Assert.Equal(0x7FF8000000000123UL, Bits(r.ReadDouble())));
        }

        [Test]
        public void Strings()
        {
            Golden("00 00 00 00", w => w.WriteString(string.Empty), r => Assert.Equal(string.Empty, r.ReadString()));
            Golden("00 00 00 00", w => w.WriteString(null), r => Assert.Equal(string.Empty, r.ReadString(), "null reads back as empty"));
            Golden("02 00 00 00 48 69", w => w.WriteString("Hi"), r => Assert.Equal("Hi", r.ReadString()));
            // 11 UTF-16 code units: the rocket is a surrogate pair, each half encoded on its own (CESU-8 style)
            const string mixed = "Grüße 東京 🚀";
            Golden("0b 00 00 00 47 72 c3 bc c3 9f 65 20 e6 9d b1 e4 ba ac 20 ed a0 bd ed ba 80",
                   w => w.WriteString(mixed), r => Assert.Equal(mixed, r.ReadString()));
            // a lone surrogate survives as well
            Golden("02 00 00 00 ed b0 80 41", w => w.WriteString("\uDC00A"), r => Assert.Equal("\uDC00A", r.ReadString()));
        }

        [Test]
        public void FloatVectors()
        {
            Golden("00 00 80 3f 00 00 00 40", w => w.WriteFloat2(1, 2), r =>
            {
                r.ReadFloat2(out float x, out float y);
                Assert.Equal(1f, x); Assert.Equal(2f, y);
            });
            Golden("00 00 80 3f 00 00 00 40 00 00 80 bf", w => w.WriteFloat3(1, 2, -1), r =>
            {
                r.ReadFloat3(out float x, out float y, out float z);
                Assert.Equal(1f, x); Assert.Equal(2f, y); Assert.Equal(-1f, z);
            });
            Golden("00 00 00 00 00 00 80 3f 00 00 00 40 00 00 40 40", w => w.WriteFloat4(0, 1, 2, 3), r =>
            {
                r.ReadFloat4(out float x, out float y, out float z, out float v);
                Assert.Equal(0f, x); Assert.Equal(1f, y); Assert.Equal(2f, z); Assert.Equal(3f, v);
            });
        }

        [Test]
        public void IntegerVectors()
        {
            Golden("01 00 00 00 ff ff ff ff", w => w.WriteInt2(1, -1), r =>
            {
                r.ReadInt2(out int x, out int y);
                Assert.Equal(1, x); Assert.Equal(-1, y);
            });
            Golden("01 00 00 00 02 00 00 00 03 00 00 00", w => w.WriteInt3(1, 2, 3), r =>
            {
                r.ReadInt3(out int x, out int y, out int z);
                Assert.Equal(1, x); Assert.Equal(2, y); Assert.Equal(3, z);
            });
            Golden("ff ff ff 7f 00 00 00 80 00 00 00 00 05 00 00 00", w => w.WriteInt4(int.MaxValue, int.MinValue, 0, 5), r =>
            {
                r.ReadInt4(out int x, out int y, out int z, out int v);
                Assert.Equal(int.MaxValue, x); Assert.Equal(int.MinValue, y); Assert.Equal(0, z); Assert.Equal(5, v);
            });
            Golden("01 00 00 00 02 00 00 00 ff ff ff ff 00 00 00 80", w => w.WriteUInt4(1, 2, 0xFFFFFFFF, 0x80000000), r =>
            {
                r.ReadUInt4(out uint x, out uint y, out uint z, out uint v);
                Assert.Equal(1u, x); Assert.Equal(2u, y); Assert.Equal(0xFFFFFFFFu, z); Assert.Equal(0x80000000u, v);
            });
        }

        [Test]
        public void BoolVectorsArePackedIntoOneByte()
        {
            // BinaryWriter.cs:243-260: x = 1, y = 2, z = 4, w = 8
            for (int bits = 0; bits < 4; bits++)
            {
                bool x = (bits & 1) != 0, y = (bits & 2) != 0;
                Golden(bits.ToString("x2"), w => w.WriteBool2(x, y), r =>
                {
                    r.ReadBool2(out bool rx, out bool ry);
                    Assert.Equal(x, rx); Assert.Equal(y, ry);
                });
            }
            for (int bits = 0; bits < 8; bits++)
            {
                bool x = (bits & 1) != 0, y = (bits & 2) != 0, z = (bits & 4) != 0;
                Golden(bits.ToString("x2"), w => w.WriteBool3(x, y, z), r =>
                {
                    r.ReadBool3(out bool rx, out bool ry, out bool rz);
                    Assert.Equal(x, rx); Assert.Equal(y, ry); Assert.Equal(z, rz);
                });
            }
            for (int bits = 0; bits < 16; bits++)
            {
                bool x = (bits & 1) != 0, y = (bits & 2) != 0, z = (bits & 4) != 0, v = (bits & 8) != 0;
                Golden(bits.ToString("x2"), w => w.WriteBool4(x, y, z, v), r =>
                {
                    r.ReadBool4(out bool rx, out bool ry, out bool rz, out bool rv);
                    Assert.Equal(x, rx); Assert.Equal(y, ry); Assert.Equal(z, rz); Assert.Equal(v, rv);
                });
            }
        }

        [Test]
        public void QuaternionColorAndHash()
        {
            Golden("00 00 00 00 00 00 00 00 00 00 00 00 00 00 80 3f", w => w.WriteQuaternion(0, 0, 0, 1), r =>
            {
                r.ReadQuaternion(out float x, out float y, out float z, out float v);
                Assert.Equal(0f, x); Assert.Equal(0f, y); Assert.Equal(0f, z); Assert.Equal(1f, v);
            });
            Golden("00 00 80 3f 00 00 00 3f 00 00 80 3e 00 00 00 00", w => w.WriteColor(1f, 0.5f, 0.25f, 0f), r =>
            {
                r.ReadColor(out float red, out float green, out float blue, out float alpha);
                Assert.Equal(1f, red); Assert.Equal(0.5f, green); Assert.Equal(0.25f, blue); Assert.Equal(0f, alpha);
            });
            Golden("ff 00 80 01", w => w.WriteColor32(255, 0, 128, 1), r =>
            {
                r.ReadColor32(out byte red, out byte green, out byte blue, out byte alpha);
                Assert.Equal((byte)255, red); Assert.Equal((byte)0, green); Assert.Equal((byte)128, blue); Assert.Equal((byte)1, alpha);
            });
            Golden("44 33 22 11 88 77 66 55 cc bb aa 99 00 ff ee dd", w => w.WriteHash128(0x11223344, 0x55667788, 0x99AABBCC, 0xDDEEFF00), r =>
            {
                r.ReadHash128(out uint a, out uint b, out uint c, out uint d);
                Assert.Equal(0x11223344u, a); Assert.Equal(0x55667788u, b); Assert.Equal(0x99AABBCCu, c); Assert.Equal(0xDDEEFF00u, d);
            });
        }

        [Test]
        public void BezierIsTwelveFloats()
        {
            var curve = new float[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
            Golden("00 00 80 3f 00 00 00 40 00 00 40 40 00 00 80 40 00 00 a0 40 00 00 c0 40 "
                   + "00 00 e0 40 00 00 00 41 00 00 10 41 00 00 20 41 00 00 30 41 00 00 40 41",
                   w => w.WriteBezier4x3(curve), r =>
                   {
                       var back = new float[12];
                       r.ReadBezier4x3(back);
                       Assert.SequenceEqual(curve, back);
                   });
        }

        [Test]
        public void EntitiesAreWrittenAsNetRefs()
        {
            var entities = new FakeEntityRefs()
                .BindSlot(10, 3, 1)
                .Bind(500, 1, NetRef.ForPrefab(new PrefabRank(7)));
            Golden("05", w => w.WriteEntity(10, 3), r => { Assert.True(r.ReadEntity(out int i, out int v)); Assert.Equal(10, i); Assert.Equal(3, v); }, entities);
            Golden("00", w => w.WriteEntity(0, 0), r => { Assert.True(r.ReadEntity(out int i, out int v)); Assert.Equal(0, i); Assert.Equal(0, v); }, entities);
            Golden("04", w => w.WriteEntity(99, 1), r => { Assert.True(r.ReadEntity(out int i, out int v)); Assert.Equal(0, i); Assert.Equal(0, v); }, entities);
            Golden("1e", w => w.WriteEntity(500, 1), r => { Assert.True(r.ReadEntity(out int i, out int v)); Assert.Equal(500, i); Assert.Equal(1, v); }, entities);
            // ignoreVersion (PathNode): resolved by index only, read back like any reference
            Golden("05", w => w.WriteEntityIgnoreVersion(10), r => { Assert.True(r.ReadEntity(out int i, out _)); Assert.Equal(10, i); }, entities);
            Assert.Contains((10, 0, true), entities.EncodeCalls);
        }

        [Test]
        public void EntityArraysHaveNoLengthPrefix()
        {
            var entities = new FakeEntityRefs().BindSlot(10, 3, 1).BindSlot(11, 1, 40);
            Golden("05 00 04 a1 01", w => w.WriteEntityArray(new[] { 10, 0, 99, 11 }, new[] { 3, 0, 1, 1 }, 4), r =>
            {
                var indices = new int[4];
                var versions = new int[4];
                Assert.True(r.ReadEntityArray(indices, versions, 4));
                Assert.SequenceEqual(new[] { 10, 0, 0, 11 }, indices);
                Assert.SequenceEqual(new[] { 3, 0, 0, 1 }, versions);
            }, entities);
        }

        [Test]
        public void EntityListsAreACountThenTheReferences()
        {
            // IWriter.Write(NativeList<Entity>) writes the count, then each reference (BinaryWriter.cs:59-66); NetWriter
            // forwards WriteCount + WriteEntityArray, NetReader ReadCount(1) + ReadEntityArray (BinaryReader.cs:123-135)
            var entities = new FakeEntityRefs().BindSlot(10, 3, 1).BindSlot(11, 1, 40);
            Golden("03 00 00 00 05 a1 01 00", w =>
            {
                w.WriteCount(3);
                w.WriteEntityArray(new[] { 10, 11, 0 }, new[] { 3, 1, 0 }, 3);
            }, r =>
            {
                int n = r.ReadCount(1);
                Assert.Equal(3, n);
                var indices = new int[n];
                var versions = new int[n];
                Assert.True(r.ReadEntityArray(indices, versions, n));
                Assert.SequenceEqual(new[] { 10, 11, 0 }, indices);
                Assert.SequenceEqual(new[] { 3, 1, 0 }, versions);
            }, entities);
            Golden("00 00 00 00", w => { w.WriteCount(0); w.WriteEntityArray(null, null, 0); }, r =>
            {
                Assert.Equal(0, r.ReadCount(1));
                Assert.True(r.ReadEntityArray(null, null, 0));
            }, entities);
        }

        [Test]
        public void SkipConsumesExactlyTheGivenBytes()
        {
            // IReader.Skip(size) (BinaryReader.cs:405-408); bounded here
            Golden("07 aa bb cc 08", w =>
            {
                w.WriteByte(7);
                w.WriteByteArray(new byte[] { 0xAA, 0xBB, 0xCC }, 3);
                w.WriteByte(8);
            }, r =>
            {
                Assert.Equal((byte)7, r.ReadByte());
                r.Skip(3);
                Assert.Equal((byte)8, r.ReadByte());
            });
        }

        [Test]
        public void PrimitiveArraysHaveNoLengthPrefix()
        {
            Golden("01 00 00 00 ff ff ff ff 04 03 02 01", w => w.WriteIntArray(new[] { 1, -1, 0x01020304 }, 3), r =>
            {
                var back = new int[3];
                r.ReadIntArray(back, 3);
                Assert.SequenceEqual(new[] { 1, -1, 0x01020304 }, back);
            });
            Golden("01 00 00 00", w => w.WriteIntArray(new[] { 1, 2, 3 }, 1), r => { var back = new int[1]; r.ReadIntArray(back, 1); Assert.Equal(1, back[0]); });
            Golden(string.Empty, w => w.WriteIntArray(null, 0), r => r.ReadIntArray(null, 0));
            Golden("01 00 cd ab", w => w.WriteUShortArray(new ushort[] { 1, 0xABCD }, 2), r =>
            {
                var back = new ushort[2];
                r.ReadUShortArray(back, 2);
                Assert.SequenceEqual(new ushort[] { 1, 0xABCD }, back);
            });
            Golden("01 02 03", w => w.WriteByteArray(new byte[] { 1, 2, 3 }, 3), r =>
            {
                var back = new byte[3];
                r.ReadByteArray(back, 3);
                Assert.SequenceEqual(new byte[] { 1, 2, 3 }, back);
            });
            Golden("00 00 80 3f 00 00 00 40 00 00 40 40 00 00 80 40", w => w.WriteFloat2Array(new float[] { 1, 2, 3, 4 }, 2), r =>
            {
                var back = new float[4];
                r.ReadFloat2Array(back, 2);
                Assert.SequenceEqual(new float[] { 1, 2, 3, 4 }, back);
            });
            Golden("00 00 80 3f 00 00 00 40 00 00 40 40 00 00 80 40", w => w.WriteFloat4Array(new float[] { 1, 2, 3, 4, 99 }, 1), r =>
            {
                var back = new float[4];
                r.ReadFloat4Array(back, 1);
                Assert.SequenceEqual(new float[] { 1, 2, 3, 4 }, back);
            });
            Golden("01 00 00 00 02 00 00 00 03 00 00 00 04 00 00 00", w => w.WriteInt2Array(new[] { 1, 2, 3, 4 }, 2), r =>
            {
                var back = new int[4];
                r.ReadInt2Array(back, 2);
                Assert.SequenceEqual(new[] { 1, 2, 3, 4 }, back);
            });
        }

        [Test]
        public void FloatArraysKeepNaNPayloads()
        {
            float nan = FromBits(0x7FC0BEEFu);
            float snan = FromBits(0x7F800DEDu);
            Golden("ef be c0 7f ed 0d 80 7f", w => w.WriteFloat2Array(new[] { nan, snan }, 1), r =>
            {
                var back = new float[2];
                r.ReadFloat2Array(back, 1);
                Assert.Equal(0x7FC0BEEFu, Bits(back[0]));
                Assert.Equal(0x7F800DEDu, Bits(back[1]));
            });
        }

        [Test]
        public void StridedByteArraysAreTransposed()
        {
            byte[] column = { 0x10, 0x11, 0x20, 0x21, 0x30, 0x31 };
            Golden("10 20 30 11 21 31", w => w.WriteByteArray(column, 6, 2), r =>
            {
                var back = new byte[6];
                r.ReadByteArray(back, 6, 2);
                Assert.SequenceEqual(column, back);
            });
            byte[] withTail = { 0x10, 0x11, 0x20, 0x21, 0x30, 0x31, 0x40 };
            Golden("10 20 30 11 21 31 40", w => w.WriteByteArray(withTail, 7, 2), r =>
            {
                var back = new byte[7];
                r.ReadByteArray(back, 7, 2);
                Assert.SequenceEqual(withTail, back);
            });
            byte[] quads = { 1, 2, 3, 4, 5, 6, 7, 8 };
            Golden("01 05 02 06 03 07 04 08", w => w.WriteByteArray(quads, 8, 4), r =>
            {
                var back = new byte[8];
                r.ReadByteArray(back, 8, 4);
                Assert.SequenceEqual(quads, back);
            });
            // fewer than two whole elements, stride 1 or below: unchanged
            Golden("01 02 03 04 05", w => w.WriteByteArray(quads, 5, 3), r => { var back = new byte[5]; r.ReadByteArray(back, 5, 3); Assert.SequenceEqual(new byte[] { 1, 2, 3, 4, 5 }, back); });
            Golden("01 02 03", w => w.WriteByteArray(quads, 3, 1), r => { var back = new byte[3]; r.ReadByteArray(back, 3, 1); Assert.SequenceEqual(new byte[] { 1, 2, 3 }, back); });
            Golden("01 02 03", w => w.WriteByteArray(quads, 3, 0), r => { var back = new byte[3]; r.ReadByteArray(back, 3, 0); Assert.SequenceEqual(new byte[] { 1, 2, 3 }, back); });
        }

        [Test]
        public void CountsAreLittleEndianInts()
        {
            // three one-byte elements follow, so the count fits the remaining bytes
            Golden("03 00 00 00 0a 0b 0c", w => { w.WriteCount(3); w.WriteByte(10); w.WriteByte(11); w.WriteByte(12); }, r =>
            {
                Assert.Equal(3, r.ReadCount());
                Assert.Equal((byte)10, r.ReadByte());
                Assert.Equal((byte)11, r.ReadByte());
                Assert.Equal((byte)12, r.ReadByte());
            });
            Golden("00 00 00 00", w => w.WriteCount(0), r => Assert.Equal(0, r.ReadCount()));
            // a NativeList<int> forwarder: count, then the ints
            Golden("02 00 00 00 07 00 00 00 08 00 00 00", w => { w.WriteCount(2); w.WriteIntArray(new[] { 7, 8 }, 2); }, r =>
            {
                int n = r.ReadCount(4);
                var back = new int[n];
                r.ReadIntArray(back, n);
                Assert.SequenceEqual(new[] { 7, 8 }, back);
            });
        }

        [Test]
        public void Blocks()
        {
            Golden("01 00 00 00 07", w =>
            {
                BlockToken block = w.Begin();
                w.WriteByte(7);
                Assert.True(w.End(block, out int size));
                Assert.Equal(1, size);
            }, r =>
            {
                BlockToken block = r.Begin(out int size);
                Assert.Equal(1, size);
                Assert.Equal((byte)7, r.ReadByte());
                Assert.True(r.End(block));
            });
            Golden("00 00 00 00", w => Assert.False(w.End(w.Begin())), r => Assert.True(r.End(r.Begin()), "an empty block is consumed by definition"));
            // nested: A { 01, B { 02 00 } } -> sizes 7 and 2 (BinaryWriter.cs:27-49)
            Golden("07 00 00 00 01 02 00 00 00 02 00", w =>
            {
                BlockToken a = w.Begin();
                w.WriteByte(1);
                BlockToken b = w.Begin();
                w.WriteShort(2);
                Assert.True(w.End(b, out int sizeB));
                Assert.True(w.End(a, out int sizeA));
                Assert.Equal(2, sizeB);
                Assert.Equal(7, sizeA);
            }, r =>
            {
                BlockToken a = r.Begin(out int sizeA);
                Assert.Equal(7, sizeA);
                Assert.Equal((byte)1, r.ReadByte());
                BlockToken b = r.Begin(out int sizeB);
                Assert.Equal(2, sizeB);
                Assert.Equal((short)2, r.ReadShort());
                Assert.True(r.End(b));
                Assert.True(r.End(a));
            });
        }
    }
}
